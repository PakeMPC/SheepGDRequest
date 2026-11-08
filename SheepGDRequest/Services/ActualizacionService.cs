using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SheepGDRequest.Services;

/// <summary>
/// Un archivo adjunto de una release: nombre, URL de descarga, tamaño en bytes y, si GitHub lo
/// publica, su SHA-256 en hexadecimal.
/// </summary>
public sealed record AdjuntoDeRelease(string Nombre, string Url, long Tamanio, string? Sha256);

/// <summary>
/// La última release del programa en GitHub: su versión (sacada de la etiqueta), el texto de
/// novedades, la página de la release y sus adjuntos.
/// </summary>
public sealed record ReleaseDelPrograma(
    Version Version, string Etiqueta, string Notas, string UrlPagina, IReadOnlyList<AdjuntoDeRelease> Adjuntos);

/// <summary>
/// Cómo puede actualizarse esta copia del programa.
/// </summary>
public enum FormaDeActualizar
{
    /// <summary>
    /// La carpeta portable de Windows: lanzadores en la raíz y una carpeta por app.
    /// </summary>
    Windows,

    /// <summary>Un AppImage de Linux, que se sustituye por el nuevo.</summary>
    LinuxAppImage,

    /// <summary>
    /// Ninguna de las dos (desarrollo, una copia suelta, una carpeta sin permiso de escritura):
    /// solo se puede abrir la página de la release para descargarla a mano.
    /// </summary>
    SoloPagina
}

/// <summary>
/// Un fallo al preparar la actualización. El mensaje es técnico y solo va a la consola; al
/// usuario se le avisa con una notificación general.
/// </summary>
public sealed class ActualizacionException : Exception
{
    public ActualizacionException(string mensaje) : base(mensaje) { }
}

/// <summary>
/// Actualizaciones del propio programa (SheepGDRequest y SheepGDEditor, que se publican juntos)
/// desde las releases de <see cref="Repositorio"/>. Es el equivalente de GeodeModService para el
/// programa.
///
/// Cada release lleva estos adjuntos (los genera build/publicar.sh):
///   - <see cref="ArchivoWindows"/>: una carpeta "SheepGD" con los dos lanzadores y las carpetas
///     SheepGDRequest y SheepGDEditor.
///   - SheepGDRequest-Linux-x86_64.AppImage y SheepGDEditor-Linux-x86_64.AppImage.
///
/// En Windows no se pueden reemplazar los archivos de un programa abierto: aquí solo se descarga
/// y se descomprime la versión nueva en la carpeta oculta ".actualizacion" de la raíz, y el
/// cambio de carpetas lo hace el lanzador nuevo (build/lanzador.c), después de que el programa se
/// cierre. En Linux sí se puede sustituir un AppImage en uso, así que se hace aquí mismo.
///
/// No muestra nada: devuelve resultados o lanza <see cref="ActualizacionException"/>, y
/// SettingsViewModel decide qué decir.
/// </summary>
public class ActualizacionService
{
    /// <summary>Repositorio de GitHub del programa, "propietario/nombre".</summary>
    public const string Repositorio = "PakeMPC/SheepGDRequest";

    /// <summary>Adjunto de la release para Windows.</summary>
    public const string ArchivoWindows = "SheepGD-win-x64.zip";

    /// <summary>
    /// Adjunto de la release con el AppImage de una app ("SheepGDRequest" o "SheepGDEditor").
    /// </summary>
    public static string ArchivoAppImage(string app) => $"{app}-Linux-x86_64.AppImage";

    /// <summary>Carpeta, dentro de la raíz de Windows, donde se prepara la versión nueva.</summary>
    public const string CarpetaDePreparacion = ".actualizacion";

    // Ninguna descarga legítima se acerca a esto (el zip de Windows ronda los 200 MB).
    private const long TamanioMaximo = 700L * 1024 * 1024;

    // Un AppImage más pequeño que esto está roto o no es el programa.
    private const long TamanioMinimoAppImage = 5L * 1024 * 1024;

    /// <summary>
    /// Cliente para la API. GitHub exige un User-Agent. El tiempo límite es corto porque la
    /// consulta se hace al arrancar y no debe hacer esperar.
    /// </summary>
    private static readonly HttpClient ClienteApi = CrearCliente(TimeSpan.FromSeconds(20));

    /// <summary>
    /// Cliente para las descargas, sin tiempo límite global: cientos de MB con una conexión lenta
    /// tardan minutos. Una descarga colgada se corta con el token de cancelación.
    /// </summary>
    private static readonly HttpClient ClienteDescargas = CrearCliente(Timeout.InfiniteTimeSpan);

    private static HttpClient CrearCliente(TimeSpan limite)
    {
        var cliente = new HttpClient { Timeout = limite };
        cliente.DefaultRequestHeaders.UserAgent.ParseAdd("SheepGDRequest");
        return cliente;
    }

    private readonly string _urlUltimaRelease;

    /// <param name="urlUltimaRelease">URL de la API con la última release. Solo se cambia en
    /// pruebas; por defecto es la de <see cref="Repositorio"/>.</param>
    public ActualizacionService(string? urlUltimaRelease = null)
    {
        _urlUltimaRelease = urlUltimaRelease
                            ?? $"https://api.github.com/repos/{Repositorio}/releases/latest";
    }

    /// <summary>
    /// Versión de esta copia del programa, con tres números. Sale de &lt;Version&gt; en
    /// Directory.Build.props.
    /// </summary>
    public static Version VersionActual { get; } = LeerVersionActual();

    private static Version LeerVersionActual()
    {
        var v = typeof(ActualizacionService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    // ================================================================
    // GITHUB
    // ================================================================
    /// <summary>
    /// Pide a GitHub la última release. "latest" ya descarta los borradores y las pre-releases.
    /// </summary>
    /// <returns>
    /// La release, o null si no se pudo consultar o su etiqueta no es una versión.
    /// </returns>
    public async Task<ReleaseDelPrograma?> ObtenerUltimaReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Get, _urlUltimaRelease);
            peticion.Headers.Accept.ParseAdd("application/vnd.github+json");
            peticion.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var respuesta = await ClienteApi.SendAsync(peticion, ct);
            if (!respuesta.IsSuccessStatusCode) return null;

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(flujo, cancellationToken: ct);
            var raiz = json.RootElement;

            string etiqueta = Texto(raiz, "tag_name");
            Version? version = GeodeModService.LeerVersion(etiqueta);
            if (version == null) return null;

            var adjuntos = new List<AdjuntoDeRelease>();
            if (raiz.TryGetProperty("assets", out var lista) && lista.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in lista.EnumerateArray())
                {
                    string url = Texto(a, "browser_download_url");
                    if (!UrlAceptable(url)) continue;

                    long tamanio = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long t) ? t : 0;

                    // GitHub publica el resumen como "sha256:<hex>" desde 2025; en releases
                    // anteriores no viene y la comprobación se salta.
                    string digest = Texto(a, "digest");
                    string? sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest.Substring(7).ToLowerInvariant()
                        : null;

                    adjuntos.Add(new AdjuntoDeRelease(Texto(a, "name"), url, tamanio, sha));
                }
            }

            string pagina = Texto(raiz, "html_url");
            if (!UrlAceptable(pagina)) pagina = $"https://github.com/{Repositorio}/releases/latest";

            return new ReleaseDelPrograma(version, etiqueta, Texto(raiz, "body").Trim(), pagina, adjuntos);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Sin red, tiempo agotado o una respuesta que no es el JSON esperado: no hay release.
            return null;
        }
    }

    private static string Texto(JsonElement e, string propiedad) =>
        e.TryGetProperty(propiedad, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// Solo HTTPS. La excepción es el propio equipo (localhost), que usan las pruebas con un
    /// servidor falso en lugar de GitHub.
    /// </summary>
    private static bool UrlAceptable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>
    /// Abre la página de la release en el navegador del sistema. No lanza excepciones.
    /// </summary>
    public static void AbrirPagina(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_OpenPathError"), url, ex.Message));
        }
    }

    // ================================================================
    // QUÉ TIPO DE COPIA ES ESTA
    // ================================================================
    /// <summary>
    /// Raíz de la instalación portable de Windows: la carpeta que contiene los lanzadores y las
    /// carpetas SheepGDRequest y SheepGDEditor. Null si esta copia no tiene esa forma.
    /// </summary>
    public static string? RaizDeWindows()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var carpetaApp = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppPaths.AppDir));
        string? raiz = carpetaApp.Parent?.FullName;
        if (raiz == null || !carpetaApp.Name.Equals("SheepGDRequest", StringComparison.OrdinalIgnoreCase))
            return null;

        bool completa = File.Exists(Path.Combine(raiz, "SheepGDRequest.exe"))
                        && Directory.Exists(Path.Combine(raiz, "SheepGDEditor"));
        return completa ? raiz : null;
    }

    /// <summary>
    /// Ruta del AppImage que se está ejecutando, o null si el programa no se abrió desde un
    /// AppImage. El propio AppImage la deja en la variable de entorno APPIMAGE, también si el
    /// usuario lo renombró.
    /// </summary>
    public static string? AppImageEnUso()
    {
        if (!OperatingSystem.IsLinux()) return null;

        string? ruta = Environment.GetEnvironmentVariable("APPIMAGE");
        return !string.IsNullOrWhiteSpace(ruta) && File.Exists(ruta) ? Path.GetFullPath(ruta) : null;
    }

    /// <summary>
    /// El AppImage del Editor que está junto al de este programa, o null si no hay. Se prefiere el
    /// nombre de la release; si no, cualquier "SheepGDEditor*.AppImage", pero solo si hay uno.
    /// </summary>
    public static string? AppImageDelEditorJunto(string appImageDelPrograma)
    {
        try
        {
            string? carpeta = Path.GetDirectoryName(appImageDelPrograma);
            if (carpeta == null) return null;

            string exacto = Path.Combine(carpeta, ArchivoAppImage("SheepGDEditor"));
            if (File.Exists(exacto)) return exacto;

            var parecidos = Directory.GetFiles(carpeta, "*.AppImage")
                .Where(f => Path.GetFileName(f).StartsWith("SheepGDEditor", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return parecidos.Count == 1 ? parecidos[0] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Cómo puede actualizarse esta copia. Además de la forma de la instalación, comprueba que se
    /// puede escribir en su carpeta: una copia en "Archivos de programa" o en /opt no puede
    /// actualizarse sin permisos de administrador.
    /// </summary>
    public static FormaDeActualizar DetectarForma()
    {
        string? raiz = RaizDeWindows();
        if (raiz != null) return SePuedeEscribir(raiz) ? FormaDeActualizar.Windows : FormaDeActualizar.SoloPagina;

        string? appImage = AppImageEnUso();
        if (appImage != null && SePuedeEscribir(Path.GetDirectoryName(appImage)!))
            return FormaDeActualizar.LinuxAppImage;

        return FormaDeActualizar.SoloPagina;
    }

    private static bool SePuedeEscribir(string carpeta)
    {
        string prueba = Path.Combine(carpeta, $".sheepgd-prueba-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(prueba, "");
            File.Delete(prueba);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ================================================================
    // DESCARGA
    // ================================================================
    /// <summary>
    /// Descarga un adjunto a <paramref name="destino"/>, informando del avance (0 a 1), y
    /// comprueba su SHA-256 si la release lo publica. Si algo falla, borra lo descargado y lanza
    /// <see cref="ActualizacionException"/>.
    /// </summary>
    private static async Task DescargarAsync(AdjuntoDeRelease adjunto, string destino,
        IProgress<double>? progreso, CancellationToken ct)
    {
        try
        {
            using var respuesta = await ClienteDescargas.GetAsync(adjunto.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!respuesta.IsSuccessStatusCode)
                throw new ActualizacionException($"HTTP {(int)respuesta.StatusCode} ({adjunto.Nombre})");

            long total = respuesta.Content.Headers.ContentLength ?? adjunto.Tamanio;
            if (total > TamanioMaximo) throw new ActualizacionException($"{adjunto.Nombre}: {total} bytes");

            using var sha = SHA256.Create();
            await using (var origen = await respuesta.Content.ReadAsStreamAsync(ct))
            await using (var archivo = File.Create(destino))
            {
                byte[] buffer = new byte[128 * 1024];
                long leidosTotal = 0;
                int leidos;
                while ((leidos = await origen.ReadAsync(buffer, ct)) > 0)
                {
                    leidosTotal += leidos;
                    if (leidosTotal > TamanioMaximo) throw new ActualizacionException($"{adjunto.Nombre}: > {TamanioMaximo} bytes");

                    sha.TransformBlock(buffer, 0, leidos, null, 0);
                    await archivo.WriteAsync(buffer.AsMemory(0, leidos), ct);
                    if (total > 0) progreso?.Report(Math.Min(1.0, (double)leidosTotal / total));
                }
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            string calculado = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (adjunto.Sha256 != null && calculado != adjunto.Sha256)
                throw new ActualizacionException($"SHA-256 ({adjunto.Nombre}): {calculado} != {adjunto.Sha256}");
        }
        catch (Exception ex)
        {
            BorrarArchivo(destino);
            if (ex is ActualizacionException || ct.IsCancellationRequested) throw;
            throw new ActualizacionException($"{adjunto.Nombre}: {ex.Message}");
        }
    }

    /// <summary>
    /// El adjunto con ese nombre exacto o, si no está, el primero que cumpla
    /// <paramref name="parecido"/>. Así una release con un nombre algo distinto sigue sirviendo.
    /// </summary>
    private static AdjuntoDeRelease? Buscar(ReleaseDelPrograma release, string nombre, Func<string, bool> parecido) =>
        release.Adjuntos.FirstOrDefault(a => a.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase))
        ?? release.Adjuntos.FirstOrDefault(a => parecido(a.Nombre));

    // ================================================================
    // WINDOWS
    // ================================================================
    /// <summary>
    /// Descarga el zip de Windows, lo descomprime en "{raíz}\.actualizacion" y comprueba que trae
    /// las dos apps y el lanzador. Devuelve cómo arrancar el lanzador nuevo en modo "aplicar": ese
    /// lanzador espera a que este proceso termine, cambia las carpetas (y los lanzadores de la
    /// raíz) y vuelve a abrir el programa. Quien llama tiene que arrancarlo y cerrar el programa.
    ///
    /// Se usa el lanzador de la versión nueva, no el que ya está en la raíz: así funciona aunque el
    /// de la raíz sea antiguo, y los de la raíz se pueden reemplazar porque no están en uso.
    /// </summary>
    public async Task<ProcessStartInfo> PrepararWindowsAsync(ReleaseDelPrograma release,
        IProgress<double>? progreso, CancellationToken ct = default)
    {
        string raiz = RaizDeWindows() ?? throw new ActualizacionException("No es una instalación portable de Windows.");

        var adjunto = Buscar(release, ArchivoWindows,
                          n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                               && n.Contains("win", StringComparison.OrdinalIgnoreCase))
                      ?? throw new ActualizacionException($"La release no trae {ArchivoWindows}.");

        // Una preparación anterior a medias se descarta entera.
        string preparacion = Path.Combine(raiz, CarpetaDePreparacion);
        BorrarCarpeta(preparacion);
        var info = Directory.CreateDirectory(preparacion);
        try { info.Attributes |= FileAttributes.Hidden; } catch { }

        string zip = Path.Combine(preparacion, "descarga.zip");
        await DescargarAsync(adjunto, zip, progreso, ct);

        string extraido = Path.Combine(preparacion, "paquete");
        try
        {
            ExtraerSeguro(zip, extraido);
        }
        catch (Exception ex) when (ex is not ActualizacionException)
        {
            throw new ActualizacionException($"{adjunto.Nombre}: {ex.Message}");
        }
        finally
        {
            BorrarArchivo(zip);
        }

        string paquete = BuscarPaquete(extraido)
                         ?? throw new ActualizacionException($"{adjunto.Nombre} no tiene la forma esperada.");

        var inicio = new ProcessStartInfo
        {
            FileName = Path.Combine(paquete, "SheepGDRequest.exe"),
            WorkingDirectory = paquete,
            UseShellExecute = false
        };
        inicio.ArgumentList.Add("--aplicar-actualizacion");
        inicio.ArgumentList.Add(Environment.ProcessId.ToString());
        inicio.ArgumentList.Add(raiz);
        return inicio;
    }

    /// <summary>
    /// La carpeta del paquete descomprimido que tiene el lanzador y las dos apps. Puede ser la
    /// propia carpeta o una de sus subcarpetas (el zip de publicar.sh lleva todo dentro de
    /// "SheepGD"). Null si no aparece.
    /// </summary>
    public static string? BuscarPaquete(string extraido)
    {
        static bool Completo(string d) =>
            File.Exists(Path.Combine(d, "SheepGDRequest.exe"))
            && File.Exists(Path.Combine(d, "SheepGDRequest", "SheepGDRequest.exe"))
            && File.Exists(Path.Combine(d, "SheepGDEditor", "SheepGDEditor.exe"));

        if (Completo(extraido)) return extraido;
        return Directory.GetDirectories(extraido).FirstOrDefault(Completo);
    }

    /// <summary>
    /// Descomprime un zip comprobando que ninguna entrada se sale de la carpeta de destino (una
    /// entrada "..\..\algo" escribiría fuera).
    /// </summary>
    public static void ExtraerSeguro(string zip, string destino)
    {
        string raiz = Path.GetFullPath(destino) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(raiz);

        using var archivo = ZipFile.OpenRead(zip);
        foreach (var entrada in archivo.Entries)
        {
            string ruta = Path.GetFullPath(Path.Combine(raiz, entrada.FullName));
            if (!ruta.StartsWith(raiz, StringComparison.OrdinalIgnoreCase))
                throw new ActualizacionException($"Entrada fuera de la carpeta: {entrada.FullName}");

            if (entrada.FullName.EndsWith('/') || entrada.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(ruta);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            entrada.ExtractToFile(ruta, overwrite: true);
        }
    }

    // ================================================================
    // LINUX
    // ================================================================
    /// <summary>
    /// Sustituye el AppImage en uso por el de la release y, si hay un AppImage del Editor al lado,
    /// también ese. Primero se descargan y comprueban los dos (a un temporal oculto en la misma
    /// carpeta) y luego se ponen en su sitio con un renombrado, que en Linux funciona aunque el
    /// archivo se esté ejecutando: el proceso abierto sigue con el archivo viejo hasta cerrarse.
    ///
    /// Si falla lo del programa, no se toca nada. Si solo falla lo del Editor, el programa queda
    /// actualizado y el error se apunta en la consola.
    /// </summary>
    /// <returns>La ruta del AppImage del programa, para volver a abrirlo.</returns>
    public async Task<string> ReemplazarAppImagesAsync(ReleaseDelPrograma release,
        IProgress<double>? progreso, CancellationToken ct = default)
    {
        string programa = AppImageEnUso() ?? throw new ActualizacionException("No se está ejecutando desde un AppImage.");
        string? editor = AppImageDelEditorJunto(programa);

        var adjuntoPrograma = BuscarAppImage(release, "SheepGDRequest")
                              ?? throw new ActualizacionException($"La release no trae {ArchivoAppImage("SheepGDRequest")}.");
        var adjuntoEditor = editor == null ? null : BuscarAppImage(release, "SheepGDEditor");

        // El avance se reparte según lo que haya que bajar: el programa y, si toca, el Editor.
        double parte = adjuntoEditor == null ? 1.0 : 0.5;

        string temporalPrograma = TemporalJunto(programa);
        await DescargarAsync(adjuntoPrograma, temporalPrograma,
            progreso == null ? null : new Progress<double>(p => progreso.Report(p * parte)), ct);
        ComprobarYPrepararAppImage(temporalPrograma, adjuntoPrograma.Nombre);

        string? temporalEditor = null;
        if (editor != null && adjuntoEditor != null)
        {
            try
            {
                temporalEditor = TemporalJunto(editor);
                await DescargarAsync(adjuntoEditor, temporalEditor,
                    progreso == null ? null : new Progress<double>(p => progreso.Report(parte + p * parte)), ct);
                ComprobarYPrepararAppImage(temporalEditor, adjuntoEditor.Nombre);
            }
            catch (ActualizacionException ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_AppUpdateEditorFailed"), ex.Message));
                if (temporalEditor != null) BorrarArchivo(temporalEditor);
                temporalEditor = null;
            }
        }

        try
        {
            File.Move(temporalPrograma, programa, overwrite: true);
        }
        catch (Exception ex)
        {
            BorrarArchivo(temporalPrograma);
            if (temporalEditor != null) BorrarArchivo(temporalEditor);
            throw new ActualizacionException($"{programa}: {ex.Message}");
        }

        if (temporalEditor != null)
        {
            try
            {
                File.Move(temporalEditor, editor!, overwrite: true);
            }
            catch (Exception ex)
            {
                BorrarArchivo(temporalEditor);
                Console.WriteLine(string.Format(I18n.Tr("Log_AppUpdateEditorFailed"), ex.Message));
            }
        }

        progreso?.Report(1.0);
        return programa;
    }

    private static AdjuntoDeRelease? BuscarAppImage(ReleaseDelPrograma release, string app) =>
        Buscar(release, ArchivoAppImage(app),
            n => n.StartsWith(app, StringComparison.OrdinalIgnoreCase)
                 && n.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
                 && n.Contains("x86_64", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Temporal oculto en la misma carpeta que el archivo: el renombrado final solo es inmediato
    /// (y seguro) dentro del mismo sistema de archivos.
    /// </summary>
    private static string TemporalJunto(string archivo) =>
        Path.Combine(Path.GetDirectoryName(archivo)!, "." + Path.GetFileName(archivo) + ".actualizacion");

    /// <summary>
    /// Comprueba que lo descargado es un AppImage (un ejecutable ELF con la marca "AI" de tipo 2
    /// en el byte 8) y le da permisos de ejecución (755). Si no lo es, lo borra y lanza
    /// <see cref="ActualizacionException"/>.
    /// </summary>
    public static void ComprobarYPrepararAppImage(string ruta, string nombre)
    {
        try
        {
            var info = new FileInfo(ruta);
            if (info.Length < TamanioMinimoAppImage) throw new ActualizacionException($"{nombre}: {info.Length} bytes");

            byte[] cabecera = new byte[11];
            using (var f = File.OpenRead(ruta))
            {
                if (f.Read(cabecera, 0, cabecera.Length) != cabecera.Length)
                    throw new ActualizacionException($"{nombre}: archivo incompleto");
            }

            bool esElf = cabecera[0] == 0x7F && cabecera[1] == (byte)'E' && cabecera[2] == (byte)'L' && cabecera[3] == (byte)'F';
            bool esAppImage = cabecera[8] == (byte)'A' && cabecera[9] == (byte)'I' && cabecera[10] == 0x02;
            if (!esElf || !esAppImage) throw new ActualizacionException($"{nombre}: no es un AppImage");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(ruta,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            BorrarArchivo(ruta);
            if (ex is ActualizacionException) throw;
            throw new ActualizacionException($"{nombre}: {ex.Message}");
        }
    }

    /// <summary>
    /// Cómo volver a abrir el AppImage ya sustituido: a través de sh, que espera a que este
    /// proceso termine (como mucho 60 s) antes de abrirlo. Así la versión nueva no arranca
    /// mientras la vieja todavía guarda los ajustes o tiene ocupado el puerto del servidor de
    /// widgets; cerrar puede tardar varios segundos. La carpeta de trabajo es la del AppImage: la
    /// actual está dentro del AppImage montado, que desaparece al cerrar.
    /// </summary>
    public static ProcessStartInfo ComoReabrirAppImage(string appImage)
    {
        var inicio = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = Path.GetDirectoryName(appImage)!,
            UseShellExecute = false
        };
        inicio.ArgumentList.Add("-c");
        inicio.ArgumentList.Add(
            "i=0; while kill -0 \"$1\" 2>/dev/null && [ \"$i\" -lt 120 ]; do sleep 0.5; i=$((i+1)); done; exec \"$0\"");
        inicio.ArgumentList.Add(appImage);
        inicio.ArgumentList.Add(Environment.ProcessId.ToString());
        return inicio;
    }

    // ================================================================
    // LIMPIEZA
    // ================================================================
    /// <summary>
    /// Borra lo que deja una actualización: en Windows, la carpeta ".actualizacion" y las versiones
    /// anteriores que apartó el lanzador ("SheepGDRequest.anterior.*",
    /// "SheepGDEditor.exe.anterior.*", etc.); en Linux, temporales de una descarga a medias. Se llama al arrancar, unos segundos
    /// después, porque el lanzador que aplicó la actualización se ejecuta desde ".actualizacion" y
    /// tarda un momento en cerrarse. No lanza excepciones: lo que no se pueda borrar se intenta en
    /// el siguiente arranque.
    /// </summary>
    public static void LimpiarRestos()
    {
        try
        {
            string? raiz = RaizDeWindows();
            if (raiz != null)
            {
                BorrarCarpeta(Path.Combine(raiz, CarpetaDePreparacion));

                foreach (string carpeta in Directory.GetDirectories(raiz))
                {
                    string nombre = Path.GetFileName(carpeta);
                    if (EsCopiaAnterior(nombre)) BorrarCarpeta(carpeta);
                }

                foreach (string archivo in Directory.GetFiles(raiz))
                {
                    if (EsCopiaAnterior(Path.GetFileName(archivo))) BorrarArchivo(archivo);
                }
                return;
            }

            string? appImage = AppImageEnUso();
            if (appImage != null)
            {
                string carpeta = Path.GetDirectoryName(appImage)!;
                foreach (string archivo in Directory.GetFiles(carpeta, ".*.AppImage.actualizacion"))
                    BorrarArchivo(archivo);
            }
        }
        catch (Exception)
        {
            // Limpieza de restos: si falla, no afecta a nada.
        }
    }

    /// <summary>
    /// Si un nombre de la raíz es una versión apartada por el lanzador. Solo las de estas dos apps,
    /// para no tocar nada más que el usuario tenga en esa carpeta.
    /// </summary>
    public static bool EsCopiaAnterior(string nombre) =>
        (nombre.StartsWith("SheepGDRequest.", StringComparison.OrdinalIgnoreCase)
         || nombre.StartsWith("SheepGDEditor.", StringComparison.OrdinalIgnoreCase))
        && nombre.Contains(".anterior", StringComparison.OrdinalIgnoreCase);

    // En Windows, un archivo de solo lectura no se puede borrar (tampoco con Directory.Delete
    // recursivo): se le quita ese atributo antes. Pasa, por ejemplo, con archivos que vinieron
    // de un zip o de una copia que conservó los atributos.
    private static void BorrarArchivo(string ruta)
    {
        try
        {
            if (!File.Exists(ruta)) return;
            File.SetAttributes(ruta, FileAttributes.Normal);
            File.Delete(ruta);
        }
        catch { }
    }

    private static void BorrarCarpeta(string ruta)
    {
        try
        {
            if (!Directory.Exists(ruta)) return;
            foreach (string archivo in Directory.EnumerateFiles(ruta, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(archivo, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(ruta, true);
        }
        catch { }
    }
}
