using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SheepGDRequest.Services;

/// <summary>
/// De dónde salió el .geode que se instala: la última release de GitHub o la copia que viaja con
/// el programa (carpeta "geode" junto al ejecutable).
/// </summary>
public enum OrigenDelMod { GitHub, Incluido }

/// <summary>
/// La última release del mod publicada en GitHub: su versión (sacada de la etiqueta) y la URL del
/// archivo que se descarga.
/// </summary>
public sealed record ReleaseDelMod(Version Version, string Etiqueta, string UrlDescarga, bool EsZip);

/// <summary>
/// Un .geode listo para instalar: dónde está en disco, su versión (leída de su mod.json) y de
/// dónde salió. Si es una descarga, <see cref="EsTemporal"/> indica que hay que borrarlo después.
/// </summary>
public sealed record ArchivoDelMod(string Ruta, Version Version, OrigenDelMod Origen, bool EsTemporal);

/// <summary>
/// Todo lo que el programa necesita saber del mod de Geode (SheepGDintegration): cuál es la
/// última versión publicada en GitHub, cuál está instalada en Geometry Dash y cómo instalarlo.
///
/// La instalación consiste en copiar el .geode a {carpeta de GD}/geode/mods. El archivo se toma de
/// la última release del repositorio <see cref="Repositorio"/>; si no se puede (sin red, GitHub
/// caído, límite de peticiones o una release sin .geode), se usa la copia incluida en el programa.
///
/// Un .geode es un ZIP con un mod.json en la raíz, que trae el id y la versión del mod ("v1.0.0").
/// Así se lee la versión instalada sin depender de Geode, y así se comprueba que una descarga es
/// de verdad este mod antes de instalarla.
///
/// No escribe en la consola ni avisa: devuelve resultados, y quien lo usa (SettingsViewModel)
/// decide qué decir. Las excepciones de disco al instalar sí se propagan.
/// </summary>
public class GeodeModService
{
    /// <summary>Repositorio de GitHub del mod, "propietario/nombre".</summary>
    public const string Repositorio = "PakeMPC/SheepGDintegration";

    /// <summary>Id del mod en Geode; también es el nombre del archivo, con ".geode".</summary>
    public const string IdDelMod = "pakempc.sheepgdintegration";

    public const string NombreDelArchivo = IdDelMod + ".geode";

    // Una descarga más grande que esto no es el mod (el .geode ocupa unos 3 MB).
    private const long TamanioMaximo = 50L * 1024 * 1024;

    /// <summary>
    /// Cliente compartido. GitHub exige un User-Agent en su API. El tiempo límite es corto: si
    /// GitHub no responde, se pasa a la copia incluida sin hacer esperar al usuario.
    /// </summary>
    private static readonly HttpClient Cliente = CrearCliente();

    private static HttpClient CrearCliente()
    {
        var cliente = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        cliente.DefaultRequestHeaders.UserAgent.ParseAdd("SheepGDRequest");
        return cliente;
    }

    private readonly string _urlUltimaRelease;

    /// <param name="urlUltimaRelease">URL de la API con la última release. Solo se cambia en
    /// pruebas; por defecto es la de <see cref="Repositorio"/>.</param>
    public GeodeModService(string? urlUltimaRelease = null)
    {
        _urlUltimaRelease = urlUltimaRelease
                            ?? $"https://api.github.com/repos/{Repositorio}/releases/latest";
    }

    // ================================================================
    // GITHUB
    // ================================================================
    /// <summary>
    /// Pide a GitHub la última release del mod. "latest" ya descarta los borradores y las
    /// pre-releases. Del archivo adjunto se prefiere el que se llama exactamente
    /// <see cref="NombreDelArchivo"/>; si no, cualquier .geode; y si no hay ninguno, un .zip, que
    /// se abrirá para buscar el .geode dentro (es la forma en que GitHub Actions empaqueta lo que
    /// compila).
    /// </summary>
    /// <returns>
    /// La release, o null si no se pudo consultar o no trae un archivo que sirva.
    /// </returns>
    public async Task<ReleaseDelMod?> ObtenerUltimaReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Get, _urlUltimaRelease);
            peticion.Headers.Accept.ParseAdd("application/vnd.github+json");
            peticion.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var respuesta = await Cliente.SendAsync(peticion, ct);
            if (!respuesta.IsSuccessStatusCode) return null;

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(flujo, cancellationToken: ct);
            var raiz = json.RootElement;

            string etiqueta = raiz.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
            Version? version = LeerVersion(etiqueta);
            if (version == null) return null;
            if (!raiz.TryGetProperty("assets", out var adjuntos) || adjuntos.ValueKind != JsonValueKind.Array)
                return null;

            var archivos = adjuntos.EnumerateArray()
                .Select(a => (
                    Nombre: a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Url: a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : ""))
                .Where(a => UrlAceptable(a.Url))
                .ToList();

            var elegido =
                archivos.FirstOrDefault(a => a.Nombre.Equals(NombreDelArchivo, StringComparison.OrdinalIgnoreCase));
            if (elegido.Url == null)
                elegido = archivos.FirstOrDefault(a => a.Nombre.EndsWith(".geode", StringComparison.OrdinalIgnoreCase));
            if (elegido.Url == null)
                elegido = archivos.FirstOrDefault(a => a.Nombre.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (elegido.Url == null) return null;

            bool esZip = elegido.Nombre.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            return new ReleaseDelMod(version, etiqueta, elegido.Url, esZip);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Sin red, tiempo agotado o una respuesta que no es el JSON esperado: no hay release.
            return null;
        }
    }

    /// <summary>
    /// Solo se descarga por HTTPS. La excepción es el propio equipo (localhost), que usan las
    /// pruebas con un servidor falso en lugar de GitHub.
    /// </summary>
    private static bool UrlAceptable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>
    /// Descarga el archivo de una release a una carpeta temporal dentro de la carpeta de datos y
    /// comprueba que es este mod (mod.json con el id <see cref="IdDelMod"/> y una versión). Si el
    /// adjunto es un .zip, extrae de él el .geode.
    /// </summary>
    /// <returns>El archivo descargado, o null si la descarga falló o no es el mod.</returns>
    public async Task<ArchivoDelMod?> DescargarAsync(ReleaseDelMod release, CancellationToken ct = default)
    {
        string carpeta = Path.Combine(AppPaths.DataDir, "DescargasMod");
        string descarga = Path.Combine(carpeta, release.EsZip ? "release.zip" : NombreDelArchivo + ".descarga");
        string geode = Path.Combine(carpeta, NombreDelArchivo);

        try
        {
            Directory.CreateDirectory(carpeta);

            using (var respuesta = await Cliente.GetAsync(release.UrlDescarga, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!respuesta.IsSuccessStatusCode) return null;
                if (respuesta.Content.Headers.ContentLength > TamanioMaximo) return null;

                await using var origen = await respuesta.Content.ReadAsStreamAsync(ct);
                await using var destino = File.Create(descarga);
                await CopiarConLimiteAsync(origen, destino, ct);
            }

            if (release.EsZip)
            {
                // Del .zip se saca el primer .geode que tenga, esté en la carpeta que esté.
                using var zip = ZipFile.OpenRead(descarga);
                var entrada = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals(NombreDelArchivo, StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".geode", StringComparison.OrdinalIgnoreCase));
                if (entrada == null) return null;
                entrada.ExtractToFile(geode, overwrite: true);
                File.Delete(descarga);
            }
            else
            {
                File.Move(descarga, geode, overwrite: true);
            }

            Version? version = LeerVersionDeGeode(geode);
            if (version == null)
            {
                BorrarSinErrores(geode);
                return null;
            }

            return new ArchivoDelMod(geode, version, OrigenDelMod.GitHub, EsTemporal: true);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            BorrarSinErrores(descarga);
            BorrarSinErrores(geode);
            return null;
        }
    }

    /// <summary>
    /// Copia un flujo y corta si pasa de <see cref="TamanioMaximo"/>: cuando el servidor no
    /// manda el tamaño, es la única forma de no llenar el disco con algo que no es el mod.
    /// </summary>
    private static async Task CopiarConLimiteAsync(Stream origen, Stream destino, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int leidos;
        while ((leidos = await origen.ReadAsync(buffer, ct)) > 0)
        {
            total += leidos;
            if (total > TamanioMaximo) throw new InvalidDataException("Descarga demasiado grande.");
            await destino.WriteAsync(buffer.AsMemory(0, leidos), ct);
        }
    }

    // ================================================================
    // ARCHIVOS LOCALES
    // ================================================================
    /// <summary>
    /// El .geode que viaja con el programa, o null si no está o no se puede leer.
    /// </summary>
    public static ArchivoDelMod? ObtenerIncluido()
    {
        string ruta = Path.Combine(AppPaths.AppDir, "geode", NombreDelArchivo);
        if (!File.Exists(ruta)) return null;

        Version? version = LeerVersionDeGeode(ruta);
        return version == null ? null : new ArchivoDelMod(ruta, version, OrigenDelMod.Incluido, EsTemporal: false);
    }

    /// <summary>
    /// Ruta en la que queda el mod instalado dentro de una carpeta de Geometry Dash.
    /// </summary>
    public static string RutaInstalada(string carpetaGd) =>
        Path.Combine(carpetaGd, "geode", "mods", NombreDelArchivo);

    /// <summary>
    /// Versión del mod instalado en esa carpeta de Geometry Dash, o null si no está instalado o
    /// no se puede leer.
    /// </summary>
    public static Version? VersionInstalada(string carpetaGd)
    {
        string ruta = RutaInstalada(carpetaGd);
        return File.Exists(ruta) ? LeerVersionDeGeode(ruta) : null;
    }

    /// <summary>
    /// Lee la versión del mod.json de un .geode, y solo si su id es <see cref="IdDelMod"/>.
    /// Devuelve null si el archivo no es un ZIP, no tiene mod.json o es de otro mod.
    /// </summary>
    public static Version? LeerVersionDeGeode(string ruta)
    {
        try
        {
            using var zip = ZipFile.OpenRead(ruta);
            var entrada = zip.GetEntry("mod.json");
            if (entrada == null) return null;

            using var flujo = entrada.Open();
            using var json = JsonDocument.Parse(flujo);
            var raiz = json.RootElement;

            string id = raiz.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            if (!id.Equals(IdDelMod, StringComparison.OrdinalIgnoreCase)) return null;

            string version = raiz.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            return LeerVersion(version);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Convierte una versión de Geode o una etiqueta de GitHub ("v1.2.0", "1.2", "v1.2.0-beta.1")
    /// en un <see cref="Version"/> de tres números comparable. Se ignoran la "v" inicial y lo que
    /// vaya detrás de "-" o "+". Devuelve null si no es una versión.
    /// </summary>
    public static Version? LeerVersion(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;

        string limpio = texto.Trim().TrimStart('v', 'V');
        int corte = limpio.IndexOfAny(new[] { '-', '+', ' ' });
        if (corte >= 0) limpio = limpio.Substring(0, corte);
        if (!limpio.Contains('.')) limpio += ".0";

        if (!Version.TryParse(limpio, out var version)) return null;
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    // ================================================================
    // INSTALACIÓN
    // ================================================================
    /// <summary>
    /// Consigue el .geode que se va a instalar: el de la última release de GitHub y, si no se
    /// puede, el incluido en el programa.
    /// </summary>
    /// <returns>El archivo y, aparte, si se intentó GitHub y falló (para decirlo en la consola).
    /// Null en el archivo si tampoco está la copia incluida.</returns>
    public async Task<(ArchivoDelMod? Archivo, bool FalloGitHub)> ConseguirModAsync(CancellationToken ct = default)
    {
        var release = await ObtenerUltimaReleaseAsync(ct);
        if (release != null)
        {
            var descargado = await DescargarAsync(release, ct);
            if (descargado != null) return (descargado, false);
        }

        return (ObtenerIncluido(), true);
    }

    /// <summary>
    /// Copia el .geode a la carpeta de mods de Geode de esa instalación de Geometry Dash. Se copia
    /// a un temporal y se pone en su sitio de una vez, para no dejar un mod a medias. Si el
    /// archivo era una descarga, se borra después. Las excepciones de disco se propagan (por
    /// ejemplo, si Windows tiene el archivo bloqueado porque el juego está abierto).
    /// </summary>
    public static void Instalar(ArchivoDelMod archivo, string carpetaGd)
    {
        string destino = RutaInstalada(carpetaGd);
        string temporal = destino + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            File.Copy(archivo.Ruta, temporal, overwrite: true);
            File.Move(temporal, destino, overwrite: true);
        }
        finally
        {
            // Si el Move falló, el temporal no debe quedarse en la carpeta de mods.
            BorrarSinErrores(temporal);
            if (archivo.EsTemporal) BorrarSinErrores(archivo.Ruta);
        }
    }

    /// <summary>
    /// Borra un archivo temporal propio ignorando los errores: si no se puede, no afecta a nada.
    /// </summary>
    public static void BorrarSinErrores(string ruta)
    {
        try { if (File.Exists(ruta)) File.Delete(ruta); } catch { }
    }
}
