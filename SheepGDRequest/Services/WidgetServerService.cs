using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.WebSockets;
using Avalonia.Platform; // Para leer los Assets nativos de Avalonia
using SheepGDRequest.Models;
using SheepGDRequest.ViewModels;

namespace SheepGDRequest.Services;

// =======================================================================
// 1. MODELO DEL MENSAJE
// =======================================================================
/// <summary>
/// Formato de cada mensaje del WebSocket: <c>event</c> indica qué ocurrió y <c>target</c>, a qué
/// widget va dirigido ("playing", "queue", "notification", "unified"). Se serializa en
/// camelCase. Forma parte del contrato de datos con los widgets: no renombrar.
/// </summary>
public class WsMessage
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

// =======================================================================
// 2. SERVICIO PRINCIPAL (SINGLETON)
// =======================================================================
/// <summary>
/// Servidor local para los widgets de OBS (EmbedIO, solo en localhost). Sirve tres cosas: los
/// archivos de cada tema, leídos directamente del .zip (/widget/...); los recursos de la
/// aplicación, como las caras de dificultad (/Assets/...); y un WebSocket (/ws) que notifica
/// cada cambio a los widgets. Lo arrancan y detienen los ajustes (SettingsModel) y la carga
/// inicial (SettingsViewModel).
/// </summary>
public class WidgetServerService
{
    public static WidgetServerService Instance { get; } = new();

    private WebServer? _server;
    private GDEventWebSocketModule? _wsModule;
    private CancellationTokenSource? _cts;

    private WidgetServerService() { }

    /// <summary>
    /// Arranca el servidor en el puerto indicado; si ya había uno en marcha, lo detiene antes.
    /// Vuelve enseguida: el resultado del arranque (escuchando, o el error si el puerto está
    /// ocupado) lo comunica <see cref="VigilarAsync"/> en la consola y, si falla, en el panel.
    /// </summary>
    public void StartServer(int port)
    {
        StopServer();
        _cts = new CancellationTokenSource();

        var url = $"http://localhost:{port}/";
        _wsModule = new GDEventWebSocketModule("/ws");

        // Los temas elegidos se consultan en cada petición, mediante las funciones de abajo, así
        // que cambiar de tema en Ajustes no obliga a reiniciar el servidor.
        var settings = MainWindowViewModel.Instance.SettingsVM.Settings;

        _server = new WebServer(o => o
                .WithUrlPrefix(url)
                .WithMode(HttpListenerMode.EmbedIO))

            // Expone los recursos "avares://" de Avalonia a la web, para OBS.
            .WithModule(new AvaloniaAssetsModule("/Assets/"))

            // Un módulo por tipo de widget, para buscar primero en la subcarpeta de ese tipo dentro
            // del ZIP.
            .WithModule(new ZipWidgetModule("/widget/notification/", "notification", () => settings.SelectedNotificationTheme))
            .WithModule(new ZipWidgetModule("/widget/playing/", "playing", () => settings.SelectedPlayingTheme))
            .WithModule(new ZipWidgetModule("/widget/queue/", "queue", () => settings.SelectedQueueTheme))
            .WithModule(new ZipWidgetModule("/widget/unified/", "unified", () => settings.SelectedUnifiedTheme))

            .WithModule(_wsModule);

        var server = _server;
        var token = _cts.Token;
        server.StateChanged += (_, e) =>
        {
            if (e.NewState == WebServerState.Listening)
                Console.WriteLine(string.Format(I18n.Tr("Log_WidgetServerStarted"), url));
        };

        _ = VigilarAsync(server.RunAsync(token), port, token);
    }

    /// <summary>
    /// Espera a que termine la ejecución del servidor. Si termina con un error que no viene de
    /// detenerlo (lo normal: el puerto lo usa otro programa), lo dice en la consola y en el panel:
    /// sin esto, los widgets de OBS quedaban en blanco sin ninguna pista.
    /// </summary>
    private static async Task VigilarAsync(Task ejecucion, int port, CancellationToken token)
    {
        try
        {
            await ejecucion;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_WidgetServerError"), port, ex.GetBaseException().Message));
            await SheepChatListener.NotificarConexionAsync("Error", "Notif_WidgetServerError", port.ToString());
        }
        catch
        {
            // Errores al detenerlo: no indican ningún problema.
        }
    }

    public void StopServer()
    {
        if (_server != null)
        {
            _cts?.Cancel();
            _server.Dispose();
            _server = null;
            Console.WriteLine(I18n.Tr("Log_WidgetServerStopped"));
        }
    }

    /// <summary>
    /// Envía el estado de la cola a los widgets en tres formatos, uno por tipo: "now_playing" (solo
    /// si hay nivel actual), "queue_updated" y "state_updated" para el unificado. Este último
    /// lleva el idioma, como el de la conexión y el de "request_state": I18n.ChangeLanguage llama
    /// a este método, y el JavaScript de los temas (engine.js) cambia de idioma al leerlo.
    /// </summary>
    public void BroadcastQueueStateAsync(LevelRequestModel? currentLevel, List<LevelRequestModel> queue)
    {
        if (_wsModule == null) return;

        if (currentLevel != null)
        {
            _wsModule.BroadcastAsync(new WsMessage { Event = "now_playing", Target = "playing", Data = currentLevel });
        }

        _wsModule.BroadcastAsync(new WsMessage { Event = "queue_updated", Target = "queue", Data = queue });

        _wsModule.BroadcastAsync(new WsMessage
        {
            Event = "state_updated",
            Target = "unified",
            Data = new { Current = currentLevel, Queue = queue, Language = I18n.CurrentLanguage }
        });
    }

    public void BroadcastNotificationAsync(NotificationModel notif)
    {
        if (_wsModule == null) return;
        _wsModule.BroadcastAsync(new WsMessage { Event = "new_notification", Target = "notification", Data = notif });
    }

    /// <summary>
    /// Pide a los widgets de ese tipo que se recarguen. Se usa al cambiar de tema en Ajustes.
    /// </summary>
    public void BroadcastThemeReloadAsync(string targetWidget)
    {
        if (_wsModule == null) return;
        _wsModule.BroadcastAsync(new WsMessage { Event = "reload_theme", Target = targetWidget });
    }
}

// =======================================================================
// 3. MÓDULO WEBSOCKET
// =======================================================================
public class GDEventWebSocketModule : WebSocketModule
{
    public GDEventWebSocketModule(string urlPath) : base(urlPath, true) { }

    // Al conectarse un widget, se le envía el estado actual, con el idioma, para que no tenga que
    // esperar al siguiente cambio de la cola.
    protected override Task OnClientConnectedAsync(IWebSocketContext context)
    {
        var vm = SheepGDRequest.ViewModels.MainWindowViewModel.Instance;
        if (vm != null)
        {
            var msg = new WsMessage
            {
                Event = "state_updated",
                Target = "unified",
                Data = new {
                    Current = vm.CurrentLevel,
                    Queue = vm.RequestQueue,
                    Language = Services.I18n.CurrentLanguage // Idioma actual
                }
            };

            string json = JsonSerializer.Serialize(msg, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            _ = SendAsync(context, json);
        }

        return base.OnClientConnectedAsync(context);
    }

    /// <summary>
    /// El único mensaje que un widget puede enviar es "request_state", que recibe el mismo estado
    /// que al conectarse. Cualquier otro mensaje se ignora.
    /// </summary>
    protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] rxBuffer, IWebSocketReceiveResult rxResult)
    {
        try
        {
            // 1. Decodifica el mensaje enviado por el JavaScript del widget.
            string payload = System.Text.Encoding.UTF8.GetString(rxBuffer, 0, rxResult.Count);

            // 2. Lee el nombre del evento.
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("event", out var eventProperty))
            {
                string? eventName = eventProperty.GetString();

                // 3. Si el widget pide el estado, se le envía de inmediato.
                if (eventName == "request_state")
                {
                    var vm = SheepGDRequest.ViewModels.MainWindowViewModel.Instance;
                    if (vm != null)
                    {
                        var msg = new WsMessage
                        {
                            Event = "state_updated",
                            Target = "unified",
                            Data = new {
                                Current = vm.CurrentLevel,
                                Queue = vm.RequestQueue,
                                Language = Services.I18n.CurrentLanguage // Idioma actual
                            }
                        };

                        string responseJson = System.Text.Json.JsonSerializer.Serialize(msg, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                        _ = SendAsync(context, responseJson);
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine(string.Format(I18n.Tr("Log_WidgetRequestError"), ex.Message));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Envía el mismo JSON a todos los widgets conectados, sin esperar a ninguno.
    /// </summary>
    public void BroadcastAsync(WsMessage message)
    {
        string json = JsonSerializer.Serialize(message, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        foreach (var session in ActiveContexts)
        {
            _ = SendAsync(session, json);
        }
    }
}

// =======================================================================
// 4. MÓDULO HTTP: ARCHIVOS DE LOS TEMAS (ZIP)
// =======================================================================
/// <summary>
/// Sirve los archivos del tema elegido directamente desde su .zip, sin descomprimirlo. Si el
/// tema es "default", se usa default.zip, el que AppPaths copia en la carpeta de temas.
/// </summary>
public class ZipWidgetModule : WebModuleBase
{
    private readonly Func<ThemeItem?> _getThemeFunc;
    private readonly string _themesDir;
    private readonly string _widgetType;

    public override bool IsFinalHandler => true;

    public ZipWidgetModule(string baseRoute, string widgetType, Func<ThemeItem?> getThemeFunc) : base(baseRoute)
    {
        _widgetType = widgetType;
        _getThemeFunc = getThemeFunc;
        _themesDir = AppPaths.WidgetThemesDir;
    }

    protected override async Task OnRequestAsync(IHttpContext context)
    {
        var theme = _getThemeFunc();
        string fileName = theme?.FileName ?? "default.zip";
        if (fileName == "default") fileName = "default.zip";

        string zipPath = Path.Combine(_themesDir, fileName);
        // El texto del 404 solo lo ve quien abra la URL en un navegador; no forma parte de la
        // interfaz.
        if (!File.Exists(zipPath)) throw HttpException.NotFound($"No se encontró el tema: {fileName}");

        string route = (context.Route.SubPath ?? "").TrimStart('/');
        if (string.IsNullOrEmpty(route)) route = "index.html";

        // El .zip se abre en cada petición: así un tema sustituido en disco se sirve de inmediato.
        using var archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry? entry = null;

        // Búsqueda del archivo: 1. Si no es el unificado, primero en la subcarpeta del widget (por
        // ejemplo, playing/index.html).
        if (_widgetType != "unified")
        {
            entry = archive.GetEntry($"{_widgetType}/{route}");
        }

        // 2. Si no está en la subcarpeta (o es el unificado), en la raíz. Así /bg/animacion.webm
        // funciona aunque la URL sea /widget/playing/bg/animacion.webm.
        entry ??= archive.GetEntry(route);

        if (entry == null) throw HttpException.NotFound($"Archivo '{route}' no encontrado dentro del ZIP.");

        context.Response.ContentType = GetMimeType(route);
        using var stream = entry.Open();
        await stream.CopyToAsync(context.Response.OutputStream);
    }

    /// <summary>
    /// Tipo MIME según la extensión. Se envía sin "charset": una página del tema que no declare su
    /// &lt;meta charset&gt; se interpreta como windows-1252. Por eso las plantillas de tema que
    /// crea el Editor lo declaran.
    /// </summary>
    private string GetMimeType(string file)
    {
        string ext = Path.GetExtension(file).ToLower();
        return ext switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",

            // Vídeos: antes no estaban en la lista y se servían como application/octet-stream, así
            // que el navegador de OBS no los reproducía y el elemento de vídeo aparecía en negro.
            ".webm" => "video/webm",
            ".mp4" => "video/mp4",
            ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".ogv" => "video/ogg",
            ".ogg" => "video/ogg",

            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".otf" => "font/otf",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream"
        };
    }
}

// =======================================================================
// 5. MÓDULO HTTP: RECURSOS DE AVALONIA
// =======================================================================
/// <summary>
/// Expone los recursos incrustados en la aplicación (avares://) para que los widgets puedan
/// mostrar las mismas caras de dificultad e iconos que la ventana: las rutas "/Assets/..." que
/// GeometryDashService guarda en cada nivel funcionan tal cual en el navegador de OBS.
/// </summary>
public class AvaloniaAssetsModule : WebModuleBase
{
    public override bool IsFinalHandler => true;

    public AvaloniaAssetsModule(string baseRoute) : base(baseRoute) { }

    protected override async Task OnRequestAsync(IHttpContext context)
    {
        string route = (context.Route.SubPath ?? "").TrimStart('/');
        if (string.IsNullOrEmpty(route)) throw HttpException.NotFound();

        try
        {
            // Convierte la ruta de la petición HTTP en una URI interna de Avalonia.
            var uri = new Uri($"avares://SheepGDRequest/Assets/{route}");
            using var stream = AssetLoader.Open(uri);

            context.Response.ContentType = GetMimeType(route);
            await stream.CopyToAsync(context.Response.OutputStream);
        }
        catch
        {
            throw HttpException.NotFound($"Asset de Avalonia no encontrado: {route}");
        }
    }

    private string GetMimeType(string file)
    {
        string ext = Path.GetExtension(file).ToLower();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => "application/octet-stream"
        };
    }
}
