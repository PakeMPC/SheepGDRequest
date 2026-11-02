using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SheepGDRequest.Models;
using Avalonia.Threading;

namespace SheepGDRequest.Services;

/// <summary>
/// Gestiona la lectura, la escritura y la vigilancia del archivo JSON que almacena el estado
/// completo de la cola de niveles (QueueState).
///
/// queue_data.json es el punto de intercambio con el mod de Geode. Escriben en él los botones de
/// la ventana (MainWindowViewModel), cada petición aceptada (<see cref="AddLevelAsync"/>), el
/// borrado de mensajes de Discord y el propio mod, que además lo vuelve a leer cuando cambia su
/// fecha de modificación.
///
/// Todo cambio pasa por <see cref="ModificarAsync"/>, que lee, aplica y escribe dentro de un
/// cerrojo: dos cambios a la vez dentro de la aplicación no se pisan. La escritura es atómica
/// (archivo temporal y renombrado, como hace el mod), así que nadie lee el archivo a medias.
/// </summary>
public class QueueFileService
{
    private readonly string _queueFilePath;

    // Nombre propio, distinto del "queue_data.json.tmp" que usa el mod: si los dos escribieran a la
    // vez en el mismo temporal, se mezclarían.
    private readonly string _tempFilePath;

    private readonly JsonSerializerOptions _jsonOptions;
    private FileSystemWatcher? _watcher;

    /// <summary>Niveles del historial que se conservan; los más antiguos se descartan.</summary>
    public const int MaxHistorial = 50;

    /// <summary>Milisegundos sin eventos que se esperan antes de releer el archivo.</summary>
    private const int DebounceMs = 150;

    /// <summary>Cancela la relectura pendiente cada vez que llega un evento nuevo.</summary>
    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();

    /// <summary>
    /// Cerrojo de lectura, cambio y escritura: solo un <see cref="ModificarAsync"/> a la vez.
    /// </summary>
    private readonly SemaphoreSlim _cerrojo = new(1, 1);

    /// <summary>
    /// Fecha de modificación que dejó la última escritura de esta aplicación. Permite ignorar los
    /// eventos causados por las escrituras propias sin desactivar el FileSystemWatcher.
    /// </summary>
    private DateTime _lastSelfWriteUtc = DateTime.MinValue;

    /// <summary>
    /// Evento que se dispara en el hilo de la UI cuando cambia el estado de la cola. Se produce
    /// tanto con los cambios del mod (a través del FileSystemWatcher) como con las escrituras
    /// propias, que notifica directamente <see cref="ModificarAsync"/>.
    /// </summary>
    public event Action<QueueState>? OnQueueUpdated;

    public QueueFileService()
    {
        _queueFilePath = AppPaths.QueueFile;
        _tempFilePath = _queueFilePath + ".sheepgdrequest.tmp";
        _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        SetupWatcher();
    }

    /// <summary>
    /// Configura el FileSystemWatcher que detecta los cambios de otros programas (el mod de Geode).
    /// Escucha Changed, Created y Renamed porque un reemplazo atómico (escribir un .tmp y
    /// renombrarlo encima del original) no llega como Changed, sino como Created o Renamed.
    /// </summary>
    private void SetupWatcher()
    {
        // Filtro con comodín para ver también el ciclo de vida de los archivos temporales. Los
        // eventos repetidos se agrupan en el antirrebote.
        _watcher = new FileSystemWatcher(AppPaths.DataDir, "queue_data.json*")
        {
            NotifyFilter = NotifyFilters.LastWrite
                         | NotifyFilters.FileName
                         | NotifyFilters.Size
                         | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
    }

    /// <summary>
    /// Antirrebote: cada evento cancela la relectura pendiente y programa una nueva. El archivo se
    /// lee una sola vez, cuando pasan DebounceMs sin eventos (en un reemplazo atómico, el evento
    /// que importa es el último).
    /// </summary>
    private void ScheduleReload()
    {
        CancellationToken token;

        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, token);
            }
            catch (OperationCanceledException)
            {
                return; // Llegó otro evento: esta relectura se descarta
            }

            // Si el cambio es una escritura propia, no se relee: ModificarAsync ya notificó a la
            // interfaz.
            try
            {
                if (File.Exists(_queueFilePath) &&
                    File.GetLastWriteTimeUtc(_queueFilePath) == _lastSelfWriteUtc)
                {
                    return;
                }
            }
            catch { /* Si no se puede comprobar, es mejor releer de más que de menos */ }

            var state = await ReadQueueStateSafeAsync();
            Dispatcher.UIThread.Post(() => OnQueueUpdated?.Invoke(state));
        });
    }

    /// <summary>
    /// Lee el estado de la cola para mostrarlo o consultarlo. Si el archivo no existe o no se
    /// puede leer, devuelve el estado en reposo. Para cambiar la cola hay que usar
    /// <see cref="ModificarAsync"/>, que distingue esos dos casos y no escribe encima de un
    /// archivo que no ha podido leer.
    /// </summary>
    /// <returns>El estado actual de la cola, o el estado en reposo.</returns>
    public async Task<QueueState> ReadQueueStateSafeAsync()
    {
        var (state, _) = await LeerAsync();
        return state ?? CreateDefaultState();
    }

    /// <summary>
    /// Lee queue_data.json con reintentos (el archivo puede estar en uso). Devuelve el estado y si
    /// la lectura fue correcta. Un archivo que no existe o está vacío es una cola en reposo, no un
    /// error; un JSON que sigue sin poder leerse tras los reintentos sí lo es.
    /// </summary>
    private async Task<(QueueState? estado, bool leido)> LeerAsync()
    {
        if (!File.Exists(_queueFilePath)) return (CreateDefaultState(), true);

        for (int i = 0; i < 5; i++)
        {
            try
            {
                using var stream = new FileStream(_queueFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string json = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(json)) return (CreateDefaultState(), true);

                return (JsonSerializer.Deserialize<QueueState>(json, _jsonOptions) ?? CreateDefaultState(), true);
            }
            catch (FileNotFoundException)
            {
                // Desapareció entre la comprobación y la apertura (un renombrado en curso).
                await Task.Delay(100);
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                // Archivo en uso o JSON a medio escribir: se espera y se reintenta.
                await Task.Delay(100);
            }
        }
        return (null, false);
    }

    /// <summary>
    /// Lee la cola, le aplica un cambio y la guarda, todo dentro del cerrojo. Es la única forma de
    /// cambiar queue_data.json desde la aplicación.
    ///
    /// <paramref name="cambio"/> recibe el estado leído y devuelve true si lo ha cambiado; si
    /// devuelve false, no se escribe nada. Si el archivo existe pero no se puede leer (JSON
    /// dañado), se guarda una copia con la fecha antes de seguir con la cola en reposo, para que
    /// la cola anterior no se pierda sin rastro.
    ///
    /// Si el mod escribe el archivo mientras tanto (otro proceso, fuera del cerrojo), se detecta
    /// por la fecha de modificación y el cambio se vuelve a aplicar sobre la versión nueva.
    /// </summary>
    /// <returns>El estado guardado, o null si no se escribió.</returns>
    public async Task<QueueState?> ModificarAsync(Func<QueueState, bool> cambio)
    {
        await _cerrojo.WaitAsync();
        try
        {
            for (int intento = 0; intento < 3; intento++)
            {
                DateTime fechaLeida = FechaDelArchivo();
                var (leido, ok) = await LeerAsync();

                if (!ok)
                {
                    GuardarCopiaDelArchivoIlegible();
                    leido = CreateDefaultState();
                }

                var estado = leido!;
                if (!cambio(estado)) return null;

                // Si el mod escribió entre la lectura y ahora, se repite sobre su versión.
                if (ok && FechaDelArchivo() != fechaLeida && intento < 2) continue;

                if (!await EscribirAsync(estado)) return null;

                Dispatcher.UIThread.Post(() => OnQueueUpdated?.Invoke(estado));
                return estado;
            }
            return null;
        }
        finally
        {
            _cerrojo.Release();
        }
    }

    private DateTime FechaDelArchivo()
    {
        try { return File.Exists(_queueFilePath) ? File.GetLastWriteTimeUtc(_queueFilePath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// Copia queue_data.json, que no se ha podido leer, a "queue_data.json.ilegible-fecha" en la
    /// misma carpeta, y lo dice en la consola.
    /// </summary>
    private void GuardarCopiaDelArchivoIlegible()
    {
        try
        {
            string copia = $"{_queueFilePath}.ilegible-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(_queueFilePath, copia, overwrite: true);
            Console.WriteLine(string.Format(I18n.Tr("Log_QueueUnreadable"), copia));
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_QueueWriteError"), ex.Message));
        }
    }

    /// <summary>
    /// Escribe el estado en un archivo temporal y lo renombra encima de queue_data.json, igual que
    /// el mod: quien lea el archivo ve la versión anterior o la nueva, nunca una a medias. Recorta
    /// el historial a <see cref="MaxHistorial"/>. Guarda la fecha de modificación resultante para
    /// que ScheduleReload distinga esta escritura de una del mod.
    /// </summary>
    /// <returns>
    /// true si se escribió; false si fallaron los cinco intentos (se dice en la consola).
    /// </returns>
    private async Task<bool> EscribirAsync(QueueState state)
    {
        if (state.History.Count > MaxHistorial)
            state.History.RemoveRange(0, state.History.Count - MaxHistorial);

        string json = JsonSerializer.Serialize(state, _jsonOptions);
        Exception? ultimoError = null;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                await File.WriteAllTextAsync(_tempFilePath, json);
                File.Move(_tempFilePath, _queueFilePath, overwrite: true);

                _lastSelfWriteUtc = File.GetLastWriteTimeUtc(_queueFilePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // En Windows el renombrado falla si otro proceso tiene el archivo abierto sin
                // permitir borrarlo; se espera y se reintenta.
                ultimoError = ex;
                await Task.Delay(100);
            }
        }

        Console.WriteLine(string.Format(I18n.Tr("Log_QueueWriteError"), ultimoError?.Message));
        return false;
    }

    /// <summary>
    /// Crea queue_data.json con el estado en reposo si todavía no existe. El mod de Geode necesita
    /// que exista desde el primer arranque para poder enlazarlo (o encontrarlo automáticamente)
    /// sin esperar a que llegue un nivel.
    /// </summary>
    public async Task EnsureFileExistsAsync()
    {
        if (File.Exists(_queueFilePath)) return;

        await ModificarAsync(_ => true);
        Console.WriteLine(string.Format(I18n.Tr("Log_QueueFileCreated"), _queueFilePath));
    }

    /// <summary>
    /// Añade un nivel a la cola y lo guarda en disco. Si la cola está en reposo ("WAITING..."), el
    /// nivel pasa directamente a ser el nivel actual.
    /// </summary>
    /// <param name="newLevel">El nivel que se añade.</param>
    /// <returns>true si el nivel quedó guardado.</returns>
    public async Task<bool> AddLevelAsync(LevelRequestModel newLevel)
    {
        var guardado = await ModificarAsync(state =>
        {
            // En reposo, el nivel pasa directamente a ser el actual.
            if (state.CurrentLevel != null && state.CurrentLevel.Name == "WAITING...")
                state.CurrentLevel = newLevel;
            else
                state.Queue.Add(newLevel);
            return true;
        });
        return guardado != null;
    }

    /// <summary>
    /// Genera el estado en reposo: sin cola y con el marcador de espera como nivel actual.
    /// </summary>
    public QueueState CreateDefaultState()
    {
        return new QueueState
        {
            CurrentLevel = new LevelRequestModel
            {
                // "WAITING..." es un valor centinela que indica que no hay nivel actual. No debe
                // traducirse: lo comprueban MainWindowViewModel y el mod de Geode.
                Name = "WAITING...",

                Author = "-",
                DifficultyName = I18n.Tr("Default_Unrated"),
                DifficultyFacePath = "avares://SheepGDRequest/Assets/GDassets/faces/unrated.png",

                // Este texto es solo visual y sí se traduce.
                RequesterName = I18n.Tr("Default_WaitingRequester"),

                Downloads = "0", Likes = "0", Length = "-", Orbs = 0, Coins = 0, Stars = 0,
                GddlTier = "", AredlRank = ""
            }
        };
    }
}
