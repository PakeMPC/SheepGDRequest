using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SheepGDEditor.Services;

/// <summary>
/// Servidor web local, mínimo, para ver el tema en el navegador mientras se diseña
/// (botón "Vista previa" del Editor).
///
/// Hace dos cosas:
///   1. Sirve por HTTP los archivos del tema (index.html, engine.js, theme.json, assets/...)
///      desde la carpeta donde el Editor vuelca el tema actual.
///   2. Hace de "bot" de mentira por WebSocket en la ruta /ws: envía a la página los datos de
///      demostración que el Editor va rotando, igual que SheepGDRequest envía los reales a OBS.
///
/// Por qué un servidor y no abrir el index.html directamente (file://):
/// engine.js empieza pidiendo el tema con fetch('theme.json'), y los navegadores bloquean esa
/// petición cuando la página se abre desde un archivo local. engine.js atrapa el error, así que
/// lo único que se vería es una página en blanco sin ningún aviso. Servido por http:// el
/// escenario es el mismo que en el navegador de OBS, y la vista previa muestra el index.html
/// REAL del tema, no una copia aparte que con el tiempo acabaría siendo distinta.
///
/// Usa HttpListener, que viene con .NET: no añade ninguna dependencia al Editor, que evita los
/// paquetes externos a propósito.
///
/// Es un singleton (<see cref="Instance"/>): solo hay un servidor de vista previa por Editor.
/// </summary>
public sealed class PreviewServerService
{
    private static PreviewServerService? _instance;

    /// <summary>La única instancia del servidor. Se crea la primera vez que se pide.</summary>
    public static PreviewServerService Instance => _instance ??= new PreviewServerService();

    private PreviewServerService() { }

    // El servidor HTTP mientras está encendido, y la señal para pararlo junto con todas las
    // tareas que dependen de él.
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    // Las pestañas del navegador conectadas por WebSocket. Se tocan desde varias tareas a la
    // vez (cada conexión corre en la suya), así que todo acceso pasa por el candado.
    private readonly List<WebSocket> _sockets = new();
    private readonly object _socketsLock = new();

    // Un único candado de envío para todos los sockets. Dos SendAsync solapados sobre el mismo
    // WebSocket lanzan una excepción, y aquí el tráfico es mínimo (un mensaje cada 3 segundos),
    // así que no compensa tener un candado por socket.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // De dónde se sirven los archivos: la carpeta del tema volcado y, como respaldo, la carpeta
    // Assets del propio Editor (ver ResolveFile).
    private string _themeRoot = "";
    private string _assetsRoot = "";

    /// <summary>Puerto en el que escucha el servidor, o 0 si está apagado.</summary>
    public int Port { get; private set; }

    /// <summary>Dirección de la vista previa, la que se abre en el navegador.</summary>
    public string BaseUrl => $"http://localhost:{Port}/";

    /// <summary>Si el servidor está encendido y aceptando conexiones.</summary>
    public bool IsRunning => _listener is { IsListening: true };

    /// <summary>
    /// Función que devuelve, en JSON, el estado de demostración actual (el nivel que se está
    /// "jugando", la cola...). La pone el ViewModel: el servidor no sabe nada de niveles ni de
    /// temas, solo pide el mensaje y lo reparte.
    /// </summary>
    public Func<string>? StateProvider { get; set; }

    /// <summary>
    /// Lo mismo que <see cref="StateProvider"/>, pero para las notificaciones. Hace falta aparte
    /// porque una notificación es un EVENTO, no un estado: si la pestaña se abre o se recarga
    /// entre dos notificaciones, se quedaría con esa parte vacía hasta la siguiente. Con esto,
    /// cada pestaña que conecta recibe una nada más entrar, igual que recibe el estado.
    /// </summary>
    public Func<string>? NotificationProvider { get; set; }

    /// <summary>
    /// Arranca el servidor y devuelve su dirección. Si ya había uno encendido, lo apaga antes.
    /// </summary>
    /// <param name="themeRoot">Carpeta con el tema volcado, igual que dentro del ZIP.</param>
    /// <param name="assetsRoot">Carpeta Assets del Editor (imágenes de demostración).</param>
    /// <exception cref="IOException">Si no hay puerto libre entre el 7970 y el 7999.</exception>
    public string Start(string themeRoot, string assetsRoot)
    {
        Stop();

        _themeRoot = themeRoot;
        _assetsRoot = assetsRoot;
        _cts = new CancellationTokenSource();

        // Se busca un puerto libre a partir del 7970. El 6969 queda fuera a propósito: es el de
        // WidgetServerService, el servidor de SheepGDRequest, que puede estar abierto mientras
        // se diseña.
        for (int port = 7970; port < 8000; port++)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) { /* puerto ocupado: se prueba el siguiente */ }
            catch (SocketException) { /* lo mismo, según el sistema operativo */ }
        }

        if (_listener == null)
            throw new IOException(EditorI18n.Trf("UI_PreviewNoFreePort", 7970, 7999));

        // El bucle que atiende las peticiones corre en segundo plano; Start vuelve enseguida.
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return BaseUrl;
    }

    /// <summary>
    /// Apaga el servidor: cierra todas las pestañas conectadas y deja de escuchar. Se puede
    /// llamar aunque ya esté apagado. No lanza excepciones: se usa también al cerrar el Editor.
    /// </summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }

        lock (_socketsLock)
        {
            foreach (var ws in _sockets) { try { ws.Abort(); ws.Dispose(); } catch { } }
            _sockets.Clear();
        }

        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
        _cts = null;
        Port = 0;
    }

    /// <summary>
    /// Envía un mensaje a todas las pestañas de vista previa abiertas. Si alguna falla, se
    /// sigue con las demás.
    /// </summary>
    public async Task BroadcastAsync(string json)
    {
        if (string.IsNullOrEmpty(json)) return;

        // Se trabaja sobre una copia de la lista para no tener el candado cogido durante los
        // envíos, que son asíncronos.
        List<WebSocket> copia;
        lock (_socketsLock) copia = new List<WebSocket>(_sockets);

        foreach (var ws in copia)
        {
            if (ws.State != WebSocketState.Open) continue;
            try { await SendAsync(ws, json, CancellationToken.None); } catch { }
        }
    }

    // ==========================================
    // BUCLE DE ACEPTACIÓN
    // ==========================================

    /// <summary>
    /// Espera peticiones mientras el servidor esté encendido y atiende cada una en su propia
    /// tarea, para que un vídeo grande o un WebSocket abierto no bloqueen a las demás.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }   // el servidor se apagó mientras esperaba: se sale sin ruido
            _ = Task.Run(() => HandleAsync(ctx, ct));
        }
    }

    /// <summary>
    /// Atiende una petición: /ws abre el WebSocket, "/" es el index.html, y cualquier otra
    /// ruta es un archivo del tema (o, si no está ahí, de la carpeta Assets del Editor).
    /// Un fallo en una petición solo corta esa respuesta.
    /// </summary>
    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
            {
                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    return;
                }
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                await HandleSocketAsync(wsCtx.WebSocket, ct);
                return;
            }

            if (path == "/") path = "/index.html";

            // El navegador pide el icono de la pestaña por su cuenta. El tema no lo tiene ni
            // tiene por qué: un 204 (sin contenido) lo deja tranquilo y evita un 404 que solo
            // estorba cuando se buscan los 404 de verdad. OBS no lo pide, porque no tiene pestañas.
            if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            string? file = ResolveFile(path);
            if (file == null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            await ServeFileAsync(ctx, file, ct);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    // ==========================================
    // ARCHIVOS
    // ==========================================

    /// <summary>
    /// Traduce la ruta pedida por el navegador a un archivo del disco, o null si no existe o
    /// si cae fuera de las carpetas permitidas.
    /// </summary>
    private string? ResolveFile(string urlPath)
    {
        string rel = Uri.UnescapeDataString(urlPath.TrimStart('/'))
            .Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(rel)) return null;

        // 1) Primero se busca en el TEMA.
        //
        //    No se decide por el nombre de la carpeta, aunque lo parezca: el Editor tiene
        //    "Assets" y el tema "assets", y en Windows esos dos nombres son el mismo. Comparando
        //    sin distinguir mayúsculas, /assets/star.png del tema se iba a buscar a la carpeta
        //    del Editor y devolvía 404 con el archivo ahí mismo. Por eso se decide por dónde
        //    EXISTE el archivo, que no admite dudas.
        string? enTema = Buscar(_themeRoot, rel);
        if (enTema != null) return enTema;

        // 2) Si no está en el tema, es una de las imágenes de demostración del Editor (caras
        //    de dificultad, iconos de plataforma...). Los datos de demostración las nombran con
        //    rutas que empiezan por "Assets/", y esa parte sobra dentro de la carpeta Assets.
        string relEditor = rel;
        string prefijo = "Assets" + Path.DirectorySeparatorChar;
        if (relEditor.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase))
            relEditor = relEditor.Substring(prefijo.Length);

        return Buscar(_assetsRoot, relEditor);

        // Protección contra rutas con "..": el archivo final tiene que quedar DENTRO de la
        // carpeta raíz. Si no, una petición como /../../archivo saldría de ella.
        static string? Buscar(string root, string rel)
        {
            if (string.IsNullOrEmpty(root)) return null;

            string rootFull = Path.GetFullPath(root);
            string full = Path.GetFullPath(Path.Combine(rootFull, rel));

            if (full != rootFull &&
                !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;

            return File.Exists(full) ? full : null;
        }
    }

    /// <summary>
    /// Envía un archivo al navegador. Atiende las peticiones parciales (cabecera Range), que
    /// son las que usan los vídeos, y lo lee por trozos en lugar de cargarlo entero en memoria.
    /// </summary>
    private static async Task ServeFileAsync(HttpListenerContext ctx, string file, CancellationToken ct)
    {
        // FileShare.ReadWrite: se abre sin impedir que otros lo lean o lo escriban a la vez.
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long total = fs.Length;

        ctx.Response.ContentType = GetMimeType(Path.GetExtension(file));
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        // Sin caché: si no, al reabrir la vista previa el navegador enseñaría el tema anterior.
        ctx.Response.Headers["Cache-Control"] = "no-store";

        // Por defecto se envía el archivo entero.
        long desde = 0, hasta = total - 1;
        string? range = ctx.Request.Headers["Range"];

        // Chrome pide los <video> por partes ("Range: bytes=inicio-fin"). Si no se le contesta
        // con un 206 y solo esa parte, el vídeo puede no arrancar, y el vídeo es justo lo que se
        // quiere ver en la vista previa. Las tres formas: "inicio-fin", "inicio-" (hasta el
        // final) y "-N", que son los últimos N bytes (sin inicio, el número dice CUÁNTOS, no
        // hasta dónde; antes se leía como un fin y se servía desde el principio del archivo).
        if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var partes = range.Substring(6).Split('-');
            if (partes.Length == 2)
            {
                bool hayInicio = long.TryParse(partes[0], out long d);
                bool hayFin = long.TryParse(partes[1], out long h);
                if (hayInicio)
                {
                    desde = d;
                    if (hayFin) hasta = h;
                }
                else if (hayFin)
                {
                    desde = total - h;   // los últimos h bytes
                }
            }

            // Un rango imposible no rompe nada: se ajusta a los límites del archivo, y si aun
            // así no tiene sentido, se envía el archivo entero.
            if (desde < 0) desde = 0;
            if (hasta >= total) hasta = total - 1;
            if (desde > hasta) { desde = 0; hasta = total - 1; }

            ctx.Response.StatusCode = 206;
            ctx.Response.Headers["Content-Range"] = $"bytes {desde}-{hasta}/{total}";
        }

        long largo = hasta - desde + 1;
        ctx.Response.ContentLength64 = largo;

        fs.Seek(desde, SeekOrigin.Begin);

        // Por trozos de 80 KB: un vídeo de fondo de cientos de MB no se carga entero en memoria.
        var buffer = new byte[81920];
        long restante = largo;
        while (restante > 0)
        {
            int leidos = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, restante)), ct);
            if (leidos <= 0) break;
            await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, leidos), ct);
            restante -= leidos;
        }

        ctx.Response.Close();
    }

    // ==========================================
    // WEBSOCKET
    // ==========================================

    /// <summary>
    /// Mantiene viva la conexión de una pestaña hasta que se cierre o se apague el servidor.
    /// Al conectar le envía el estado y una notificación, y después contesta con el estado
    /// cada vez que la página lo pide.
    /// </summary>
    private async Task HandleSocketAsync(WebSocket ws, CancellationToken ct)
    {
        lock (_socketsLock) _sockets.Add(ws);

        try
        {
            // engine.js pide el estado ({"event":"request_state"}) nada más conectar. Se le
            // contesta ya, sin esperar al siguiente envío periódico de 3 segundos, para que la
            // primera pantalla no salga vacía.
            await SendAsync(ws, StateProvider?.Invoke() ?? "", ct);

            // Y una notificación de estreno, por lo mismo. El estado se reenvía en cada ciclo,
            // pero una notificación emitida antes de que existiera esta pestaña no vuelve nunca:
            // sin esto, al abrir la vista previa habría que esperar un ciclo entero para verla.
            await SendAsync(ws, NotificationProvider?.Invoke() ?? "", ct);

            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (res.MessageType == WebSocketMessageType.Close) break;

                // Lo único que envía engine.js es request_state, así que ni se mira el
                // contenido: se responde con el estado actual.
                await SendAsync(ws, StateProvider?.Invoke() ?? "", ct);
            }
        }
        catch { }   // la pestaña se cerró o el servidor se apagó: no es un error
        finally
        {
            lock (_socketsLock) _sockets.Remove(ws);
            try { ws.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Envía un texto por un WebSocket. No hace nada si el texto está vacío o la conexión ya
    /// no está abierta. Los envíos van de uno en uno (ver _sendLock).
    /// </summary>
    private async Task SendAsync(WebSocket ws, string json, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(json) || ws.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally { _sendLock.Release(); }
    }

    // ==========================================
    // TIPOS DE ARCHIVO (MIME)
    // ==========================================

    /// <summary>
    /// El tipo MIME que se envía con cada archivo, según su extensión. Las extensiones son las
    /// mismas que en WidgetServerService.GetMimeType de SheepGDRequest, que es quien sirve el
    /// tema en OBS, para que la vista previa se comporte igual; aquí se añaden .htm y el
    /// juego de caracteres en los tipos de texto. Importa sobre todo en los vídeos: un .mp4
    /// enviado como application/octet-stream no se reproduce.
    /// </summary>
    private static string GetMimeType(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js"             => "application/javascript; charset=utf-8",
        ".css"            => "text/css; charset=utf-8",
        ".json"           => "application/json; charset=utf-8",
        ".png"            => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif"            => "image/gif",
        ".webp"           => "image/webp",
        ".svg"            => "image/svg+xml",
        ".mp4" or ".m4v"  => "video/mp4",
        ".webm"           => "video/webm",
        ".mov"            => "video/quicktime",
        ".ogv" or ".ogg"  => "video/ogg",
        ".ttf"            => "font/ttf",
        ".otf"            => "font/otf",
        ".woff"           => "font/woff",
        ".woff2"          => "font/woff2",
        _                 => "application/octet-stream"
    };
}
