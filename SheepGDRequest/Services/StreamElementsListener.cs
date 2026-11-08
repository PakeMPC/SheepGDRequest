using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using SheepGDRequest.Models;

namespace SheepGDRequest.Services;

/// <summary>
/// Lee el chat de los overlays de StreamElements. El historial se reconoce por ráfaga: si el
/// overlay añade más de dos líneas en una misma mutación del DOM, son mensajes antiguos y no
/// se procesan. Si en los primeros <see cref="EsperaHistorialMs"/> no llega ninguna ráfaga, el
/// script empieza a aceptar mensajes igualmente.
///
/// La conexión la establece SheepChatListener (rama StreamElements), que llama a
/// <see cref="AttachToPageAsync"/> antes de cargar la página. Este archivo no usa los puentes de
/// SheepChatListener sino uno propio, <c>sendSEData</c>. Toda la espera del historial la hace el
/// script, así que se repite sola si la página se recarga.
/// </summary>
public class StreamElementsListener
{
    /// <summary>
    /// Tiempo máximo que el script espera la ráfaga del historial antes de empezar a aceptar
    /// mensajes. Tiene que coincidir con el valor del script (5000).
    /// </summary>
    public const int EsperaHistorialMs = 5_000;

    /// <summary>
    /// Conecta a la página la función puente, los mensajes de consola y el script que lee el chat.
    /// Debe llamarse antes de GotoAsync, porque el script se registra como init script.
    /// </summary>
    public static async Task AttachToPageAsync(IPage page, ChatPlatform platform)
    {
        // Errores de JavaScript de la propia página de StreamElements.
        page.PageError += (_, error) => Console.WriteLine(string.Format(I18n.Tr("Log_PageError"), platform, error));

        // El script avisa de sus cambios de estado con códigos ("SE_HISTORY|3"), que aquí se
        // traducen. El resto de la consola de la página se ignora.
        page.Console += (_, msg) =>
        {
            string texto = msg.Text ?? "";
            if (texto.StartsWith("SE_HISTORY|"))
                Console.WriteLine(string.Format(I18n.Tr("Log_SEHistory"), texto.Substring("SE_HISTORY|".Length)));
            else if (texto == "SE_READY_HISTORY")
                Console.WriteLine(I18n.Tr("Log_SEReadyHistory"));
            else if (texto == "SE_READY_TIMEOUT")
                Console.WriteLine(I18n.Tr("Log_SEReadyTimeout"));
        };

        await page.ExposeFunctionAsync("sendSEData", (string jsonString) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;

                // Cada campo tiene su valor de respaldo, para que un JSON incompleto no anule el
                // mensaje.
                string service = LeerTexto(root, "service");
                if (service.Length == 0) service = "streamelements";
                string nick = LeerTexto(root, "nick");
                if (nick.Length == 0) nick = I18n.Tr("Default_Viewer");
                string text = LeerTexto(root, "text");

                if (text.Length > 0) ProcessDetectedMessage(text, nick, platform, service);
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_SEJsonError"), ex.Message));
            }
            // El script no usa el valor devuelto.
            return true;
        });

        // Script del detector de ráfaga. Observa las líneas .chat-line que añade el overlay. Si
        // llegan más de 2 en la misma mutación mientras espera, es el historial: se marcan como
        // procesadas y la espera termina. Si no llega ninguna ráfaga, la espera termina sola a los
        // 5 s; hasta entonces, todo lo que llega se marca y se omite.
        //
        // La plataforma se deduce primero de la propia línea, sin el texto ni el nombre: sus
        // insignias y enlaces (kick-badges, kick.com, youtube) y el nick, que en YouTube lleva @
        // delante. Solo si la línea no dice nada se mira el contenedor, y "twitch" en la línea va
        // al final. El contenedor no puede ir primero: un widget que junta varias plataformas
        // puede estar dentro de un contenedor de Twitch, y entonces los mensajes de Kick se
        // marcaban como de Twitch. El texto y el nombre se quitan porque un mensaje de Twitch que
        // diga "youtube", o un usuario llamado así, debe seguir siendo de Twitch.
        string script = @"
            let isReady = false;

            let shieldTimer = setTimeout(() => {
                isReady = true;
                console.log('SE_READY_TIMEOUT');
            }, 5000);

            const observer = new MutationObserver((mutations) => {
                let newLines = [];

                for (let mutation of mutations) {
                    for (let node of mutation.addedNodes) {
                        if (node.nodeType === 1) {
                            if (node.classList && node.classList.contains('chat-line')) {
                                newLines.push(node);
                            } else {
                                let children = node.querySelectorAll('.chat-line');
                                if (children.length > 0) newLines.push(...children);
                            }
                        }
                    }
                }

                if (!isReady && newLines.length > 2) {
                    console.log('SE_HISTORY|' + newLines.length);
                    newLines.forEach(line => line.dataset.processed = 'true');
                    isReady = true;
                    clearTimeout(shieldTimer);
                    console.log('SE_READY_HISTORY');
                    return;
                }

                for (let line of newLines) {
                    if (!isReady) {
                        line.dataset.processed = 'true';
                        continue;
                    }

                    if (line.dataset.processed) continue;
                    line.dataset.processed = 'true';

                    let userNode = line.querySelector('.username, .name, .nickname');
                    let msgNode = line.querySelector('.message, .text');

                    if (userNode && msgNode) {
                        let text = msgNode.innerText.trim();
                        if (!text) continue;

                        let rawNick = userNode.innerText.trim();

                        let meta = line.cloneNode(true);
                        meta.querySelectorAll('.message, .text, .username, .name, .nickname')
                            .forEach(n => n.remove());
                        let metaHtml = meta.innerHTML.toLowerCase();

                        let detectedPlatform = 'streamelements';
                        if (metaHtml.includes('kick-badges') || metaHtml.includes('kick.com')) detectedPlatform = 'kick';
                        else if (rawNick.startsWith('@') || metaHtml.includes('youtube')) detectedPlatform = 'youtube';
                        else if (line.closest('se-widget-kick-chat, kick-chat, .kick-chat')) detectedPlatform = 'kick';
                        else if (line.closest('se-widget-youtube-chat, youtube-chat, .youtube-chat')) detectedPlatform = 'youtube';
                        else if (line.closest('se-widget-twitch-chat, twitch-chat, .twitch-chat')) detectedPlatform = 'twitch';
                        else if (metaHtml.includes('twitch')) detectedPlatform = 'twitch';

                        if (rawNick.startsWith('@')) rawNick = rawNick.substring(1);

                        window.sendSEData(JSON.stringify({
                            service: detectedPlatform,
                            nick: rawNick,
                            text: text
                        }));
                    }
                }
            });

            window.addEventListener('DOMContentLoaded', () => {
                if (document.body) observer.observe(document.body, { childList: true, subtree: true });
            });
            if (document.readyState === 'complete' || document.readyState === 'interactive') {
                if (document.body) observer.observe(document.body, { childList: true, subtree: true });
            }
        ";

        // Init script en lugar de EvaluateAsync: se vuelve a aplicar en cada carga, así que se
        // mantiene tras las recargas. Por eso el script conecta el observador en DOMContentLoaded
        // y, por si el documento ya estaba listo, también de inmediato.
        await page.AddInitScriptAsync(script);
    }

    /// <summary>
    /// Lee una propiedad de texto de un JSON; vacío si falta o no es texto.
    /// </summary>
    private static string LeerTexto(JsonElement root, string propiedad) =>
        root.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String
            ? (valor.GetString() ?? "").Trim()
            : "";

    /// <summary>
    /// Decide con el parser común
    /// (<see cref="GenericListener.TryParseCommand(string, out string)"/>) si un mensaje es una
    /// petición y, si lo es, la procesa aparte: la búsqueda en GD es lenta y no debe retener el
    /// callback de Playwright.
    /// </summary>
    private static void ProcessDetectedMessage(string chatText, string requester, ChatPlatform platform, string specificPlatform)
    {
        try
        {
            var tipo = GenericListener.TryParseCommand(chatText, out string query);
            if (tipo == GenericListener.TipoMensaje.Nada) return;

            Console.WriteLine(string.Format(
                I18n.Tr(tipo == GenericListener.TipoMensaje.Comando ? "Log_SECmdDetected" : "Log_SEAutoIdDetected"),
                requester, query));

            _ = Task.Run(() => GenericListener.ProcessLevelRequestAsync(query, requester, platform, specificPlatform));
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_SETaskError"), ex.Message));
        }
    }
}
