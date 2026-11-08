using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SheepGDRequest.Models;

namespace SheepGDRequest.Services;

/// <summary>
/// Lee el chat de un canal de Twitch por su servidor de chat oficial (IRC sobre WebSocket,
/// wss://irc-ws.chat.twitch.tv), en modo anónimo: Twitch permite leer cualquier chat con un nick
/// "justinfanNNNNN" y sin contraseña. No hace falta cuenta, token ni navegador, y el canal no
/// tiene que estar en directo para conectar.
///
/// Solo aporta una sesión (<see cref="SesionAsync"/>): la reconexión, el registro para poder
/// detenerla y los avisos de caída los pone SheepChatListener, igual que en las fuentes de
/// navegador. Al conectar no llega el historial del chat, así que no hace falta descartarlo.
/// </summary>
public static class TwitchListener
{
    private const string Servidor = "wss://irc-ws.chat.twitch.tv:443";

    // Twitch manda un PING cada unos cinco minutos. Si en este tiempo no llega nada, la conexión
    // se da por muerta (por ejemplo, tras suspender el PC) y la sesión termina para reconectar.
    private static readonly TimeSpan SilencioMaximo = TimeSpan.FromMinutes(7);

    /// <summary>
    /// Saca el nombre del canal de lo que haya pegado el usuario: "twitch.tv/canal",
    /// "https://www.twitch.tv/canal/videos", el chat emergente ("twitch.tv/popout/canal/chat") o
    /// el nombre suelto, con o sin "#". Devuelve el nombre en minúsculas, que es como lo pide el
    /// chat, o una cadena vacía si no parece un nombre de canal de Twitch (de 3 a 25 letras,
    /// números o guiones bajos).
    /// </summary>
    public static string ExtraerCanal(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        string texto = url.Trim().ToLowerInvariant();
        texto = Regex.Replace(texto, @"^https?://", "");
        texto = Regex.Replace(texto, @"^(www\.|m\.)?twitch\.tv/", "");
        texto = Regex.Replace(texto, @"^popout/", "");
        texto = texto.TrimStart('#', '@');

        string canal = texto.Split('/', '?', '#')[0];
        return Regex.IsMatch(canal, @"^[a-z0-9_]{3,25}$") ? canal : "";
    }

    /// <summary>
    /// Una sesión: conecta, se une al canal y procesa los mensajes hasta que la conexión se cae,
    /// Twitch pide reconectar o se cancela <paramref name="token"/>. Llama a
    /// <paramref name="alActivarse"/> cuando Twitch confirma que está dentro del canal.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Si la dirección no tiene un nombre de canal válido.
    /// </exception>
    public static async Task SesionAsync(string url, Action alActivarse, CancellationToken token)
    {
        string canal = ExtraerCanal(url);
        if (canal.Length == 0) throw new ArgumentException(I18n.Tr("Log_TwitchInvalidChannel"));

        Console.WriteLine(string.Format(I18n.Tr("Log_TwitchConnecting"), canal));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Servidor), token);

        // Las etiquetas (tags) traen el nombre visible del usuario, con sus mayúsculas.
        string nick = "justinfan" + Random.Shared.Next(10000, 99999);
        await EnviarAsync(ws, "CAP REQ :twitch.tv/tags twitch.tv/commands", token);
        await EnviarAsync(ws, "PASS SCHMOOPIIE", token);
        await EnviarAsync(ws, "NICK " + nick, token);
        await EnviarAsync(ws, "JOIN #" + canal, token);

        bool activo = false;
        var buffer = new byte[16 * 1024];
        var pendiente = new StringBuilder();

        // Decodificador con memoria: un carácter de varios bytes (un acento, un emoji) puede quedar
        // partido entre dos trozos de un mismo mensaje.
        var decodificador = Encoding.UTF8.GetDecoder();
        var caracteres = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(token);
            limite.CancelAfter(SilencioMaximo);

            WebSocketReceiveResult resultado;
            try
            {
                resultado = await ws.ReceiveAsync(buffer, limite.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Demasiado tiempo sin noticias de Twitch: se reconecta.
                return;
            }

            if (resultado.MessageType == WebSocketMessageType.Close) return;

            int leidos = decodificador.GetChars(buffer, 0, resultado.Count, caracteres, 0, flush: resultado.EndOfMessage);
            pendiente.Append(caracteres, 0, leidos);
            if (!resultado.EndOfMessage) continue;

            // Un mensaje del WebSocket puede traer varias líneas IRC, separadas por "\r\n".
            string[] lineas = pendiente.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            pendiente.Clear();

            foreach (string linea in lineas)
            {
                if (linea.StartsWith("PING"))
                {
                    await EnviarAsync(ws, "PONG" + linea.Substring(4), token);
                }
                else if (linea.Contains(" RECONNECT"))
                {
                    // Twitch avisa antes de reiniciar un servidor: se cierra y se vuelve a entrar.
                    return;
                }
                else if (!activo && linea.Contains($" JOIN #{canal}"))
                {
                    activo = true;
                    Console.WriteLine(string.Format(I18n.Tr("Log_InterceptorActive"), ChatPlatform.Twitch));
                    alActivarse();
                }
                else if (TryParsePrivmsg(linea, out string usuario, out string texto))
                {
                    Procesar(usuario, texto);
                }
            }
        }
    }

    private static Task EnviarAsync(ClientWebSocket ws, string linea, CancellationToken token) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(linea + "\r\n"), WebSocketMessageType.Text, true, token);

    /// <summary>
    /// Lee una línea PRIVMSG (un mensaje del chat):
    /// <c>@display-name=Pepe;... :pepe!pepe@pepe.tmi.twitch.tv PRIVMSG #canal :!add 128</c>.
    /// El nombre es el visible (display-name) y, si no viene, el del prefijo. Los mensajes con /me
    /// llegan envueltos en "\u0001ACTION ...\u0001", que se quita.
    /// </summary>
    /// <returns>false si la línea no es un mensaje del chat.</returns>
    public static bool TryParsePrivmsg(string linea, out string usuario, out string texto)
    {
        usuario = ""; texto = "";

        string resto = linea;
        string etiquetas = "";
        if (resto.StartsWith("@"))
        {
            int espacio = resto.IndexOf(' ');
            if (espacio < 0) return false;
            etiquetas = resto.Substring(1, espacio - 1);
            resto = resto.Substring(espacio + 1);
        }

        // ":nick!nick@nick.tmi.twitch.tv PRIVMSG #canal :texto"
        var m = Regex.Match(resto, @"^:([^!\s]+)![^\s]* PRIVMSG #[^\s]+ :(.*)$");
        if (!m.Success) return false;

        foreach (string etiqueta in etiquetas.Split(';'))
        {
            if (etiqueta.StartsWith("display-name="))
            {
                usuario = etiqueta.Substring("display-name=".Length).Trim();
                break;
            }
        }
        if (usuario.Length == 0) usuario = m.Groups[1].Value;

        texto = m.Groups[2].Value;
        if (texto.StartsWith("\u0001ACTION ") && texto.EndsWith("\u0001"))
            texto = texto.Substring(8, texto.Length - 9);

        return texto.Length > 0;
    }

    /// <summary>
    /// Pasa el mensaje por el parser común y, si es una petición, la procesa aparte: la búsqueda
    /// en GD es lenta y no debe frenar la lectura del chat.
    /// </summary>
    private static void Procesar(string usuario, string texto)
    {
        try
        {
            var tipo = GenericListener.TryParseCommand(texto, out string query);
            if (tipo == GenericListener.TipoMensaje.Nada) return;

            Console.WriteLine(string.Format(
                I18n.Tr(tipo == GenericListener.TipoMensaje.Comando ? "Log_TwitchCmd" : "Log_TwitchAutoId"),
                usuario, query));

            _ = Task.Run(() => GenericListener.ProcessLevelRequestAsync(query, usuario, ChatPlatform.Twitch, "twitch"));
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_TwitchError"), ex.Message));
        }
    }
}
