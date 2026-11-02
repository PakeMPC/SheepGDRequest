using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using Avalonia.Media.Imaging;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SheepGDRequest.Models;
using SheepGDRequest.Services;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Input.Platform;

namespace SheepGDRequest.ViewModels;

/// <summary>
/// ViewModel de la ventana principal: refleja el estado de la cola (queue_data.json), lleva el
/// temporizador del auto-skip y atiende los botones de la interfaz.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public QueueFileService QueueService { get; } = new();
    public NotificationService NotificationService { get; } = new();
    public TtsService TtsService { get; } = new();

    /// <summary>
    /// Instancia única, para acceder al ViewModel desde cualquier parte de la aplicación (por
    /// ejemplo, desde los servicios que trabajan en segundo plano).
    /// </summary>
    public static MainWindowViewModel Instance { get; private set; } = null!;

    // --- Enfriamiento del cambio de nivel ---
    // Equivale a sheep::trySkip() del mod. Un salto no es instantáneo: se reescribe
    // queue_data.json, el vigilante lo relee, se reenvía por WebSocket, el mod vuelve a cargar el
    // PlayLayer y el precargador cambia de objetivo a media descarga. Encadenar saltos en el mismo
    // segundo rompe los LevelBackgrounds.
    private const int EnfriamientoCambioMs = 1000;

    /// <summary>
    /// Instante, en reloj monotónico, a partir del cual se permite otro cambio. Se usa
    /// TickCount64 y no DateTime.Now porque aquí solo interesan intervalos, y este reloj no
    /// retrocede si el sistema ajusta la hora. Equivale al steady_clock del mod.
    /// </summary>
    private long _proximoCambioPermitidoMs;

    /// <summary>
    /// CanExecute compartido por siguiente, anterior y aleatorio: los tres mueven la cola,
    /// así que comparten un único límite, como las flechas y las teclas Q/E del mod.
    /// </summary>
    private bool PuedeCambiarDeNivel() => Environment.TickCount64 >= _proximoCambioPermitidoMs;

    /// <summary>
    /// Consume el turno y desactiva los tres botones hasta que venza el plazo. Solo se llama
    /// cuando el cambio va a ocurrir.
    /// </summary>
    private void ConsumirTurnoDeCambio()
    {
        _proximoCambioPermitidoMs = Environment.TickCount64 + EnfriamientoCambioMs;

        // Post al hilo de UI: el auto-skip entra desde el temporizador, no desde un clic.
        Dispatcher.UIThread.Post(() =>
        {
            RefrescarBotonesDeCambio();
            DispatcherTimer.RunOnce(RefrescarBotonesDeCambio,
                                    TimeSpan.FromMilliseconds(EnfriamientoCambioMs));
        });
    }

    /// <summary>
    /// Vuelve a evaluar el CanExecute de los tres. Que venza un temporizador anterior no tiene
    /// efecto: PuedeCambiarDeNivel mira la marca real, no el temporizador.
    /// </summary>
    private void RefrescarBotonesDeCambio()
    {
        NextLevelCommand.NotifyCanExecuteChanged();
        PreviousLevelCommand.NotifyCanExecuteChanged();
        RandomLevelCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private LevelRequestModel? _currentLevel;

    [ObservableProperty]
    private Bitmap? _backgroundBitmap;

    [ObservableProperty]
    private bool _isMenuExpanded = false;

    [ObservableProperty]
    private double _menuWidth = 50;


    /// <summary>
    /// Expande o contrae la barra lateral de navegación.
    /// </summary>
    [RelayCommand]
    private void ToggleMenu()
    {
        IsMenuExpanded = !IsMenuExpanded;
        MenuWidth = IsMenuExpanded ? 200 : 50;
    }

    public SettingsViewModel SettingsVM { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainViewVisible))]
    private bool _isConsoleVisible = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainViewVisible))]
    private bool _isSettingsVisible = false;

    public bool IsMainViewVisible => !IsConsoleVisible && !IsSettingsVisible;

    /// <summary>
    /// Mensajes de la consola de depuración de la ventana, el más reciente primero.
    /// </summary>
    public ObservableCollection<string> DebugLogs { get; } = new();

    [RelayCommand]
    private void ToggleConsole()
    {
        IsSettingsVisible = false;
        IsConsoleVisible = !IsConsoleVisible;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        IsConsoleVisible = false;
        IsSettingsVisible = true;
    }

    [RelayCommand]
    private void ShowMainView()
    {
        IsConsoleVisible = false;
        IsSettingsVisible = false;
    }

    /// <summary>
    /// Cola de niveles en espera que muestra la ventana principal.
    /// </summary>
    public ObservableCollection<LevelRequestModel> RequestQueue { get; } = new();
    private readonly Services.ObsExportService _obsExporter = new();


    public MainWindowViewModel()
    {
        Instance = this;

        // Redirige Console.WriteLine a la consola de depuración de la ventana.
        Console.SetOut(new ConsoleRedirector(msg =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                DebugLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
                // Se guardan como máximo 150 mensajes.
                if (DebugLogs.Count > 150) DebugLogs.RemoveAt(DebugLogs.Count - 1);
            });
        }));
        _ = NotificationService.LoadNotificationsAsync();
        QueueService.OnQueueUpdated += ActualizarPantalla;
        _ = CargarListaInicialAsync();
    }

    private async Task CargarListaInicialAsync()
    {
        // El archivo tiene que existir desde el primer arranque: el mod de Geode lo necesita
        // para poder enlazarlo antes de que llegue la primera petición.
        await QueueService.EnsureFileExistsAsync();

        var stateGuardado = await QueueService.ReadQueueStateSafeAsync();
        ActualizarPantalla(stateGuardado);
    }

    /// <summary>
    /// Sincroniza la interfaz con el estado de la cola. Si el nivel actual caducó con la cola
    /// vacía y ya hay niveles en espera, salta al siguiente.
    /// </summary>
    private void ActualizarPantalla(QueueState estado)
    {
        // 1. La caducidad se comprueba antes de actualizar nada. Solo se salta si el nivel en
        // pantalla sigue siendo el que caducó: si el usuario volvió al anterior, el actual ya es
        // otro.
        if (_expiredRequestId != null
            && estado.CurrentLevel?.RequestId == _expiredRequestId
            && estado.Queue.Count > 0)
        {
            _expiredRequestId = null; // Se desactiva el aviso
            Console.WriteLine(I18n.Tr("Log_AutoSkipNewLevelDetected"));

            if (SettingsVM.Settings.NotifyAutoSkipEvents)
                _ = NotificationService.AddNotificationAsync("Info", "Notif_AutoSkipNew", I18n.Tr("Default_System"), "");

            _ = NextLevel(); // Salto al siguiente
            return; // NextLevel escribe la cola y vuelve a llamar a este método
        }

        RequestQueue.Clear();
        foreach (var nivel in estado.Queue)
        {
            RequestQueue.Add(nivel);
        }

        // 2. CurrentLevel se sustituye si cambia la petición (RequestId), o si es la misma pero
        // llegaron datos que se muestran (GDDL y AREDL llegan después del alta). En el segundo
        // caso OnCurrentLevelChanged no reinicia el temporizador.
        if (CurrentLevel?.RequestId != estado.CurrentLevel?.RequestId
            || CurrentLevel?.GddlTier != estado.CurrentLevel?.GddlTier
            || CurrentLevel?.GddlTierColor != estado.CurrentLevel?.GddlTierColor
            || CurrentLevel?.AredlRank != estado.CurrentLevel?.AredlRank)
        {
            CurrentLevel = estado.CurrentLevel;
        }

        // Con una URL vacía, CargarFondoAsync carga el fondo por defecto (placeholder_bg.png). Si
        // la URL es la misma que la anterior, no hace nada.
        if (CurrentLevel != null && CurrentLevel.Name != "WAITING...")
            _ = CargarFondoAsync(CurrentLevel.ThumbnailUrl);
        else
            _ = CargarFondoAsync("");

        RefreshObsTexts();

        // ==========================================
        // WIDGETS (WEBSOCKET)
        // ==========================================
        if (SettingsVM.Settings.IsWidgetsServerEnabled)
        {
            // Se envía una copia de la cola como lista. El "?" evita un fallo si el servidor de
            // widgets todavía no existe.
            WidgetServerService.Instance?.BroadcastQueueStateAsync(CurrentLevel, RequestQueue.ToList());
        }
    }

    private void RefreshObsTexts()
    {
        _obsExporter.UpdateObsFiles(CurrentLevel, RequestQueue, SettingsVM.Settings.ExportObsTexts);
    }

    // === RELOJ DEL AUTO-SKIP ===
    [ObservableProperty]
    private string _timeRemainingText = "";

    [ObservableProperty]
    private bool _isTimerVisible = false;

    private DispatcherTimer? _autoSkipTimer;
    // RequestId del nivel cuyo tiempo se acabó con la cola vacía, o null. Se guarda cuál es, y no
    // solo que hay uno, porque volver al nivel anterior pone ese mismo nivel al frente de la cola:
    // con un simple indicador, ActualizarPantalla volvería a saltar al nivel que se acaba de dejar.
    private string? _expiredRequestId = null;
    private int _remainingSeconds = 0;

    partial void OnCurrentLevelChanged(Models.LevelRequestModel? oldValue, Models.LevelRequestModel? newValue)
    {
        // La misma petición con datos nuevos (por ejemplo, GDDL y AREDL): la cuenta atrás sigue y
        // se conserva el sello que ya tenía en memoria.
        if (oldValue != null && newValue != null && oldValue.RequestId == newValue.RequestId)
        {
            newValue.AutoSkipEndsAtUnix = oldValue.AutoSkipEndsAtUnix;
            return;
        }

        StartOrResetAutoSkipTimer();
    }

    /// <summary>
    /// Inicia o reinicia la cuenta atrás del nivel actual según los ajustes del auto-skip. También
    /// guarda AutoSkipEndsAtUnix, que es de donde el mod de Geode saca su contador.
    /// </summary>
    private void StartOrResetAutoSkipTimer()
    {
        // 1. Se detiene el reloj si estaba en marcha.
        if (_autoSkipTimer != null)
        {
            _autoSkipTimer.Stop();
        }

        _expiredRequestId = null;
        IsTimerVisible = false;
        TimeRemainingText = "";

        // 2. Sin un nivel válido, el sello queda a 0 y no hay cuenta atrás.
        if (CurrentLevel == null || CurrentLevel.Name == "WAITING..." || string.IsNullOrEmpty(CurrentLevel.Name))
        {
            StampAutoSkipDeadline(0);
            return;
        }

        // 3. Con el auto-skip desactivado, el sello queda a 0 (el mod no dibuja contador).
        if (!SettingsVM.Settings.IsAutoSkipEnabled)
        {
            StampAutoSkipDeadline(0);
            return;
        }

        // 4. Segundos totales.
        int multiplier = SettingsVM.Settings.AutoSkipFormat switch
        {
            Models.TimeUnit.Segundos => 1,
            Models.TimeUnit.Minutos => 60,
            Models.TimeUnit.Horas => 3600,
            _ => 60
        };

        _remainingSeconds = SettingsVM.Settings.AutoSkipValue * multiplier;

        // 4.5 Sello absoluto para el mod de Geode. Se escribe una vez por nivel: si el jugador abre
        // GD a mitad de la cuenta atrás, el mod calcula "fin − ahora" y muestra el tiempo correcto.
        StampAutoSkipDeadline(DateTimeOffset.UtcNow.AddSeconds(_remainingSeconds).ToUnixTimeSeconds());

        // Texto inicial del reloj.
        UpdateTimeRemainingText();
        IsTimerVisible = true;

        // 5. Reloj que avanza cada segundo.
        if (_autoSkipTimer == null)
        {
            _autoSkipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoSkipTimer.Tick += AutoSkipTimer_Tick;
        }

        _autoSkipTimer.Start();
    }

    /// <summary>
    /// Guarda el instante absoluto del auto-skip en los dos sitios que lo necesitan: el objeto en
    /// memoria (para el envío a los widgets) y el JSON (para el mod).
    /// </summary>
    private void StampAutoSkipDeadline(long endsAtUnix)
    {
        var level = CurrentLevel;
        if (level == null) return;

        // 1. En memoria: como ActualizarPantalla no sustituye este objeto mientras sea la misma
        // petición, hay que actualizarlo directamente.
        level.AutoSkipEndsAtUnix = endsAtUnix;

        // 2. En disco, sin bloquear el hilo de la interfaz.
        _ = PersistAutoSkipDeadlineAsync(level.RequestId, endsAtUnix);
    }

    /// <summary>
    /// Guarda el sello en queue_data.json. No escribe si el nivel cambió mientras tanto (se
    /// comprueba el RequestId) ni si el valor ya era el correcto.
    /// </summary>
    private async Task PersistAutoSkipDeadlineAsync(string requestId, long endsAtUnix)
    {
        try
        {
            await QueueService.ModificarAsync(state =>
            {
                // Si otro salto se adelantó, este sello ya no vale y no se escribe.
                if (state.CurrentLevel == null || state.CurrentLevel.RequestId != requestId) return false;

                // Si ya estaba bien, se evita la escritura y el reenvío a los widgets que provoca.
                if (state.CurrentLevel.AutoSkipEndsAtUnix == endsAtUnix) return false;

                state.CurrentLevel.AutoSkipEndsAtUnix = endsAtUnix;
                return true;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_AutoSkipStampError"), ex.Message));
        }
    }

    /// <summary>
    /// Se ejecuta cada segundo mientras hay cuenta atrás. Descuenta un segundo y, al llegar a cero,
    /// salta al siguiente nivel si hay niveles en cola.
    /// </summary>
    private async void AutoSkipTimer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;

        // 1. El tiempo llegó a cero.
        if (_remainingSeconds <= 0)
        {
            _autoSkipTimer?.Stop();
            TimeRemainingText = "00:00";

            Console.WriteLine(string.Format(I18n.Tr("Log_AutoSkipExpired"), CurrentLevel?.Name));

            // Con NotifyAutoSkipEvents, un aviso por caso: "pasando al siguiente" si hay cola, y
            // "el tiempo expiró" si no la hay (y "saltando" cuando llegue un nivel nuevo, en
            // ActualizarPantalla).
            bool avisar = SettingsVM.Settings.NotifyAutoSkipEvents;
            string lvlName = CurrentLevel?.Name ?? I18n.Tr("Default_UnknownName");

            // El aviso de nivel caducado y el salto se excluyen. Si se hicieran las dos cosas, la
            // escritura de NextLevel volvería por ActualizarPantalla con el aviso activo y
            // provocaría un segundo salto: con dos o más niveles en cola, se saltaba uno de más.
            if (RequestQueue.Count > 0)
            {
                // Hay niveles en cola: se salta aquí y no se deja ningún aviso pendiente.
                _expiredRequestId = null;
                Console.WriteLine(I18n.Tr("Log_AutoSkipNext"));
                _ = NextLevel();

                if (avisar)
                    await NotificationService.AddNotificationAsync("Info", "Notif_AutoSkipNext", I18n.Tr("Default_System"), lvlName);
            }
            else
            {
                // Cola vacía: no hay a dónde saltar. Se guarda qué nivel caducó para que
                // ActualizarPantalla salte solo si ese mismo nivel sigue en pantalla.
                _expiredRequestId = CurrentLevel?.RequestId;

                if (avisar)
                    await NotificationService.AddNotificationAsync("Info", "Notif_AutoSkipExpired", I18n.Tr("Default_System"), lvlName);
            }
        }
        // 2. Todavía queda tiempo.
        else
        {
            // Actualiza el texto del reloj.
            UpdateTimeRemainingText();

            // Envía el estado a los widgets para que actualicen su reloj.
            if (SettingsVM.Settings.IsWidgetsServerEnabled)
            {
                WidgetServerService.Instance?.BroadcastQueueStateAsync(CurrentLevel, RequestQueue.ToList());
            }
        }
    }

    /// <summary>
    /// Formatea los segundos restantes en un formato legible (mm:ss o hh:mm:ss).
    /// </summary>
    private void UpdateTimeRemainingText()
    {
        TimeSpan time = TimeSpan.FromSeconds(_remainingSeconds);
        if (time.TotalHours >= 1)
            TimeRemainingText = time.ToString(@"hh\:mm\:ss");
        else
            TimeRemainingText = time.ToString(@"mm\:ss");
    }

    private static readonly HttpClient ClienteMiniaturas = new() { Timeout = TimeSpan.FromSeconds(15) };

    // URL del último fondo pedido ("" para el fondo por defecto). Evita descargar otra vez la
    // misma miniatura y permite descartar una descarga que termina cuando ya se pidió otra.
    private string? _urlFondo;

    /// <summary>
    /// Descarga la miniatura del nivel actual para el fondo de la ventana principal. Si la URL
    /// está vacía o la descarga falla, carga el fondo por defecto. No hace nada si la URL es la
    /// misma que la del último fondo pedido, y si mientras se descargaba se pidió otro, el
    /// resultado se descarta.
    /// </summary>
    public async Task CargarFondoAsync(string url)
    {
        url ??= "";
        if (url == _urlFondo) return;
        _urlFondo = url;

        Bitmap? nuevo = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var bytes = await ClienteMiniaturas.GetByteArrayAsync(url);
                using var stream = new System.IO.MemoryStream(bytes);
                nuevo = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_ThumbnailUnavailable"), ex.Message));
            }
        }

        nuevo ??= CargarFondoPorDefecto();

        // Mientras se descargaba se pidió otro fondo: este ya no corresponde al nivel actual.
        if (url != _urlFondo) return;

        BackgroundBitmap = nuevo;
    }

    /// <summary>
    /// Carga placeholder_bg.png de los recursos. Primero con la carpeta Assets y, si falla, con la
    /// carpeta en minúsculas (se añadió para Linux). Devuelve null si no está.
    /// </summary>
    private static Bitmap? CargarFondoPorDefecto()
    {
        try
        {
            return new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://SheepGDRequest/Assets/placeholder_bg.png")));
        }
        catch
        {
            try
            {
                return new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://SheepGDRequest/assets/placeholder_bg.png")));
            }
            catch (Exception ex)
            {
                // placeholder_bg.png no está incluido como AvaloniaResource en el proyecto.
                Console.WriteLine(string.Format(I18n.Tr("Log_PlaceholderMissing"), ex.Message));
                return null;
            }
        }
    }

    // ==========================================
    // COMANDOS DE ACCIÓN (BOTONES DE LA INTERFAZ)
    // ==========================================

    /// <summary>
    /// Copia el ID del nivel al portapapeles.
    /// </summary>
    [RelayCommand]
    private async Task CopyIdAsync(LevelRequestModel? level)
    {
        if (level == null || string.IsNullOrEmpty(level.LevelId)) return;
        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(level.LevelId);
    }

    [RelayCommand]
    private async Task ToggleRequestsState()
    {
        var config = SettingsVM.Settings;
        if (config.IsRequestsEnabled)
        {
            await NotificationService.AddNotificationAsync("Success", "Notif_RequestsEnabled", I18n.Tr("Default_System"), "");
        }
        else
        {
            await NotificationService.AddNotificationAsync("Warning", "Notif_RequestsDisabled", I18n.Tr("Default_System"), "");
        }
    }

    /// <summary>
    /// Copia el ID de la canción (Newgrounds o NONG) al portapapeles.
    /// </summary>
    [RelayCommand]
    private async Task CopySongIdAsync(LevelRequestModel? level)
    {
        if (level == null || string.IsNullOrEmpty(level.CustomSongId) || level.CustomSongId == "0") return;
        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(level.CustomSongId);
    }

    /// <summary>
    /// Busca una petición en la cola por su RequestId, el GUID de la petición.
    ///
    /// No se busca por LevelId porque es el ID del nivel en Geometry Dash: con AllowRepeatedLevels
    /// activo, dos peticiones del mismo nivel lo comparten, y se actuaría siempre sobre la primera.
    ///
    /// Si la petición no tiene RequestId (un JSON antiguo o escrito por otro programa), se busca
    /// por LevelId: es menos preciso, pero el botón sigue funcionando.
    /// </summary>
    private static LevelRequestModel? BuscarPeticionEnCola(List<LevelRequestModel> cola, LevelRequestModel objetivo)
    {
        if (!string.IsNullOrEmpty(objetivo.RequestId))
        {
            var porRequestId = cola.FirstOrDefault(l => l.RequestId == objetivo.RequestId);
            if (porRequestId != null) return porRequestId;
        }

        return cola.FirstOrDefault(l => l.LevelId == objetivo.LevelId);
    }

    /// <summary>
    /// Quita una petición de la cola de espera.
    /// </summary>
    [RelayCommand]
    public async Task RemoveLevel(Models.LevelRequestModel levelToRemove)
    {
        if (levelToRemove == null) return;

        await QueueService.ModificarAsync(state =>
        {
            var nivelEncontrado = BuscarPeticionEnCola(state.Queue, levelToRemove);
            return nivelEncontrado != null && state.Queue.Remove(nivelEncontrado);
        });
    }

    /// <summary>
    /// Mueve una petición al principio de la cola, para que sea la siguiente.
    /// </summary>
    [RelayCommand]
    private async Task PrioritizeLevel(LevelRequestModel? level)
    {
        if (level == null) return;

        LevelRequestModel? item = null;
        await QueueService.ModificarAsync(state =>
        {
            item = BuscarPeticionEnCola(state.Queue, level);
            if (item == null) return false;

            state.Queue.Remove(item);
            state.Queue.Insert(0, item);
            return true;
        });

        if (item != null)
        {
            // Notificación, si está activada en Ajustes.
            if (SettingsVM.Settings.NotifySystemEvents)
            {
                await NotificationService.AddNotificationAsync("Info", "Notif_LevelPrioritized", item.RequesterName, item.Name);
            }
        }
    }

    /// <summary>
    /// Pasa el nivel actual al historial y pone como actual el primero de la cola, o el
    /// marcador de espera si la cola está vacía. En ese caso, si se acaba de terminar el último
    /// nivel, avisa de que la cola está vacía (con NotifySystemEvents).
    /// </summary>
    [RelayCommand(CanExecute = nameof(PuedeCambiarDeNivel))]
    private async Task NextLevel()
    {
        bool seVacio = false;

        var guardado = await QueueService.ModificarAsync(state =>
        {
            seVacio = state.Queue.Count == 0
                && state.CurrentLevel != null && state.CurrentLevel.Name != "WAITING...";

            // 1. El nivel actual pasa al historial, salvo que sea el marcador de espera. El
            // historial se recorta al guardar (QueueFileService.MaxHistorial).
            if (state.CurrentLevel != null && state.CurrentLevel.Name != "WAITING...")
            {
                state.History.Add(state.CurrentLevel);
            }

            // 2. Si hay niveles en cola, pasa el siguiente.
            if (state.Queue.Count > 0)
            {
                state.CurrentLevel = state.Queue[0];
                state.Queue.RemoveAt(0);
            }
            // 3. Si no, se pone el marcador de espera ("WAITING...").
            else
            {
                state.CurrentLevel = QueueService.CreateDefaultState().CurrentLevel;
            }

            ConsumirTurnoDeCambio();
            return true;
        });

        if (guardado != null && seVacio && SettingsVM.Settings.NotifySystemEvents)
            await NotificationService.AddNotificationAsync("Info", "Notif_QueueEmpty", I18n.Tr("Default_System"), "");
    }

    /// <summary>
    /// Deshace NextLevel: devuelve el nivel actual al principio de la cola y recupera el último
    /// nivel del historial.
    /// </summary>
    [RelayCommand(CanExecute = nameof(PuedeCambiarDeNivel))]
    private async Task PreviousLevel()
    {
        await QueueService.ModificarAsync(state =>
        {
            if (state.History.Count == 0) return false;

            if (state.CurrentLevel != null && state.CurrentLevel.Name != "WAITING...")
                state.Queue.Insert(0, state.CurrentLevel);

            var prev = state.History.Last();
            state.History.RemoveAt(state.History.Count - 1);
            state.CurrentLevel = prev;

            ConsumirTurnoDeCambio();
            return true;
        });
    }

    /// <summary>
    /// Elige al azar un nivel de la cola y lo pone como actual; el resto de la cola no cambia.
    /// </summary>
    [RelayCommand(CanExecute = nameof(PuedeCambiarDeNivel))]
    private async Task RandomLevel()
    {
        // Al escribir, QueueFileService avisa con OnQueueUpdated y ActualizarPantalla refresca la
        // interfaz.
        await QueueService.ModificarAsync(state =>
        {
            // 1. Sin niveles en cola no hay a dónde saltar.
            if (state.Queue.Count == 0) return false;

            // 2. El nivel actual pasa al historial, salvo que sea el marcador de espera.
            if (state.CurrentLevel != null && state.CurrentLevel.Name != "WAITING...")
            {
                state.History.Add(state.CurrentLevel);
            }

            // 3. Posición al azar dentro de la cola.
            int index = Random.Shared.Next(state.Queue.Count);

            // 4. Ese nivel pasa a ser el actual.
            state.CurrentLevel = state.Queue[index];

            // 5. Y se quita de la cola.
            state.Queue.RemoveAt(index);

            ConsumirTurnoDeCambio();
            return true;
        });
    }

    /// <summary>
    /// Quita el nivel actual sin pasarlo al historial y pone el siguiente de la cola.
    ///
    /// Nota: no usa el enfriamiento de NextLevel, PreviousLevel y RandomLevel, a propósito: el mod
    /// no tiene un equivalente a este botón.
    /// </summary>
    [RelayCommand]
    private async Task RemoveCurrentLevel()
    {
        await QueueService.ModificarAsync(state =>
        {
            if (state.Queue.Count > 0)
            {
                state.CurrentLevel = state.Queue[0];
                state.Queue.RemoveAt(0);
            }
            else
            {
                // Solo se sustituye CurrentLevel; el historial y la cola no cambian.
                state.CurrentLevel = QueueService.CreateDefaultState().CurrentLevel;
            }
            return true;
        });
    }
}

/// <summary>
/// Redirige Console.WriteLine a la consola de depuración de la ventana.
/// </summary>
public class ConsoleRedirector : TextWriter
{
    private readonly Action<string> _logAction;
    public ConsoleRedirector(Action<string> logAction) => _logAction = logAction;
    public override Encoding Encoding => Encoding.UTF8;
    public override void WriteLine(string? value)
    {
        if (value != null) _logAction(value);
    }
}
