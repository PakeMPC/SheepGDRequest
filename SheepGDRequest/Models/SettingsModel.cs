using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Linq;

namespace SheepGDRequest.Models;

// =======================================================================
// ENUMERACIONES
// =======================================================================

// Se guardan por nombre en settings.json, y cada valor tiene su texto Enum_<Tipo>_<Valor> en I18n:
// renombrar un valor rompe los ajustes guardados y su traducción.
/// <summary>
/// Plataforma de cada enlace de chat. Otro sirve para cualquier página de chat que no sea de
/// las anteriores.
///
/// El orden es el del desplegable de Ajustes: primero las plataformas de streaming, que es lo que
/// busca la mayoría, y después los agregadores y la opción universal. Se puede cambiar sin romper
/// nada porque settings.json guarda el nombre, no la posición. El primer valor es también el que
/// toma un nombre que no se reconoce al leer los ajustes (SettingsService).
/// </summary>
public enum ChatPlatform
{
    Twitch, YouTube, Kick, TikTok, Discord, SheepChat, SocialStreamNinja, StreamElements, Otro
}

/// <summary>
/// Condición del filtro de dificultad: solo esa dificultad, todas menos esa, hasta esa (como
/// máximo) o a partir de esa (como mínimo).
/// </summary>
public enum DifficultyFilterCondition { Solo, TodosExcepto, Hasta, APartirDe }

/// <summary>
/// Dificultades de Geometry Dash para el filtro. Todos (sin filtro) y DemonTodos (cualquier
/// demon) son opciones del filtro, no dificultades. None es un nivel sin rate o con una
/// dificultad que no se reconoce.
///
/// Nota: GenericListener reconoce los demons por el rango de EasyDemon a ExtremeDemon, así que
/// esos cinco valores deben seguir juntos y en ese orden.
/// </summary>
public enum DifficultyRating { Todos, None, Auto, Easy, Normal, Hard, Harder, Insane, DemonTodos, EasyDemon, MediumDemon, HardDemon, InsaneDemon, ExtremeDemon }

/// <summary>
/// Unidad del tiempo que dura cada nivel con el auto-skip.
/// </summary>
public enum TimeUnit { Segundos, Minutos, Horas }

/// <summary>
/// Filtro por tipo de nivel: todos, solo los clásicos (de cubo, nave, etc.) o solo los de
/// plataforma. Todos va primero porque es también el valor que toman los ajustes que no lo tenían.
/// </summary>
public enum LevelTypeFilter { Todos, SoloClasicos, SoloPlataforma }





// =======================================================================
// SUB-MODELOS DE DATOS
// =======================================================================

/// <summary>
/// Un enlace de chat de Ajustes: la dirección del chat y su plataforma.
///
/// Es ObservableObject porque la interfaz tiene que reaccionar cuando el usuario cambia la
/// plataforma en el desplegable: la sección del token de Discord solo se muestra si hay algún
/// enlace de Discord.
/// </summary>
public partial class ChatLinkModel : ObservableObject
{
    [ObservableProperty]
    private string _url = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsaInicioDeSesion))]
    private ChatPlatform _platform = ChatPlatform.Twitch;

    /// <summary>
    /// Si el enlace guarda una sesión iniciada por el usuario (solo "Otro"). Muestra en Ajustes el
    /// botón de volver a iniciar sesión. No se guarda en settings.json.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsaInicioDeSesion => Platform == ChatPlatform.Otro;

    /// <summary>
    /// Clave con la que este enlace está registrado en el diccionario de conexiones activas de
    /// SheepChatListener. No se guarda en settings.json: solo tiene sentido en ejecución.
    ///
    /// Se guarda aparte porque, si se calculara a partir de la URL actual y el usuario la
    /// editara, ya no coincidiría con la usada al conectar: la conexión anterior quedaría sin
    /// cerrar hasta reiniciar la aplicación y cada petición llegaría dos veces.
    ///
    /// No notifica cambios porque la interfaz no la muestra.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ActiveKey { get; set; } = "";
}

/// <summary>
/// Un comando de petición del chat (por ejemplo, !add). Es observable para que la interfaz se
/// actualice cuando el usuario lo edita. Ajustes admite hasta tres.
/// </summary>
public partial class RequestCommandModel : ObservableObject
{
    [ObservableProperty]
    private string _commandText = "!";
}

/// <summary>
/// Un ID de nivel de la lista negra. Es observable por la misma razón que RequestCommandModel.
/// </summary>
public partial class BlacklistIdModel : ObservableObject
{
    [ObservableProperty]
    private string _levelIdText = "";
}


// =======================================================================
// MODELO PRINCIPAL DE CONFIGURACIÓN
// =======================================================================

/// <summary>
/// Configuración del usuario. SettingsService la guarda en settings.json.
/// </summary>
public partial class SettingsModel : ObservableObject
{
    // --- CONEXIONES Y COMANDOS ---

    /// <summary>
    /// Enlaces de chat configurados. Se conectan al arrancar y al guardar los ajustes.
    /// </summary>
    public ObservableCollection<ChatLinkModel> ChatLinks { get; set; } = new();

    /// <summary>
    /// Comandos con los que los espectadores piden niveles. Por defecto, solo !add.
    /// </summary>
    public ObservableCollection<RequestCommandModel> RequestCommands { get; set; } = new()
    {
        new RequestCommandModel { CommandText = "!add" }
    };

    /// <summary>
    /// Token del bot de Discord del usuario. Se guarda tal cual en settings.json.
    /// </summary>
    [ObservableProperty]
    private string _discordBotToken = "";

    /// <summary>
    /// Si está activo, un nivel pedido por Discord no entra en la cola hasta que alguien
    /// reaccione al mensaje con un visto bueno (✅, ☑️, ✔️, ♥️, ❤️ o 👍). Pensado para quien
    /// tiene moderadores que revisan los niveles antes de que el streamer los juegue.
    /// </summary>
    [ObservableProperty]
    private bool _discordOnlyCheckedLevels = false;

    /// <summary>
    /// Carpeta de Geometry Dash que el usuario confirmó la última vez que instaló el mod. Se
    /// recuerda porque la detección automática no siempre acierta (bibliotecas de Steam en
    /// varios discos, enlaces simbólicos, unidades que se montan y se desmontan), y la carpeta
    /// confirmada por el usuario sí es fiable.
    /// </summary>
    [ObservableProperty]
    private string _geometryDashPath = "";

    // --- RESTRICCIONES DE USUARIO Y LISTA NEGRA ---

    /// <summary>
    /// IDs de niveles que se rechazan si IsBlacklistEnabled está activo. Se comprueban después
    /// de buscar el nivel en GD.
    /// </summary>
    public ObservableCollection<BlacklistIdModel> BlacklistedIds { get; set; } = new();

    /// <summary>
    /// Activa el límite de peticiones por espectador.
    /// </summary>
    [ObservableProperty]
    private bool _isUserLimitEnabled = false;

    /// <summary>
    /// Máximo de niveles de un mismo espectador, contando el nivel en juego y la cola.
    /// </summary>
    [ObservableProperty]
    private int _userMaxRequests = 3;

    /// <summary>
    /// Activa la lista negra de niveles.
    /// </summary>
    [ObservableProperty]
    private bool _isBlacklistEnabled = false;

    // --- FILTROS DE DIFICULTAD ---

    /// <summary>
    /// Condición del filtro de dificultad (por ejemplo, "A partir de" Hard Demon).
    /// </summary>
    [ObservableProperty]
    private DifficultyFilterCondition _difficultyCondition = DifficultyFilterCondition.Solo;

    /// <summary>
    /// Dificultad con la que se compara cada nivel. Todos desactiva el filtro.
    /// </summary>
    [ObservableProperty]
    private DifficultyRating _targetDifficulty = DifficultyRating.Todos;

    /// <summary>
    /// Qué tipo de nivel se acepta: clásicos, de plataforma o los dos. Se comprueba con la duración
    /// que da GD, que en los de plataforma es "Plat.".
    /// </summary>
    [ObservableProperty]
    private LevelTypeFilter _levelTypeFilter = LevelTypeFilter.Todos;

    /// <summary>
    /// Si está activo, se rechazan los niveles sin estrellas.
    /// </summary>
    [ObservableProperty]
    private bool _onlyRatedLevels = false;

    // --- INTERFAZ Y COMPORTAMIENTO GENERAL ---

    /// <summary>
    /// Idioma de la aplicación (es, en o pt). Vacío significa que el usuario aún no ha elegido:
    /// MainWindow muestra el selector de idioma al arrancar.
    /// </summary>
    [ObservableProperty]
    private string _appLanguage = "";

    /// <summary>
    /// Opacidad (0 a 100) de la miniatura del nivel actual, que hace de fondo de la ventana
    /// principal.
    /// </summary>
    [ObservableProperty]
    private int _thumbnailOpacity = 70;

    /// <summary>
    /// Exporta el nivel actual, el siguiente y la cola a archivos .txt para las fuentes de texto
    /// de OBS.
    /// </summary>
    [ObservableProperty]
    private bool _exportObsTexts = false;

    /// <summary>
    /// Si está activo, los comandos solo aceptan IDs numéricos, no nombres de niveles.
    /// </summary>
    [ObservableProperty]
    private bool _onlyIdRequests = false;

    /// <summary>
    /// Toma como ID de nivel cualquier número suelto de 5 a 10 cifras en un mensaje normal, sin
    /// comando.
    /// </summary>
    [ObservableProperty]
    private bool _autoDetectIds = true;

    /// <summary>
    /// Mantiene la ventana principal por encima de las demás ventanas.
    /// </summary>
    [ObservableProperty]
    private bool _alwaysOnTop = true;

    /// <summary>
    /// Permite pedir un nivel que ya está en juego, en la cola o en el historial. Si está apagado,
    /// también se rechazan los niveles ya jugados (pasados con "Siguiente"); los eliminados con la
    /// papelera no cuentan. Ver GenericListener.EsRepetido.
    /// </summary>
    [ObservableProperty]
    private bool _allowRepeatedLevels = false;

    /// <summary>
    /// Interruptor general: abre o cierra la recepción de peticiones.
    /// </summary>
    [ObservableProperty]
    private bool _isRequestsEnabled = true;

    // Los widgets muestran si las peticiones están abiertas, así que se les envía el estado en
    // cuanto cambia.
    partial void OnIsRequestsEnabledChanged(bool value)
    {
        if (IsWidgetsServerEnabled)
        {
            var vm = ViewModels.MainWindowViewModel.Instance;
            if (vm != null)
            {
                Services.WidgetServerService.Instance?.BroadcastQueueStateAsync(vm.CurrentLevel, vm.RequestQueue.ToList());
            }
        }
    }

    /// <summary>
    /// Activa el límite de niveles en la cola.
    /// </summary>
    [ObservableProperty]
    private bool _isGlobalLimitEnabled = false;

    /// <summary>
    /// Máximo de niveles en la cola, sin contar el nivel en juego. Con la cola llena se
    /// rechazan las peticiones nuevas.
    /// </summary>
    [ObservableProperty]
    private int _globalMaxRequests = 20;

    // --- SISTEMA DE AUTO-SKIP (TEMPORIZADOR) ---

    /// <summary>
    /// Activa el salto automático al siguiente nivel cuando se acaba el tiempo de cada uno.
    /// </summary>
    [ObservableProperty]
    private bool _isAutoSkipEnabled = false;

    /// <summary>
    /// Tiempo de cada nivel, en la unidad de AutoSkipFormat.
    /// </summary>
    [ObservableProperty]
    private int _autoSkipValue = 5;

    /// <summary>
    /// Unidad del tiempo de cada nivel (segundos, minutos u horas).
    /// </summary>
    [ObservableProperty]
    private TimeUnit _autoSkipFormat = TimeUnit.Minutos;

    // --- SISTEMA DE NOTIFICACIONES ---

    /// <summary>
    /// Notifica los niveles añadidos a la cola (Notif_LevelAdded).
    /// </summary>
    [ObservableProperty]
    private bool _notifySuccessEvents = true;

    /// <summary>
    /// Notifica las peticiones rechazadas: por los filtros, por los límites, por no ser un ID
    /// cuando solo se aceptan IDs o porque el nivel no existe en GD.
    /// </summary>
    [ObservableProperty]
    private bool _notifyFilterBlocks = true;

    /// <summary>
    /// Notifica los eventos del auto-skip: que se acabó el tiempo del nivel actual con la cola
    /// vacía (Notif_AutoSkipExpired), que se pasa al siguiente (Notif_AutoSkipNext) y que se salta
    /// el nivel caducado al llegar uno nuevo (Notif_AutoSkipNew).
    /// </summary>
    [ObservableProperty]
    private bool _notifyAutoSkipEvents = true;

    /// <summary>
    /// Notifica eventos del sistema: niveles subidos de prioridad, búsquedas en GD sin respuesta,
    /// cola vacía al pasar del último nivel, caídas y vueltas de las conexiones de chat, fallo del
    /// servidor de widgets y peticiones de Discord pendientes de aprobación.
    /// </summary>
    [ObservableProperty]
    private bool _notifySystemEvents = true;

    // --- LECTURA DE VOZ (TTS) ---

    // Qué notificaciones se leen en voz alta; NotificationService decide según el tipo.
    [ObservableProperty]
    private bool _ttsEnableSuccess = false; // Desactivado por defecto: se leería cada nivel añadido

    [ObservableProperty]
    private bool _ttsEnableFilters = true; // Rechazos y avisos (Error y Warning)

    [ObservableProperty]
    private bool _ttsEnableSystem = false; // Tipo Info y las que envía el propio programa

    [ObservableProperty]
    private int _ttsVolume = 80; // De 0 a 100

    // --- INTERFAZ ---
    // Posición y tamaño de la ventana principal; se guardan al cerrarla. -1 en X o en Y significa
    // que todavía no se han guardado, y se usa la posición por defecto.
    [ObservableProperty]
    private int _windowX = -1;

    [ObservableProperty]
    private int _windowY = -1;

    [ObservableProperty]
    private int _windowWidth = 1100;

    [ObservableProperty]
    private int _windowHeight = 750;

    // ==========================================
    // WIDGETS Y OBS (SERVIDOR LOCAL)
    // ==========================================

    /// <summary>
    /// Activa el servidor local que sirve los widgets para OBS.
    /// </summary>
    [ObservableProperty]
    private bool _isWidgetsServerEnabled = false;

    partial void OnIsWidgetsServerEnabledChanged(bool value)
    {
        // Al leer settings.json, el deserializador crea otra instancia y asigna esta propiedad; si
        // no es la instancia de la aplicación, no se hace nada. Al terminar la carga,
        // SettingsViewModel arranca el servidor con los temas ya emparejados.
        var mainSettings = ViewModels.MainWindowViewModel.Instance?.SettingsVM?.Settings;
        if (mainSettings != null && mainSettings != this) return;

        if (value)
        {
            Services.WidgetServerService.Instance?.StartServer(WidgetServerPort);
        }
        else
        {
            Services.WidgetServerService.Instance?.StopServer();
        }
    }

    [ObservableProperty]
    private int _widgetServerPort = 6969;

    // Al cambiar el puerto se actualizan las URL que muestra Ajustes y, si el servidor está activo,
    // se reinicia en el puerto nuevo.
    partial void OnWidgetServerPortChanged(int value)
    {
        OnPropertyChanged(nameof(NotificationWidgetUrl));
        OnPropertyChanged(nameof(PlayingWidgetUrl));
        OnPropertyChanged(nameof(QueueWidgetUrl));
        OnPropertyChanged(nameof(UnifiedWidgetUrl));

        // Igual que en OnIsWidgetsServerEnabledChanged: la instancia que crea el deserializador
        // al leer settings.json no arranca nada; lo hace SettingsViewModel al terminar la carga.
        var mainSettings = ViewModels.MainWindowViewModel.Instance?.SettingsVM?.Settings;
        if (mainSettings != null && mainSettings != this) return;

        if (IsWidgetsServerEnabled)
        {
            Services.WidgetServerService.Instance?.StartServer(value);
        }
    }

    // --- URL de cada widget (solo lectura, para Ajustes) ---
    public string NotificationWidgetUrl => $"http://localhost:{WidgetServerPort}/widget/notification/";
    public string PlayingWidgetUrl => $"http://localhost:{WidgetServerPort}/widget/playing/";
    public string QueueWidgetUrl => $"http://localhost:{WidgetServerPort}/widget/queue/";
    public string UnifiedWidgetUrl => $"http://localhost:{WidgetServerPort}/widget/unified/";

    // Cada widget tiene un interruptor y un tema. El interruptor solo muestra u oculta su URL y su
    // selector de tema en Ajustes; el servidor sirve todos los widgets igualmente. El unificado y
    // los otros tres se excluyen entre sí. Al cambiar de tema, se pide a los widgets abiertos de
    // ese tipo que se recarguen.
    //
    // --- NOTIFICACIONES ---
    [ObservableProperty]
    private bool _isNotificationWidgetEnabled = false;
    [ObservableProperty]
    private Models.ThemeItem? _selectedNotificationTheme;

    partial void OnSelectedNotificationThemeChanged(Models.ThemeItem? value)
    {
        if (IsWidgetsServerEnabled) Services.WidgetServerService.Instance?.BroadcastThemeReloadAsync("notification");
    }


    partial void OnIsNotificationWidgetEnabledChanged(bool value)
    {
        if (value) IsUnifiedWidgetEnabled = false;
    }

    // --- NIVEL JUGANDO ---
    [ObservableProperty]
    private bool _isPlayingWidgetEnabled = false;
    [ObservableProperty]
    private Models.ThemeItem? _selectedPlayingTheme;

    partial void OnSelectedPlayingThemeChanged(Models.ThemeItem? value)
    {
        if (IsWidgetsServerEnabled) Services.WidgetServerService.Instance?.BroadcastThemeReloadAsync("playing");
    }

    partial void OnIsPlayingWidgetEnabledChanged(bool value)
    {
        if (value) IsUnifiedWidgetEnabled = false;
    }

    // --- COLA DE NIVELES ---
    [ObservableProperty]
    private bool _isQueueWidgetEnabled = false;
    [ObservableProperty]
    private Models.ThemeItem? _selectedQueueTheme;

    partial void OnSelectedQueueThemeChanged(Models.ThemeItem? value)
    {
        if (IsWidgetsServerEnabled) Services.WidgetServerService.Instance?.BroadcastThemeReloadAsync("queue");
    }

    partial void OnIsQueueWidgetEnabledChanged(bool value)
    {
        if (value) IsUnifiedWidgetEnabled = false;
    }

    // --- UNIFICADO ---
    [ObservableProperty]
    private bool _isUnifiedWidgetEnabled = false;
    [ObservableProperty]
    private Models.ThemeItem? _selectedUnifiedTheme;

    partial void OnSelectedUnifiedThemeChanged(Models.ThemeItem? value)
    {
        if (IsWidgetsServerEnabled) Services.WidgetServerService.Instance?.BroadcastThemeReloadAsync("unified");
    }

    partial void OnIsUnifiedWidgetEnabledChanged(bool value)
    {
        if (value)
        {
            IsNotificationWidgetEnabled = false;
            IsPlayingWidgetEnabled = false;
            IsQueueWidgetEnabled = false;
        }
    }

}
