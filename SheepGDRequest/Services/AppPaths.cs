using System;
using System.IO;

namespace SheepGDRequest.Services;

/// <summary>
/// Punto único donde se decide en qué carpetas escribe la aplicación.
///
/// Antes, todas las rutas partían de AppDomain.CurrentDomain.BaseDirectory, es decir, de la
/// carpeta del ejecutable. Eso funciona si el programa está en una carpeta del usuario, pero en
/// Windows, instalado en "Archivos de programa", esa carpeta es de solo lectura para un usuario
/// sin permisos de administrador y la aplicación no podría guardar ni los ajustes.
///
/// Regla: la carpeta del ejecutable es de solo lectura (recursos y temas de fábrica). Todo lo
/// que se escribe va a <see cref="DataDir"/>.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Carpeta de datos del usuario:
    ///   Windows → %LOCALAPPDATA%\SheepGDRequest   (C:\Users\X\AppData\Local\SheepGDRequest)
    ///   Linux   → ~/.local/share/SheepGDRequest   (respeta $XDG_DATA_HOME si está definido)
    ///   macOS   → ~/Library/Application Support/SheepGDRequest
    /// </summary>
    public static string DataDir { get; }

    public static string SettingsFile { get; }
    public static string QueueFile { get; }
    public static string NotificationsFile { get; }
    public static string ObsNotificationsFile { get; }
    public static string ObsExportsDir { get; }
    public static string BrowserProfilesDir { get; }
    public static string WidgetThemesDir { get; }

    /// <summary>
    /// Carpeta del ejecutable, de solo lectura. En el AppImage de Linux, además, cambia en cada
    /// arranque, porque es un montaje temporal.
    /// </summary>
    public static string AppDir => AppContext.BaseDirectory;

    // LocalApplicationData ya corresponde a $XDG_DATA_HOME (o ~/.local/share) en Linux y a
    // Application Support en macOS; .NET resuelve la ruta de cada sistema.
    static AppPaths()
    {
        string raiz = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Si el sistema no devuelve ninguna ruta, se usa la carpeta del ejecutable, como en las
        // versiones antiguas, en lugar de fallar.
        if (string.IsNullOrWhiteSpace(raiz)) raiz = AppContext.BaseDirectory;

        DataDir              = Path.Combine(raiz, "SheepGDRequest");
        SettingsFile         = Path.Combine(DataDir, "settings.json");
        QueueFile            = Path.Combine(DataDir, "queue_data.json");
        NotificationsFile    = Path.Combine(DataDir, "notifications.json");
        ObsNotificationsFile = Path.Combine(DataDir, "obs_notifications.txt");
        ObsExportsDir        = Path.Combine(DataDir, "OBS_Exports");
        BrowserProfilesDir   = Path.Combine(DataDir, "BrowserProfiles");
        WidgetThemesDir      = Path.Combine(DataDir, "WidgetThemes");
    }

    /// <summary>
    /// Crea las carpetas, migra los datos de la ubicación antigua y copia los temas de fábrica.
    /// Debe llamarse antes de construir cualquier servicio, porque varios calculan sus rutas en
    /// el constructor.
    ///
    /// Los mensajes no pasan por I18n porque este método se ejecuta antes de leer settings.json y
    /// todavía no se conoce el idioma del usuario.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(WidgetThemesDir);

            MigrarDesdeLaCarpetaDelEjecutable();
            SembrarTemasDeFabrica();

            Console.WriteLine($"[Datos] Carpeta de datos: {DataDir}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Datos] No se pudo preparar la carpeta de datos: {ex.Message}");
        }
    }

    /// <summary>
    /// Trae la configuración de una instalación antigua la primera vez que se ejecuta. Los archivos
    /// se copian en lugar de moverse, para que los originales sigan disponibles si algo falla.
    /// </summary>
    private static void MigrarDesdeLaCarpetaDelEjecutable()
    {
        // Si ya existen ajustes en la carpeta nueva, la migración ya se hizo.
        if (File.Exists(SettingsFile)) return;

        string viejo = AppContext.BaseDirectory;
        bool migroAlgo = false;

        migroAlgo |= CopiarSiExiste(Path.Combine(viejo, "settings.json"), SettingsFile);
        migroAlgo |= CopiarSiExiste(Path.Combine(viejo, "queue_data.json"), QueueFile);
        migroAlgo |= CopiarSiExiste(Path.Combine(viejo, "notifications.json"), NotificationsFile);
        migroAlgo |= CopiarSiExiste(Path.Combine(viejo, "obs_notifications.txt"), ObsNotificationsFile);

        // Los perfiles de navegador pueden ocupar cientos de MB, así que se mueven en lugar de
        // copiarse (en el mismo disco, la operación es inmediata). Si el movimiento falla (por
        // ejemplo, entre discos distintos), los perfiles se regeneran solos; la única consecuencia
        // es tener que volver a iniciar sesión en la fuente "Otro".
        string perfilesViejos = Path.Combine(viejo, "BrowserProfiles");
        if (Directory.Exists(perfilesViejos) && !Directory.Exists(BrowserProfilesDir))
        {
            try
            {
                Directory.Move(perfilesViejos, BrowserProfilesDir);
                migroAlgo = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Datos] Los perfiles de navegador no se migraron ({ex.Message}). Se regenerarán solos.");
            }
        }

        if (migroAlgo)
        {
            Console.WriteLine("[Datos] Configuración migrada desde la carpeta del programa.");
            Console.WriteLine("[Datos] IMPORTANTE: queue_data.json cambió de sitio. Vuelve a enlazarlo en el mod de Geode.");
        }
    }

    /// <summary>
    /// Copia los temas incluidos con el programa a la carpeta del usuario. Es necesario porque el
    /// tema "default" se resuelve a un default.zip que debe existir en esa carpeta.
    ///
    /// Los temas que ya existen no se sobrescriben, salvo default.zip: ese es del programa, y se
    /// sustituye siempre que el incluido sea distinto, para que las correcciones del tema por
    /// defecto lleguen a quien instaló una versión anterior.
    /// </summary>
    private static void SembrarTemasDeFabrica()
    {
        string origen = Path.Combine(AppContext.BaseDirectory, "WidgetThemes");
        if (!Directory.Exists(origen)) return;

        foreach (string zip in Directory.GetFiles(origen, "*.zip"))
        {
            string destino = Path.Combine(WidgetThemesDir, Path.GetFileName(zip));
            if (!File.Exists(destino))
            {
                File.Copy(zip, destino);
                continue;
            }

            bool esDelPrograma = Path.GetFileName(zip).Equals("default.zip", StringComparison.OrdinalIgnoreCase);
            if (esDelPrograma && !MismoContenido(zip, destino))
            {
                // Copia a un temporal y lo pone en su sitio de una vez, para no dejar un .zip a
                // medias si la copia se interrumpe.
                string temporal = destino + ".tmp";
                File.Copy(zip, temporal, true);
                File.Move(temporal, destino, true);
                Console.WriteLine("[Datos] Tema por defecto actualizado a la versión de este programa.");
            }
        }
    }

    /// <summary>
    /// Indica si dos archivos tienen exactamente el mismo contenido.
    /// </summary>
    private static bool MismoContenido(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>
    /// Copia un archivo solo si existe en el origen y todavía no existe en el destino.
    /// Devuelve true si se copió.
    /// </summary>
    private static bool CopiarSiExiste(string origen, string destino)
    {
        if (!File.Exists(origen) || File.Exists(destino)) return false;
        File.Copy(origen, destino);
        return true;
    }
}
