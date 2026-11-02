using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TikTokLiveSharp.Client;
using SheepGDRequest.ViewModels;
using SheepGDRequest.Models;

namespace SheepGDRequest.Services;

/// <summary>
/// Lee el chat de los directos de TikTok con la librería TikTokLive_Sharp, que se comunica
/// directamente con la API de TikTok Live, sin Playwright ni DOM.
///
/// Incluye un bucle de reconexión: si el directo se corta o TikTok cierra el socket, vuelve a
/// conectar con una espera creciente. Antes, si la conexión se perdía, no se reintentaba ni se
/// avisaba, y el chat dejaba de llegar sin ninguna indicación.
///
/// Las conexiones se indexan por nombre de usuario (<see cref="ExtraerUsuario"/>), no por URL.
/// SheepChatListener.CalcularClave usa esta misma función para que detener y reiniciar
/// encuentren la conexión.
/// </summary>
public class TiktokListener
{
    /// <summary>
    /// Clientes activos por usuario, para poder detenerlos desde fuera. El propio bucle de
    /// supervisión añade y quita la entrada en cada vuelta. La quita solo si sigue siendo su
    /// cliente: tras un reinicio, el bucle anterior no debe borrar la entrada del nuevo.
    /// </summary>
    public static readonly ConcurrentDictionary<string, TikTokLiveClient> ActiveClients = new();

    /// <summary>
    /// Un CancellationTokenSource por usuario, para detener el bucle de supervisión anterior.
    ///
    /// Es imprescindible: RestartWidgetAsync llama a StartListeningAsync en cada "Guardar cambios".
    /// Sin esto, cada guardado dejaría otro bucle activo reconectando por su cuenta, y habría
    /// varios clientes duplicando cada mensaje del chat.
    /// </summary>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> ActiveLoops = new();

    // Registrar el cancelador nuevo y cancelar el anterior tienen que ser un solo paso.
    private static readonly object LoopsLock = new();

    private const int BackoffInicialMs = 5_000;    // 5 s
    private const int BackoffMaximoMs = 300_000;   // 5 min
    // Una sesión cuenta como conexión establecida cuando lleva este tiempo abierta (ver el bucle).
    private const int SegundosParaConsiderarConectado = 60;

    /// <summary>
    /// Deja de vigilar a un usuario: detiene el bucle de reconexión y cierra el cliente.
    /// Devuelve true si había algo activo que cerrar.
    /// </summary>
    public static async Task<bool> StopByUsernameAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;

        bool habiaConexion = false;

        // Primero se cancela el bucle, para que no vuelva a crear un cliente nuevo. El
        // cancelador no se libera: el bucle puede estar usando todavía su token.
        CancellationTokenSource? cts;
        lock (LoopsLock)
        {
            ActiveLoops.TryRemove(username, out cts);
        }
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            habiaConexion = true;
        }

        if (ActiveClients.TryRemove(username, out var client))
        {
            try { await client.Stop(); } catch { }
            habiaConexion = true;
        }

        return habiaConexion;
    }

    /// <summary>
    /// Igual que StopByUsernameAsync, pero obteniendo el usuario a partir de una URL.
    /// </summary>
    public static Task<bool> StopListeningAsync(string url) => StopByUsernameAsync(ExtraerUsuario(url));

    /// <summary>
    /// Arranca (o reinicia) la vigilancia de un directo de TikTok.
    /// </summary>
    /// <param name="url">Enlace completo o nombre de usuario, con o sin @.</param>
    public static async Task StartListeningAsync(string url)
    {
        string username = ExtraerUsuario(url);
        if (string.IsNullOrWhiteSpace(username)) return;

        // Detiene el bucle y el cliente anteriores de este usuario, si los hay. Desde
        // RestartWidgetAsync ya llegan detenidos; esto cubre cualquier otra llamada.
        await StopListeningAsync(url);

        // Dos llamadas a la vez para el mismo usuario pueden pasar las dos por el "detener" sin
        // encontrar nada. Al registrar, la segunda cancela el bucle de la primera, así que nunca
        // quedan dos bucles vivos duplicando cada mensaje.
        var cts = new CancellationTokenSource();
        CancellationTokenSource? anterior;
        lock (LoopsLock)
        {
            ActiveLoops.TryRemove(username, out anterior);
            ActiveLoops[username] = cts;
        }
        try { anterior?.Cancel(); } catch { }

        _ = Task.Run(() => SupervisarConexionAsync(username, cts.Token));
    }

    /// <summary>
    /// Extrae el nombre de usuario de TikTok de una URL.
    /// Acepta "@usuario", "usuario" o el enlace del perfil o del directo. Si hay @, el nombre es lo
    /// que va entre la @ y la siguiente "/" o "?". Sin @, solo se quitan el esquema,
    /// "www.tiktok.com/" y "/live".
    /// </summary>
    public static string ExtraerUsuario(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        string username = url.Trim();

        if (username.Contains("@"))
        {
            var partes = username.Split('@');
            if (partes.Length < 2) return "";
            username = partes[1].Split('/')[0].Split('?')[0];
        }
        else
        {
            username = username.Replace("https://", "").Replace("http://", "")
                               .Replace("www.tiktok.com/", "").Replace("/live", "").Trim();
        }

        return username.Trim();
    }

    /// <summary>
    /// Estado del bucle de reconexión de un usuario. Lo modifican el bucle y el temporizador de
    /// <see cref="ValidarSesionAsync"/>, nunca a la vez: el bucle espera a que el temporizador
    /// termine antes de volver a tocarlo.
    /// </summary>
    private sealed class EstadoReconexion
    {
        public int BackoffMs = BackoffInicialMs;

        // Arrancar con el directo cerrado no debe generar un aviso de "conexión perdida" en cada
        // reintento: solo se avisa si llegó a haber una sesión válida.
        public bool HuboConexion;

        // Una caída se avisa una sola vez, aunque los reintentos fallen durante horas.
        public bool CaidaAvisada;
    }

    /// <summary>
    /// Bucle de vida de la conexión. Cada vuelta crea un cliente nuevo (no se da por hecho que uno
    /// detenido pueda reutilizarse), espera a que RunAsync termine, lo que solo ocurre al
    /// desconectarse, y reintenta con una espera creciente.
    /// </summary>
    private static async Task SupervisarConexionAsync(string username, CancellationToken token)
    {
        var estado = new EstadoReconexion();

        while (!token.IsCancellationRequested)
        {
            // ClientSettings es un struct: si se construye con un inicializador de objeto,
            // cualquier campo que no se mencione queda con su valor cero, no con el valor por
            // defecto de la librería. Eso dejaba SocketBufferSize, Timeout y PollingInterval en 0 y
            // ClientLanguage en null. Con un búfer de socket de 0 bytes, la conexión se establece
            // pero no llega ningún mensaje.
            //
            // Por eso se parte de DEFAULT_SETTINGS (una copia, al ser un struct) y solo se cambia
            // lo necesario.
            var settings = TikTokLiveSharp.Client.Config.Constants.DEFAULT_SETTINGS;
            settings.LogLevel = TikTokLiveSharp.Client.Config.LogLevel.Error;
            settings.PrintToConsole = false;  // Que la librería no escriba en la consola

            var client = new TikTokLiveClient(username, null, settings);

            client.OnChatMessage += (sender, e) =>
            {
                string requester = e.Sender?.UniqueId ?? e.Sender?.NickName ?? "";
                if (string.IsNullOrWhiteSpace(requester)) requester = I18n.Tr("Default_Viewer");

                // Parser común: comando seguido de un espacio y todo lo que va detrás es el nivel
                // (los nombres con espacios llegan completos); si no, detección automática de IDs.
                var tipo = GenericListener.TryParseCommand(e.Message ?? "", out string query);
                if (tipo == GenericListener.TipoMensaje.Nada) return;

                Console.WriteLine(string.Format(
                    I18n.Tr(tipo == GenericListener.TipoMensaje.Comando ? "Log_TikTokCmd" : "Log_TikTokAutoId"),
                    requester, query));

                // La búsqueda en GD es lenta: se ejecuta aparte para no retener el evento de la
                // librería. "tiktok" fijo como plataforma de origen: determina el icono.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await GenericListener.ProcessLevelRequestAsync(query, requester, ChatPlatform.TikTok, "tiktok");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(string.Format(I18n.Tr("Log_TikTokTaskError"), ex.Message));
                    }
                });
            };

            // Sin este mensaje no se distingue "conectado y escuchando" de "bloqueado": una vez que
            // RunAsync conecta, la consola no muestra nada más.
            client.OnConnected += (sender, e) =>
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_TikTokConnected"), username));
            };

            ActiveClients[username] = client;

            // La sesión se da por válida cuando lleva un minuto abierta, mientras sigue abierta:
            // así el aviso de "restablecida" sale al volver, no cuando la sesión vuelve a caerse.
            using var finSesion = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task validacion = ValidarSesionAsync(username, estado, finSesion.Token);
            bool cancelado = false;

            try
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_TikTokConnecting"), username));

                // RunAsync no termina mientras la conexión está activa: dentro están el bucle de
                // recepción y el de ping. Que este await termine indica que la conexión se perdió.
                await client.RunAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Cancelado desde StopByUsernameAsync, que ya detuvo el cliente si seguía
                // registrado.
                cancelado = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_TikTokConnError"), ex.Message));
            }

            finSesion.Cancel();
            await validacion;

            if (!cancelado)
            {
                try { await client.Stop(); } catch { }
            }
            ActiveClients.TryRemove(new KeyValuePair<string, TikTokLiveClient>(username, client));

            if (cancelado || token.IsCancellationRequested) break;

            if (estado.HuboConexion && !estado.CaidaAvisada)
            {
                estado.CaidaAvisada = true;
                await SheepChatListener.NotificarConexionAsync("Warning", "Notif_TikTokDisconnected", username);
            }

            Console.WriteLine(string.Format(I18n.Tr("Log_TikTokDisconnected"), username, estado.BackoffMs / 1000));

            try { await Task.Delay(estado.BackoffMs, token); }
            catch (OperationCanceledException) { break; }

            estado.BackoffMs = Math.Min(estado.BackoffMs * 2, BackoffMaximoMs);
        }

        Console.WriteLine(string.Format(I18n.Tr("Log_TikTokLoopStopped"), username));
    }

    /// <summary>
    /// Temporizador de una sesión: si sigue abierta pasados
    /// <see cref="SegundosParaConsiderarConectado"/> segundos, cuenta como conexión establecida.
    /// Reinicia la espera entre intentos y, si se había avisado de una caída, avisa de que la
    /// conexión volvió. Termina sin hacer nada si la sesión acaba antes.
    /// </summary>
    private static async Task ValidarSesionAsync(string username, EstadoReconexion estado, CancellationToken finSesion)
    {
        try { await Task.Delay(SegundosParaConsiderarConectado * 1000, finSesion); }
        catch (OperationCanceledException) { return; }

        estado.BackoffMs = BackoffInicialMs;
        estado.HuboConexion = true;

        if (estado.CaidaAvisada)
        {
            estado.CaidaAvisada = false;
            Console.WriteLine(string.Format(I18n.Tr("Log_TikTokReconnected"), username));
            await SheepChatListener.NotificarConexionAsync("Success", "Notif_TikTokReconnected", username);
        }
    }
}
