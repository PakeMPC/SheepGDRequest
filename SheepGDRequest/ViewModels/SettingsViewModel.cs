using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SheepGDRequest.Models;
using SheepGDRequest.Services;
using Avalonia.Controls;

namespace SheepGDRequest.ViewModels;


/// <summary>
/// Una opción del selector de idioma: el código ("es", "en" o "pt") y el nombre que se muestra.
/// </summary>
public class LanguageItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}


/// <summary>
/// ViewModel del panel de Ajustes: expone SettingsModel a la vista, carga y guarda settings.json
/// y atiende los botones del panel.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    public Services.WidgetThemeManager ThemeManager { get; } = new();

    [ObservableProperty]
    private SettingsModel _settings;

    /// <summary>
    /// Indica si hay algún enlace de Discord. La sección del token de Discord y sus opciones solo
    /// se muestran en ese caso: a quien no usa Discord no le hace falta un token de bot.
    /// </summary>
    public bool HayEnlaceDiscord => Settings.ChatLinks.Any(l => l.Platform == ChatPlatform.Discord);

    /// <summary>
    /// Se dispara cuando LoadSettingsAsync reemplaza el objeto Settings entero.
    /// </summary>
    partial void OnSettingsChanged(SettingsModel value)
    {
        EngancharEnlaces(value);
        OnPropertyChanged(nameof(HayEnlaceDiscord));
    }

    /// <summary>
    /// Vigila la lista de enlaces para recalcular HayEnlaceDiscord.
    ///
    /// Hacen falta dos suscripciones: la colección avisa de altas y bajas, pero cambiar la
    /// plataforma de un enlace que ya existe solo notifica al propio enlace. Sin la segunda, pasar
    /// un enlace a Discord no haría aparecer la sección.
    /// </summary>
    private void EngancharEnlaces(SettingsModel settings)
    {
        settings.ChatLinks.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (ChatLinkModel viejo in e.OldItems) viejo.PropertyChanged -= EnlaceCambiado;

            if (e.NewItems != null)
                foreach (ChatLinkModel nuevo in e.NewItems) nuevo.PropertyChanged += EnlaceCambiado;

            OnPropertyChanged(nameof(HayEnlaceDiscord));
        };

        foreach (var link in settings.ChatLinks)
            link.PropertyChanged += EnlaceCambiado;
    }

    private void EnlaceCambiado(object? remitente, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatLinkModel.Platform))
            OnPropertyChanged(nameof(HayEnlaceDiscord));
    }

    // === LISTAS PARA LOS DESPLEGABLES ===
    public ChatPlatform[] AvailablePlatforms => (ChatPlatform[])Enum.GetValues(typeof(ChatPlatform));
    public DifficultyFilterCondition[] AvailableConditions => (DifficultyFilterCondition[])Enum.GetValues(typeof(DifficultyFilterCondition));
    public DifficultyRating[] AvailableDifficulties => (DifficultyRating[])Enum.GetValues(typeof(DifficultyRating));
    public TimeUnit[] AvailableTimeUnits => (TimeUnit[])Enum.GetValues(typeof(TimeUnit));

    // === SISTEMA DE IDIOMAS ===
    public LanguageItem[] AvailableLanguages { get; } = new[]
    {
        new LanguageItem { Code = "es", Name = "Español (ES)" },
        new LanguageItem { Code = "en", Name = "English (EN)" },
        new LanguageItem { Code = "pt", Name = "Português (PT)" }
    };

    private LanguageItem? _selectedLanguage;
    public LanguageItem? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value) && value != null)
            {
                // Si el usuario cambia el idioma en el desplegable, se aplica y se guarda.
                if (Settings != null && Settings.AppLanguage != value.Code)
                {
                    Settings.AppLanguage = value.Code;
                    I18n.ChangeLanguage(value.Code);

                    // Los textos de XAML con {i18n:Tr}, los desplegables de enums y las
                    // notificaciones del panel se actualizan solos, porque dependen de
                    // I18nNotifier.LanguageTrigger. Los nombres de los temas se calculan al
                    // escanear, así que se vuelven a escanear.
                    VolverAEscanearTemas();

                    // Se guarda con el servicio y no con SaveSettingsAsync, que además reconecta
                    // todos los enlaces de chat: cambiar de idioma no tiene por qué cortarlos.
                    _ = _settingsService.SaveSettingsAsync(Settings);
                }
            }
        }
    }

    /// <summary>
    /// Se completa cuando LoadSettingsAsync ha terminado y Settings ya es el objeto leído de disco,
    /// no el que crea en blanco el constructor.
    ///
    /// Existe porque la carga es asíncrona y un constructor no puede esperarla: quien necesite los
    /// ajustes reales (por ejemplo, MainWindow al abrirse) espera a esta tarea antes de leerlos.
    /// </summary>
    public Task Cargado { get; }

    public SettingsViewModel()
    {
        _settingsService = new SettingsService();
        Settings = new SettingsModel();
        Cargado = LoadSettingsAsync();
    }

    public bool CanAddCommand => Settings.RequestCommands.Count < 3;

    private async Task LoadSettingsAsync()
    {
        // 1. Se lee settings.json en un objeto aparte; Settings no se sustituye hasta el paso 3.
        var loadedSettings = await _settingsService.LoadSettingsAsync();

        // 1.b Se vuelven a escanear los temas, ya con el idioma del usuario aplicado. ThemeManager
        // se crea en un inicializador de propiedad, que se ejecuta antes que el constructor y que
        // esta carga, así que sus nombres salían en el idioma por defecto.
        // _settingsService.LoadSettingsAsync ya ha llamado a I18n.ChangeLanguage, así que este
        // escaneo sale en el idioma correcto. Va antes del paso 2 porque ese paso empareja por
        // FileName con las instancias que crea este escaneo.
        //
        // 2. Los temas guardados se emparejan con las instancias de las listas antes de arrancar
        // nada. Un fallo aquí no debe impedir que se usen los ajustes leídos.
        try
        {
            ThemeManager.ScanThemes();
            EmparejarTemas(loadedSettings);
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_ThemeReadError"), AppPaths.WidgetThemesDir, ex.Message));
        }

        // 3. Ahora sí se sustituye Settings por la configuración leída.
        Settings = loadedSettings;

        // Si settings.json no se pudo leer, se avisa también en el panel: en la consola es fácil
        // que pase desapercibido, y el usuario ve sus ajustes en blanco.
        if (_settingsService.CopiaDelArchivoIlegible != null)
        {
            _ = MainWindowViewModel.Instance?.NotificationService.AddNotificationAsync(
                "Warning", "Notif_SettingsUnreadable", I18n.Tr("Default_System"), "");
        }

        // 4. El desplegable de idioma se empareja solo si ya hay un idioma elegido.
        //
        // No se usa "?? AvailableLanguages[0]" para evitar que el desplegable salga vacío: el
        // setter de SelectedLanguage escribe en Settings.AppLanguage y guarda el JSON, así que
        // convertiría el valor vacío ("todavía no se ha elegido") en "es" durante la propia carga,
        // y el selector del primer arranque no llegaría a mostrarse.
        //
        // Si está vacío, se queda en null a propósito: el desplegable sale vacío un momento y
        // MainWindow lo rellena en cuanto el usuario elige en el selector.
        if (!string.IsNullOrEmpty(Settings.AppLanguage))
        {
            SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Code == Settings.AppLanguage)
                               ?? AvailableLanguages[0];
        }

        OnPropertyChanged(nameof(CanAddCommand));

        // 5. Con los temas ya emparejados, se arranca el servidor de widgets si está activado.
        if (Settings.IsWidgetsServerEnabled)
        {
            Services.WidgetServerService.Instance?.StartServer(Settings.WidgetServerPort);
        }
    }

    /// <summary>
    /// Sustituye los temas elegidos de <paramref name="s"/> por las instancias de las listas de
    /// ThemeManager con el mismo FileName (o la primera de la lista si ese tema ya no existe). Hace
    /// falta después de cada escaneo: los desplegables solo reconocen como elegido un objeto que
    /// esté en su lista.
    /// </summary>
    private void EmparejarTemas(SettingsModel s)
    {
        if (s.SelectedNotificationTheme != null)
            s.SelectedNotificationTheme = ThemeManager.NotificationThemes.FirstOrDefault(t => t.FileName == s.SelectedNotificationTheme.FileName) ?? ThemeManager.NotificationThemes.FirstOrDefault();

        if (s.SelectedPlayingTheme != null)
            s.SelectedPlayingTheme = ThemeManager.PlayingThemes.FirstOrDefault(t => t.FileName == s.SelectedPlayingTheme.FileName) ?? ThemeManager.PlayingThemes.FirstOrDefault();

        if (s.SelectedQueueTheme != null)
            s.SelectedQueueTheme = ThemeManager.QueueThemes.FirstOrDefault(t => t.FileName == s.SelectedQueueTheme.FileName) ?? ThemeManager.QueueThemes.FirstOrDefault();

        if (s.SelectedUnifiedTheme != null)
            s.SelectedUnifiedTheme = ThemeManager.UnifiedThemes.FirstOrDefault(t => t.FileName == s.SelectedUnifiedTheme.FileName) ?? ThemeManager.UnifiedThemes.FirstOrDefault();
    }

    /// <summary>
    /// Vuelve a escanear los temas (sus nombres se calculan en el idioma activo) y a emparejar los
    /// elegidos. Se usa al cambiar de idioma.
    /// </summary>
    private void VolverAEscanearTemas()
    {
        try
        {
            // Los nombres de archivo se guardan antes de escanear: al vaciar las listas, cada
            // desplegable pierde su elemento elegido y lo devuelve a Settings como null.
            string? notificacion = Settings.SelectedNotificationTheme?.FileName;
            string? jugando = Settings.SelectedPlayingTheme?.FileName;
            string? cola = Settings.SelectedQueueTheme?.FileName;
            string? unificado = Settings.SelectedUnifiedTheme?.FileName;

            ThemeManager.ScanThemes();

            Settings.SelectedNotificationTheme = BuscarTema(ThemeManager.NotificationThemes, notificacion);
            Settings.SelectedPlayingTheme = BuscarTema(ThemeManager.PlayingThemes, jugando);
            Settings.SelectedQueueTheme = BuscarTema(ThemeManager.QueueThemes, cola);
            Settings.SelectedUnifiedTheme = BuscarTema(ThemeManager.UnifiedThemes, unificado);
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_ThemeReadError"), AppPaths.WidgetThemesDir, ex.Message));
        }
    }

    private static ThemeItem? BuscarTema(System.Collections.ObjectModel.ObservableCollection<ThemeItem> lista, string? fileName)
        => fileName == null ? null : lista.FirstOrDefault(t => t.FileName == fileName) ?? lista.FirstOrDefault();

    /// <summary>
    /// Guarda settings.json sin reconectar los enlaces de chat (al contrario que el comando de
    /// "Guardar cambios"). Lo usa la ventana al cerrarse.
    /// </summary>
    public Task GuardarSinReconectarAsync() => _settingsService.SaveSettingsAsync(Settings);

    // ==========================================
    // GUARDADO Y RECONEXIÓN
    // ==========================================
    /// <summary>
    /// Guarda settings.json y vuelve a conectar todos los enlaces de chat que tienen dirección; los
    /// que no la tienen se desconectan.
    /// </summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await _settingsService.SaveSettingsAsync(Settings);
        Console.WriteLine(I18n.Tr("Log_SettingsSaved"));

        // También los enlaces sin dirección: RestartWidgetAsync cierra la conexión que tuvieran
        // (por ejemplo, si se acaba de borrar su URL) y no abre ninguna.
        foreach (var link in Settings.ChatLinks.ToList())
        {
            await SheepChatListener.RestartWidgetAsync(link);
        }

        AvisarSiNoHayEnlaces();
    }

    // ==========================================
    // BORRAR CACHÉ
    // ==========================================
    /// <summary>
    /// Cierra las conexiones de Playwright, borra los perfiles del navegador (las sesiones
    /// guardadas) y vuelve a conectar esos enlaces; los de "Otro" vuelven a pedir el inicio de
    /// sesión. TikTok y Discord no usan el navegador y no se tocan. Para borrar la sesión de un
    /// solo enlace "Otro" está <see cref="ReLoginChatLink"/>.
    /// </summary>
    [RelayCommand]
    private async Task ClearCache()
    {
        try
        {
            Console.WriteLine(I18n.Tr("Log_ClosingConnectionsForCache"));

            // Espera a que los Chromium se cierren: con un perfil abierto, el borrado fallaría.
            await SheepChatListener.DetenerNavegadoresAsync();

            string path = AppPaths.BrowserProfilesDir;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                Console.WriteLine(I18n.Tr("Log_CacheClearedSuccess"));
            }
            else
            {
                Console.WriteLine(I18n.Tr("Log_CacheAlreadyClean"));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_CacheClearError"), ex.Message));
        }

        // Se reconecta aunque el borrado haya fallado: las fuentes ya estaban detenidas, y
        // dejarlas así sería peor que conectarlas con la sesión que hubiera quedado.
        foreach (var link in Settings.ChatLinks.ToList())
        {
            bool usaNavegador = link.Platform != ChatPlatform.TikTok && link.Platform != ChatPlatform.Discord;
            if (usaNavegador && !string.IsNullOrWhiteSpace(link.Url))
                await SheepChatListener.RestartWidgetAsync(link);
        }
    }

    [RelayCommand]
    private async Task ReconnectChatLink(ChatLinkModel link)
    {
        if (link != null && !string.IsNullOrWhiteSpace(link.Url))
        {
            await SheepChatListener.RestartWidgetAsync(link);
        }
    }

    /// <summary>
    /// Botón de volver a iniciar sesión de un enlace "Otro": borra solo su sesión guardada y lo
    /// reconecta, lo que vuelve a abrir la ventana de inicio de sesión.
    /// </summary>
    [RelayCommand]
    private async Task ReLoginChatLink(ChatLinkModel link)
    {
        if (link != null) await SheepChatListener.VolverAIniciarSesionAsync(link);
    }

    // ==========================================
    // LISTAS (ENLACES, COMANDOS Y LISTA NEGRA)
    // ==========================================
    // Como máximo, 10 enlaces.
    [RelayCommand]
    private void AddChatLink()
    {
        if (Settings.ChatLinks.Count < 10) Settings.ChatLinks.Add(new ChatLinkModel());
    }

    [RelayCommand]
    private async Task RemoveChatLink(ChatLinkModel? link)
    {
        if (link == null || !Settings.ChatLinks.Contains(link)) return;

        // Se permite quitar el último enlace: la lista vacía es un estado válido.
        Settings.ChatLinks.Remove(link);

        // Quitar el enlace de la lista no lo desconecta, así que se detiene aparte.
        await SheepChatListener.StopWidgetAsync(link);

        Console.WriteLine(string.Format(I18n.Tr("Log_ChatLinkRemoved"), link.Platform));
        AvisarSiNoHayEnlaces();
    }

    /// <summary>
    /// Si no queda ningún enlace con dirección, recuerda en la consola que el bot está en pausa.
    /// El mismo aviso sale al arrancar sin enlaces (SheepChatListener.StartListeningAsync).
    /// </summary>
    private void AvisarSiNoHayEnlaces()
    {
        if (Settings.ChatLinks.All(l => string.IsNullOrWhiteSpace(l.Url)))
            Console.WriteLine(I18n.Tr("Log_BotPaused"));
    }

    [RelayCommand]
    private void AddCommand()
    {
        if (Settings.RequestCommands.Count < 3)
        {
            Settings.RequestCommands.Add(new RequestCommandModel { CommandText = "!" });
            OnPropertyChanged(nameof(CanAddCommand));
        }
    }

    // Siempre queda al menos un comando: con uno solo, el botón de quitar no hace nada.
    [RelayCommand]
    private void RemoveCommand(RequestCommandModel? cmd)
    {
        if (cmd != null && Settings.RequestCommands.Contains(cmd) && Settings.RequestCommands.Count > 1)
        {
            Settings.RequestCommands.Remove(cmd);
            OnPropertyChanged(nameof(CanAddCommand));
        }
    }

    // ==========================================
    // HERRAMIENTAS
    // ==========================================

    /// <summary>
    /// Abre una carpeta o un archivo con el explorador del sistema. Se elige el programa según el
    /// sistema en lugar de usar UseShellExecute porque, en Linux, ese atajo no es igual de fiable
    /// con carpetas que con URL.
    /// </summary>
    private static void AbrirRuta(string ruta)
    {
        string programa = OperatingSystem.IsWindows() ? "explorer.exe"
                        : OperatingSystem.IsMacOS()   ? "open"
                        : "xdg-open";

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = programa,
            Arguments = $"\"{ruta}\"",
            UseShellExecute = false
        });
    }

    /// <summary>
    /// Localiza el ejecutable de SheepGDEditor.
    ///
    /// Estructura de la instalación:
    ///     SheepGD/
    ///       ├── SheepGDEditor/    &lt;- ejecutable del editor
    ///       └── SheepGDRequest/   &lt;- este programa
    /// En una instalación real basta con subir un nivel y entrar en SheepGDEditor.
    ///
    /// En desarrollo eso no sirve, porque el programa se ejecuta desde bin/Debug/net8.0. Por eso
    /// hay candidatos que apuntan a la salida de compilación del Editor.
    /// </summary>
    private static string? BuscarEjecutableDelEditor()
    {
        string exe = OperatingSystem.IsWindows() ? "SheepGDEditor.exe" : "SheepGDEditor";
        string baseDir = AppPaths.AppDir;

        string[] candidatos =
        {
            // Instalación real
            Path.Combine(baseDir, "..", "SheepGDEditor", exe),

            // Desarrollo: sube 4 niveles, hasta la carpeta de la solución.
            Path.Combine(baseDir, "..", "..", "..", "..", "SheepGDEditor", "bin", "Debug", "net8.0", exe),
            Path.Combine(baseDir, "..", "..", "..", "..", "SheepGDEditor", "bin", "Release", "net8.0", exe),
        };

        foreach (var candidato in candidatos)
        {
            string completa = Path.GetFullPath(candidato);
            if (File.Exists(completa)) return completa;
        }

        return null;
    }

    [RelayCommand]
    private void OpenThemesFolder()
    {
        try
        {
            // La carpeta puede no existir todavía.
            Directory.CreateDirectory(AppPaths.WidgetThemesDir);
            AbrirRuta(AppPaths.WidgetThemesDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_OpenPathError"), AppPaths.WidgetThemesDir, ex.Message));
        }
    }

    [RelayCommand]
    private void OpenThemeEditor()
    {
        string? editor = BuscarEjecutableDelEditor();

        if (editor == null)
        {
            string esperada = Path.GetFullPath(Path.Combine(AppPaths.AppDir, "..", "SheepGDEditor"));
            Console.WriteLine(string.Format(I18n.Tr("Log_EditorNotFound"), esperada));
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editor,
                WorkingDirectory = Path.GetDirectoryName(editor) ?? AppPaths.AppDir,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_OpenPathError"), editor, ex.Message));
        }
    }

    // ==========================================
    // MOD DE GEODE: INSTALACIÓN Y ACTUALIZACIÓN
    // ==========================================
    private readonly GeodeModService _modService = new();

    /// <summary>
    /// Instala el mod de Geode en la carpeta de mods de Geometry Dash. Propone la carpeta
    /// guardada o la detectada, y si no hay ninguna, abre el selector de carpetas.
    ///
    /// El mod se descarga de la última release de GitHub (GeodeModService). Si no se puede, se
    /// instala la copia que viaja con el programa, salvo que la ya instalada sea más nueva: en
    /// ese caso no se toca, para no volver a una versión anterior.
    /// </summary>
    [RelayCommand]
    private async Task InstallGeodeMod()
    {
        try
        {
            var ventana = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (ventana == null) return;

            // 1. Carpeta conocida: primero la guardada y, si no, la detectada.
            string? candidata = CarpetaDeGdConocida();

            // 2. Si hay una, se pide confirmación; si no, se abre directamente el selector.
            if (candidata != null)
            {
                var respuesta = await Helpers.ConfirmFolderDialog.MostrarAsync(ventana, candidata);

                if (respuesta == Helpers.RespuestaCarpeta.Cancelar) return;
                if (respuesta == Helpers.RespuestaCarpeta.ElegirOtra) candidata = await PedirCarpetaAsync(ventana);
            }
            else
            {
                candidata = await PedirCarpetaAsync(ventana);
            }

            if (candidata == null) return; // El usuario canceló el selector

            // 3. Copiar el .geode no instala Geode: sin el cargador, el archivo no tiene efecto y
            // parecería que el botón no ha hecho nada.
            string carpetaGeode = Path.Combine(candidata, "geode");
            if (!Directory.Exists(carpetaGeode))
            {
                await AvisarAsync("Warning", "Notif_ModGeodeMissing");
                return;
            }

            // 4. El .geode: el de la última release de GitHub o, si no se puede, el incluido.
            var (archivo, falloGitHub) = await _modService.ConseguirModAsync();
            if (archivo == null)
            {
                await AvisarAsync("Warning", "Notif_ModFileMissing");
                return;
            }

            if (falloGitHub)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_ModReleaseUnavailable"), Mostrar(archivo.Version)));

                // La copia incluida puede ser más antigua que la instalada (por ejemplo, si el mod
                // ya se actualizó desde GitHub y ahora no hay red): no se instala encima.
                var instalada = GeodeModService.VersionInstalada(candidata);
                if (instalada != null && archivo.Version < instalada)
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_ModSkippedOlder"), Mostrar(archivo.Version), Mostrar(instalada)));
                    await AvisarAsync("Info", "Notif_ModAlreadyNewer", Mostrar(instalada));
                    return;
                }
            }

            // 5. Copia. Si falla (lo normal: Windows tiene el archivo bloqueado porque el juego
            // está abierto), se avisa en el panel y no se recuerda la carpeta.
            try
            {
                GeodeModService.Instalar(archivo, candidata);
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_ModInstallError"), ex.Message));
                await AvisarAsync("Warning", "Notif_ModInstallFailed");
                return;
            }

            Console.WriteLine(string.Format(
                I18n.Tr(archivo.Origen == OrigenDelMod.GitHub ? "Log_ModInstalledFromGitHub" : "Log_ModInstalledBundled"),
                Mostrar(archivo.Version), GeodeModService.RutaInstalada(candidata)));

            // 6. Se recuerda la carpeta. Se guarda con el servicio directamente y no con
            // SaveSettingsAsync, porque ese método también reconecta todos los enlaces de chat,
            // algo que no tiene que ver con instalar un mod.
            Settings.GeometryDashPath = candidata;
            await _settingsService.SaveSettingsAsync(Settings);

            await AvisarAsync("Success", "Notif_ModInstalled");
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_ModInstallError"), ex.Message));
        }
    }

    /// <summary>
    /// Comprueba si el mod instalado en Geometry Dash es más antiguo que la última release de
    /// GitHub y, si lo es, muestra el aviso de actualización. Al pulsar "Actualizar", descarga la
    /// release y la instala en la misma carpeta. La llama MainWindow una vez por arranque, cuando
    /// ya se han cargado los ajustes.
    ///
    /// Solo mira la carpeta guardada o la detectada, y solo avisa si el mod ya está instalado en
    /// ella: quien no lo ha instalado nunca no recibe avisos. Sin red, o si GitHub no responde, no
    /// muestra nada (una línea en la consola). No lanza excepciones.
    /// </summary>
    /// <param name="ventana">Ventana sobre la que se muestra el aviso.</param>
    public async Task ComprobarActualizacionDelModAsync(Window ventana)
    {
        try
        {
            string? carpetaGd = CarpetaDeGdConocida();
            if (carpetaGd == null) return;

            var instalada = GeodeModService.VersionInstalada(carpetaGd);
            if (instalada == null) return;

            var release = await _modService.ObtenerUltimaReleaseAsync();
            if (release == null)
            {
                Console.WriteLine(I18n.Tr("Log_ModUpdateCheckFailed"));
                return;
            }

            if (release.Version <= instalada) return;

            Console.WriteLine(string.Format(I18n.Tr("Log_ModUpdateAvailable"), Mostrar(release.Version), Mostrar(instalada)));

            var respuesta = await Helpers.UpdateModDialog.MostrarAsync(ventana, Mostrar(release.Version), Mostrar(instalada));
            if (respuesta != Helpers.RespuestaActualizacion.Actualizar) return;

            // La actualización solo vale con la release: la copia incluida no es más nueva que lo
            // instalado, así que aquí no hay alternativa.
            var archivo = await _modService.DescargarAsync(release);
            if (archivo == null)
            {
                await AvisarAsync("Warning", "Notif_ModUpdateFailed");
                return;
            }

            try
            {
                GeodeModService.Instalar(archivo, carpetaGd);
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_ModInstallError"), ex.Message));
                await AvisarAsync("Warning", "Notif_ModInstallFailed");
                return;
            }

            Console.WriteLine(string.Format(I18n.Tr("Log_ModInstalledFromGitHub"),
                Mostrar(archivo.Version), GeodeModService.RutaInstalada(carpetaGd)));
            await AvisarAsync("Success", "Notif_ModUpdated", Mostrar(archivo.Version));
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_ModInstallError"), ex.Message));
        }
    }

    /// <summary>
    /// La carpeta de Geometry Dash que se conoce sin preguntar: la guardada en los ajustes si
    /// todavía existe y, si no, la que detecta GeometryDashLocator. Null si no hay ninguna.
    /// </summary>
    private string? CarpetaDeGdConocida()
    {
        if (!string.IsNullOrWhiteSpace(Settings.GeometryDashPath) && Directory.Exists(Settings.GeometryDashPath))
            return Settings.GeometryDashPath;

        return GeometryDashLocator.Detectar();
    }

    /// <summary>
    /// Una versión como la escribe Geode ("v1.2.0"), para la consola, el panel y el aviso.
    /// </summary>
    private static string Mostrar(Version version) => "v" + version.ToString(3);

    /// <summary>
    /// Abre el selector de carpetas. Devuelve null si el usuario cancela. No se le pasa
    /// SuggestedStartLocation porque en Linux lo dibuja el portal XDG del escritorio, que lo
    /// ignora; para proponer una carpeta está el diálogo de confirmación.
    /// </summary>
    private static async Task<string?> PedirCarpetaAsync(Window ventana)
    {
        var elegidas = await ventana.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = I18n.Tr("UI_PickGdFolder"),
                AllowMultiple = false
            });

        return elegidas.Count == 0 ? null : elegidas[0].Path.LocalPath;
    }

    /// <summary>
    /// Notificación para acciones que el usuario acaba de pulsar. No depende de NotifySystemEvents
    /// a propósito: al pulsar un botón, la respuesta tiene que llegar siempre.
    /// </summary>
    private static async Task AvisarAsync(string tipo, string llave, string dato = "")
    {
        // El texto lleva el nombre del sistema en {0} y el dato (por ejemplo, una versión) en {1},
        // igual que lo rellena NotificationService.
        Console.WriteLine(string.Format(I18n.Tr(llave), I18n.Tr("Default_System"), dato));
        await MainWindowViewModel.Instance.NotificationService.AddNotificationAsync(
            tipo, llave, I18n.Tr("Default_System"), dato);
    }

    /// <summary>
    /// Abre en el navegador el portal para desarrolladores de Discord, donde el usuario crea su bot
    /// y obtiene el token.
    /// </summary>
    [RelayCommand]
    private void OpenDiscordTutorial()
    {
        const string url = "https://discord.com/developers/applications";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true // Necesario para abrir la URL con el navegador
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_OpenPathError"), url, ex.Message));
        }
    }

    [RelayCommand]
    private void AddBlacklistId() => Settings.BlacklistedIds.Add(new BlacklistIdModel { LevelIdText = "" });

    [RelayCommand]
    private void RemoveBlacklistId(BlacklistIdModel? idModel)
    {
        if (idModel != null && Settings.BlacklistedIds.Contains(idModel)) Settings.BlacklistedIds.Remove(idModel);
    }

    /// <summary>
    /// Vacía la cola de espera. El nivel actual y el historial no cambian.
    /// </summary>
    [RelayCommand]
    private async Task ClearAllRequests()
    {
        var vm = MainWindowViewModel.Instance;
        await vm.QueueService.ModificarAsync(state =>
        {
            state.Queue.Clear();
            return true;
        });
        Console.WriteLine(I18n.Tr("Log_QueueCleared"));
    }
    [RelayCommand]
    private async Task ClearNotifications()
    {
        await MainWindowViewModel.Instance.NotificationService.ClearAllAsync();
        Console.WriteLine(I18n.Tr("Log_NotificationsCleared"));
    }

    [RelayCommand]
    private async Task CopyWidgetUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clipboard = desktop.MainWindow?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(url);
            }
        }
    }
}
