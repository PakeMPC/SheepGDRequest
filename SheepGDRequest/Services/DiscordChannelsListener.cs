using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using SheepGDRequest.Models;
using SheepGDRequest.ViewModels;

namespace SheepGDRequest.Services;

/// <summary>
/// Escucha un canal de Discord con un bot (Discord.Net, a través del Gateway). SheepChatListener
/// crea una instancia por cada enlace de Discord y la guarda en ActiveDiscordListeners; todas
/// usan el mismo token, el configurado en Ajustes.
///
/// Dos modos:
/// <list type="bullet">
///   <item>Directo: cada mensaje del canal con un comando es una petición.</item>
///   <item>Moderado (<c>DiscordOnlyCheckedLevels</c>): el mensaje solo se anuncia, y el nivel
///   entra cuando alguien le pone una reacción de visto bueno.</item>
/// </list>
/// En los dos modos, si se borra el mensaje en Discord, su nivel sale de la cola.
///
/// Discord.Net atiende los eventos del Gateway de uno en uno y espera a que cada manejador
/// termine, así que los manejadores lanzan la petición (que incluye la búsqueda en GD, a veces
/// lenta) con Task.Run y vuelven enseguida.
/// </summary>
public class DiscordChannelsListener
{
    // Es null antes de StartAsync y después de StopAsync.
    private DiscordSocketClient? _client;

    /// <summary>
    /// IDs de los mensajes aprobados cuya petición todavía se está procesando (por ejemplo,
    /// buscando en GD). Mientras están aquí no aparecen en la cola, y sin este registro una segunda
    /// aprobación del mismo mensaje lo metería dos veces.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> MensajesEnProceso = new();
    private ulong _targetChannelId;

    /// <summary>
    /// Inicia la conexión con Discord usando un token de bot y el enlace del canal.
    /// No lanza excepciones: los errores se escriben en la consola. Vuelve en cuanto Discord acepta
    /// el inicio de sesión; la conexión continúa en segundo plano y el evento Ready indica cuándo
    /// está lista.
    /// </summary>
    public async Task StartAsync(string botToken, string channelLink)
    {
        try
        {
            _targetChannelId = ExtractChannelId(channelLink);
            if (_targetChannelId == 0)
            {
                Console.WriteLine(I18n.Tr("Log_DiscordInvalidLink"));
                return;
            }

            var config = new DiscordSocketConfig
            {
                // GuildMessageReactions es imprescindible para el modo moderado: sin él, el evento
                // ReactionAdded no llega. Se solicita siempre, esté o no activa la opción, porque
                // no es un intent privilegiado (no requiere cambios en el portal de
                // desarrolladores) y así activar la opción no obliga a reconectar el bot.
                // MessageContent, en cambio, sí es privilegiado y debe activarse en el portal del
                // bot; si no lo está, Discord rechaza la conexión.
                GatewayIntents = GatewayIntents.Guilds
                                 | GatewayIntents.GuildMessages
                                 | GatewayIntents.MessageContent
                                 | GatewayIntents.GuildMessageReactions
            };

            _client = new DiscordSocketClient(config);
            _client.Log += LogAsync;
            _client.MessageReceived += MessageReceivedAsync;
            _client.MessageDeleted += MessageDeletedAsync;
            _client.ReactionAdded += ReactionAddedAsync;

            // El bot solo se da por conectado cuando Discord confirma la conexión. Ready puede
            // volver a dispararse si la librería abre una sesión nueva, así que el mensaje puede
            // repetirse.
            _client.Ready += () =>
            {
                Console.WriteLine(I18n.Tr("Log_DiscordConnected"));
                return Task.CompletedTask;
            };

            // Se eliminan los espacios sobrantes, habituales al pegar el token.
            string cleanToken = botToken.Trim();

            await _client.LoginAsync(TokenType.Bot, cleanToken);
            await _client.StartAsync();

            // StartAsync de Discord.Net vuelve de inmediato; hasta aquí solo se ha iniciado sesión.
            Console.WriteLine(I18n.Tr("Log_DiscordConnecting"));
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_DiscordCriticalError"), ex.Message));
        }
    }

    /// <summary>
    /// Escribe en la consola los avisos y errores de la librería (Warning o más graves) e ignora el
    /// resto.
    /// </summary>
    private Task LogAsync(LogMessage log)
    {
        // Se muestra el mensaje de la excepción si la hay y, si no, el del registro. Es solo el
        // mensaje, no la traza completa.
        if (log.Severity <= LogSeverity.Warning)
        {
            string errorMessage = log.Exception != null ? log.Exception.Message : log.Message;
            Console.WriteLine(string.Format(I18n.Tr("Log_DiscordLibrary"), errorMessage));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cierra la sesión, detiene y libera el cliente. No lanza excepciones y puede llamarse más de
    /// una vez: si _client ya es null, no hace nada.
    /// </summary>
    public async Task StopAsync()
    {
        if (_client != null)
        {
            try
            {
                // Primero se cierra la sesión en Discord y después se detiene y libera el cliente
                // local.
                await _client.LogoutAsync();
                await _client.StopAsync();
                await _client.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_DiscordCloseError"), ex.Message));
            }
            finally
            {
                _client = null;
                Console.WriteLine(I18n.Tr("Log_DiscordDisconnected"));
            }
        }
    }

    /// <summary>
    /// Recibe cada mensaje nuevo de los canales que ve el bot; solo se procesa el canal del enlace.
    /// El que pide es siempre el autor del mensaje: en Discord se sabe con certeza, y el nombre que
    /// supone el parser (la palabra antes del comando) no se usa.
    /// </summary>
    private async Task MessageReceivedAsync(SocketMessage message)
    {
        // Se ignoran los mensajes de bots y los de otros canales.
        if (message.Author.IsBot || message.Channel.Id != _targetChannelId) return;

        var vm = MainWindowViewModel.Instance;
        if (vm == null) return;

        var commands = vm.SettingsVM.Settings.RequestCommands;

        // En el modo moderado el nivel no entra aquí, sino al aprobarlo con una reacción
        // (ReactionAddedAsync). Aquí solo se avisa de que hay una petición pendiente.
        if (vm.SettingsVM.Settings.DiscordOnlyCheckedLevels)
        {
            if (GenericListener.TryParseGenericMessage(message.Content, commands, out _, out string pendiente, true))
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_DiscordWaitingCheck"), message.Author.Username, pendiente));

                // La notificación también puede leerse en voz alta: el streamer suele estar jugando
                // y no mirando la consola, y una petición pendiente de aprobación debe llegar a su
                // conocimiento.
                if (vm.SettingsVM.Settings.NotifySystemEvents)
                {
                    await vm.NotificationService.AddNotificationAsync(
                        "Info", "Notif_DiscordWaitingCheck", message.Author.Username, pendiente);
                }
            }

            return;
        }

        // Parser genérico con takeFullText: el nivel es todo lo que va detrás del comando.
        if (GenericListener.TryParseGenericMessage(message.Content, commands, out _, out string query, true))
        {
            string autor = message.Author.Username;
            string idMensaje = message.Id.ToString();

            // El ID del mensaje acompaña al nivel: permite quitarlo si se borra el mensaje y evita
            // añadirlo dos veces al aprobarlo.
            _ = Task.Run(() => GenericListener.ProcessLevelRequestAsync(query, autor, ChatPlatform.Discord, "discord", idMensaje));
        }
    }

    // Emojis aceptados como visto bueno:
    // ✅ U+2705 · ☑️ U+2611 · ✔️ U+2714 · ♥️ U+2665 · ❤️ U+2764 · 👍 U+1F44D
    private static readonly string[] EmojisDeVisto =
    {
        "\u2705", "\u2611", "\u2714", "\u2665", "\u2764", "\U0001F44D"
    };

    /// <summary>
    /// Indica si una reacción es un visto bueno. Antes de comparar se normaliza el emoji, porque
    /// hay dos diferencias que deben ignorarse:
    ///  - El selector de variación (FE0F/FE0E), que Discord envía unas veces sí y otras no.
    ///  - Los modificadores de tono de piel (U+1F3FB..U+1F3FF): 👍🏽 debe valer igual que 👍; si
    ///    no, un moderador con el emoji en otro tono no podría aprobar.
    ///
    /// Se recorre por runas y no por chars porque 👍 es un par suplente en UTF-16 y un bucle de
    /// chars lo partiría.
    ///
    /// Los emojis personalizados nunca coinciden: su Name es el nombre que les dio el servidor.
    /// </summary>
    private static bool EsVistoBueno(IEmote? emote)
    {
        string nombre = emote?.Name ?? "";
        var limpio = new System.Text.StringBuilder();

        foreach (var runa in nombre.EnumerateRunes())
        {
            int valor = runa.Value;
            if (valor == 0xFE0F || valor == 0xFE0E) continue;   // selector de variación
            if (valor >= 0x1F3FB && valor <= 0x1F3FF) continue; // tono de piel
            limpio.Append(runa.ToString());
        }

        return EmojisDeVisto.Contains(limpio.ToString());
    }

    /// <summary>
    /// En el modo moderado, el nivel entra en la cola cuando alguien aprueba el mensaje con una
    /// reacción de visto bueno. Es la contraparte de MessageReceivedAsync cuando
    /// DiscordOnlyCheckedLevels está activo. Puede aprobar cualquiera que pueda reaccionar en el
    /// canal: quién modera se controla con los permisos del canal en Discord.
    /// </summary>
    private async Task ReactionAddedAsync(
        Cacheable<IUserMessage, ulong> mensajeCacheado,
        Cacheable<IMessageChannel, ulong> canalCacheado,
        SocketReaction reaccion)
    {
        var vm = MainWindowViewModel.Instance;
        if (vm == null) return;

        // Con la opción desactivada, las reacciones no tienen efecto.
        if (!vm.SettingsVM.Settings.DiscordOnlyCheckedLevels) return;
        if (canalCacheado.Id != _targetChannelId) return;

        // Las reacciones del propio bot no aprueban nada.
        if (_client?.CurrentUser != null && reaccion.UserId == _client.CurrentUser.Id) return;

        if (!EsVistoBueno(reaccion.Emote)) return;

        // El mensaje puede no estar en caché: ocurre al reaccionar a mensajes anteriores al
        // arranque del bot, el caso habitual cuando un moderador revisa peticiones pendientes.
        var mensaje = await mensajeCacheado.GetOrDownloadAsync();
        if (mensaje == null || mensaje.Author.IsBot) return;

        var commands = vm.SettingsVM.Settings.RequestCommands;
        if (!GenericListener.TryParseGenericMessage(mensaje.Content, commands, out _, out string query, true)) return;

        // Si dos moderadores aprueban el mismo mensaje (o uno le pone dos vistos buenos), el nivel
        // no debe entrar dos veces. El enfriamiento de 3 s del antispam no basta: es por usuario y
        // caduca enseguida. Se comprueban la cola y las aprobaciones que aún se están procesando.
        string idMensaje = mensaje.Id.ToString();
        if (!MensajesEnProceso.TryAdd(idMensaje, 0))
        {
            Console.WriteLine(I18n.Tr("Log_DiscordAlreadyQueued"));
            return;
        }

        if (await YaEstaEnLaFila(idMensaje))
        {
            MensajesEnProceso.TryRemove(idMensaje, out _);
            Console.WriteLine(I18n.Tr("Log_DiscordAlreadyQueued"));
            return;
        }

        // El solicitante es el autor del mensaje, no quien lo aprobó.
        string autor = mensaje.Author.Username;
        Console.WriteLine(string.Format(I18n.Tr("Log_DiscordApproved"), autor, query));

        _ = Task.Run(async () =>
        {
            try
            {
                await GenericListener.ProcessLevelRequestAsync(query, autor, ChatPlatform.Discord, "discord", idMensaje);
            }
            finally
            {
                MensajesEnProceso.TryRemove(idMensaje, out _);
            }
        });
    }

    /// <summary>
    /// Indica si ese mensaje de Discord ya generó un nivel que sigue en el nivel actual o en la
    /// cola. El historial no se consulta: volver a aprobar un nivel ya jugado puede ser
    /// intencionado. Las aprobaciones que aún se están procesando se controlan aparte, con
    /// <see cref="MensajesEnProceso"/>.
    /// </summary>
    private static async Task<bool> YaEstaEnLaFila(string discordMessageId)
    {
        var state = await MainWindowViewModel.Instance.QueueService.ReadQueueStateSafeAsync();

        if (state.CurrentLevel?.DiscordMessageId == discordMessageId) return true;
        return state.Queue.Any(l => l.DiscordMessageId == discordMessageId);
    }

    /// <summary>
    /// Si se borra en Discord un mensaje que ya añadió un nivel, el nivel se quita de la cola. Solo
    /// de la cola: el nivel que se está jugando no se toca.
    /// </summary>
    private async Task MessageDeletedAsync(Cacheable<IMessage, ulong> cachedMessage, Cacheable<IMessageChannel, ulong> channel)
    {
        // Los borrados de otros canales se ignoran.
        if (channel.Id != _targetChannelId) return;

        string deletedMessageId = cachedMessage.Id.ToString();
        var queueService = MainWindowViewModel.Instance.QueueService;

        // Busca un nivel de la cola asociado a este mensaje de Discord y lo quita.
        LevelRequestModel? levelToRemove = null;
        await queueService.ModificarAsync(state =>
        {
            levelToRemove = state.Queue.FirstOrDefault(l => l.DiscordMessageId == deletedMessageId);
            return levelToRemove != null && state.Queue.Remove(levelToRemove);
        });

        if (levelToRemove != null)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_DiscordMessageDeleted"), levelToRemove.LevelId, levelToRemove.RequesterName));
        }
    }

    /// <summary>
    /// Extrae el ID numérico del canal de un enlace de Discord. En
    /// "https://discord.com/channels/servidor/canal" es el número que va después del servidor; si
    /// el enlace es el de un mensaje (".../servidor/canal/mensaje"), se ignora el del mensaje. Se
    /// admite también el ID del canal suelto. Devuelve 0 si el enlace no tiene esa forma.
    /// </summary>
    public static ulong ExtractChannelId(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return 0;

        string limpio = link.Trim();
        int corte = limpio.IndexOfAny(new[] { '?', '#' });
        if (corte >= 0) limpio = limpio.Substring(0, corte);

        if (ulong.TryParse(limpio, out ulong suelto)) return suelto;

        var parts = limpio.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int canales = Array.FindIndex(parts, p => p.Equals("channels", StringComparison.OrdinalIgnoreCase));
        if (canales >= 0 && canales + 2 < parts.Length && ulong.TryParse(parts[canales + 2], out ulong id))
            return id;

        return 0;
    }
}
