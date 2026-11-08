using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace SheepGDRequest.Services;

/// <summary>
/// Lo propio de las fuentes de YouTube y Kick, que se leen con el navegador interno como
/// SheepChat: a qué página hay que ir a partir de lo que pegó el usuario, y el script que lee los
/// mensajes de esa página. La sesión, los puentes y la reconexión los pone SheepChatListener; los
/// scripts mandan cada mensaje por su puente dedicado (<c>window.sendDedicatedData</c>) como
/// JSON {platform, user, text}.
///
/// Las dos páginas son los chats emergentes que las propias plataformas ofrecen para OBS, que no
/// piden iniciar sesión para leer. Los scripts dependen de su HTML, comprobado el 2026-11-07 con
/// directos reales: si una plataforma lo cambia, la fuente conecta pero deja de detectar mensajes.
/// </summary>
public static class ChatsDeStreaming
{
    // ================================================================
    // KICK
    // ================================================================
    /// <summary>
    /// Página del chat emergente de un canal de Kick (<c>kick.com/popout/canal/chat</c>), a partir
    /// de "kick.com/canal", de esa misma página o del nombre suelto. Null si no hay un nombre de
    /// canal válido.
    /// </summary>
    public static string? UrlDeChatDeKick(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        string texto = url.Trim().ToLowerInvariant();
        texto = Regex.Replace(texto, @"^https?://", "");
        texto = Regex.Replace(texto, @"^(www\.)?kick\.com/", "");
        texto = Regex.Replace(texto, @"^popout/", "");
        texto = texto.TrimStart('@');

        string canal = texto.Split('/', '?', '#')[0];
        return Regex.IsMatch(canal, @"^[a-z0-9_-]{2,30}$") ? $"https://kick.com/popout/{canal}/chat" : null;
    }

    /// <summary>
    /// Script del chat emergente de Kick. Cada mensaje es un <c>div[data-index]</c> con un número
    /// que crece; dentro, el nombre está en un <c>button</c> con el nombre en "title", después va
    /// un separador ":" (<c>span[aria-hidden]</c>) y, a continuación, el texto. Los avisos del
    /// sistema (suscripciones regaladas, etc.) no llevan ese botón y se ignoran.
    ///
    /// La lista es virtual: al desplazarse, Kick vuelve a pintar mensajes antiguos con su mismo
    /// número. Por eso solo se procesan los números mayores que el último visto. Al cargar, el
    /// historial se marca como visto durante los primeros 3 segundos.
    /// </summary>
    public const string ScriptKick = @"
                let ultimo = -1;
                const aceptarDesde = Date.now() + 3000;

                const leer = (nodo) => {
                    const linea = nodo.matches('div[data-index]') ? nodo : nodo.closest('div[data-index]');
                    if (!linea) return;
                    const indice = parseInt(linea.getAttribute('data-index'), 10);
                    if (isNaN(indice) || indice <= ultimo) return;
                    ultimo = indice;
                    if (Date.now() < aceptarDesde) return;

                    const boton = linea.querySelector('button[title]');
                    const separador = linea.querySelector('span[aria-hidden=""true""]');
                    if (!boton || !separador || !separador.parentElement) return;

                    const usuario = (boton.getAttribute('title') || boton.textContent || '').trim();
                    const cuerpo = separador.parentElement.nextElementSibling;
                    const texto = cuerpo ? cuerpo.textContent.trim() : '';
                    if (texto.length > 0) {
                        window.sendDedicatedData(JSON.stringify({ platform: 'kick', user: usuario, text: texto }));
                    }
                };

                const observer = new MutationObserver((mutations) => {
                    for (const mutation of mutations) {
                        for (const node of mutation.addedNodes) {
                            if (node.nodeType !== 1) continue;
                            if (node.matches('div[data-index]')) leer(node);
                            else node.querySelectorAll('div[data-index]').forEach(leer);
                        }
                    }
                });
                observer.observe(document.body, { childList: true, subtree: true });
                document.querySelectorAll('div[data-index]').forEach(leer);";

    // ================================================================
    // YOUTUBE
    // ================================================================
    /// <summary>
    /// Si lo que pegó el usuario ya señala un vídeo concreto (watch?v=, youtu.be/, /live/ID,
    /// /embed/ID o el propio live_chat?v=), devuelve su ID de 11 caracteres; si no, null.
    /// </summary>
    public static string? IdDeVideoDeYouTube(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var m = Regex.Match(url.Trim(),
            @"(?:[?&]v=|youtu\.be/|/live/|/embed/)([A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Página "en directo" del canal que pegó el usuario (<c>youtube.com/@canal/live</c>), donde
    /// YouTube muestra el directo en curso. Acepta "@canal", el enlace del canal (también
    /// /channel/, /c/ y /user/) o el nombre suelto, que se toma como @canal. Null si no se
    /// reconoce.
    /// </summary>
    public static string? UrlEnDirectoDeYouTube(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        string texto = url.Trim();
        texto = Regex.Replace(texto, @"^https?://", "", RegexOptions.IgnoreCase);
        texto = Regex.Replace(texto, @"^(www\.|m\.)?youtube\.com/", "", RegexOptions.IgnoreCase);

        var m = Regex.Match(texto, @"^(@[^/?#\s]+|channel/[^/?#\s]+|c/[^/?#\s]+|user/[^/?#\s]+)");
        if (m.Success) return $"https://www.youtube.com/{m.Groups[1].Value}/live";

        // Un nombre suelto, sin barras: se trata como el @ del canal.
        if (Regex.IsMatch(texto, @"^[A-Za-z0-9._-]{3,}$")) return $"https://www.youtube.com/@{texto}/live";
        return null;
    }

    /// <summary>
    /// Página del chat emergente de un directo de YouTube.
    /// </summary>
    public static string UrlDeChatDeYouTube(string idVideo) =>
        $"https://www.youtube.com/live_chat?is_popout=1&v={idVideo}";

    /// <summary>
    /// Busca el directo en curso de un canal: abre su página "en directo" en una pestaña aparte del
    /// mismo navegador y lee el ID del vídeo que muestra. Si el canal no está en directo, YouTube
    /// muestra la portada del canal en lugar de un vídeo, y devuelve null.
    /// </summary>
    public static async Task<string?> BuscarDirectoDeYouTubeAsync(IBrowserContext contexto, string urlEnDirecto, CancellationToken token)
    {
        var pagina = await contexto.NewPageAsync();
        try
        {
            await pagina.GotoAsync(urlEnDirecto, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            token.ThrowIfCancellationRequested();

            var reproductor = await pagina.WaitForSelectorAsync("ytd-watch-flexy[video-id]",
                new PageWaitForSelectorOptions { Timeout = 15_000, State = WaitForSelectorState.Attached });
            string? id = reproductor == null ? null : await reproductor.GetAttributeAsync("video-id");
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            try { await pagina.CloseAsync(); } catch { }
        }
    }

    /// <summary>
    /// Cookie que da por respondido el aviso de cookies de YouTube en Europa. Sin ella, YouTube
    /// lleva a consent.youtube.com en lugar de a la página pedida y la fuente no encontraría el
    /// directo.
    /// </summary>
    public static Cookie CookieDeConsentimiento => new()
    {
        Name = "SOCS", Value = "CAI", Domain = ".youtube.com", Path = "/", Secure = true
    };

    /// <summary>
    /// Script del chat emergente de YouTube. Cada mensaje es un
    /// <c>yt-live-chat-text-message-renderer</c> con un id único, el autor en <c>#author-name</c>
    /// (con "@" delante, que se quita) y el texto en <c>#message</c>.
    ///
    /// YouTube abre el chat en "Chat destacado", que oculta parte de los mensajes, así que lo
    /// primero es cambiar a "Chat en directo": se abre el menú del encabezado y se elige la segunda
    /// opción, que es esa en todos los idiomas. Al cambiar, YouTube vuelve a pintar el historial:
    /// por eso los mensajes que hay en pantalla a los 4 segundos se marcan como vistos y solo se
    /// envían los que llegan después.
    /// </summary>
    public const string ScriptYouTube = @"
                const vistos = new Set();
                let listo = false;

                const enviar = (nodo) => {
                    if (!nodo.id || vistos.has(nodo.id)) return;
                    vistos.add(nodo.id);
                    if (!listo) return;

                    const autor = nodo.querySelector('#author-name');
                    const mensaje = nodo.querySelector('#message');
                    let usuario = autor ? autor.textContent.trim() : '';
                    if (usuario.startsWith('@')) usuario = usuario.substring(1);
                    const texto = mensaje ? mensaje.textContent.trim() : '';
                    if (texto.length > 0) {
                        window.sendDedicatedData(JSON.stringify({ platform: 'youtube', user: usuario, text: texto }));
                    }
                };

                const cambiarAChatEnDirecto = () => {
                    const menu = document.querySelector('#live-chat-view-selector-sub-menu');
                    const boton = menu && menu.querySelector('#label, tp-yt-paper-menu-button, [role=button]');
                    if (!boton) return false;
                    boton.click();
                    setTimeout(() => {
                        const opciones = menu.querySelectorAll('tp-yt-paper-listbox a');
                        if (opciones.length > 1) opciones[1].click();
                    }, 600);
                    return true;
                };

                let intentos = 0;
                const esperarMenu = setInterval(() => {
                    if (cambiarAChatEnDirecto() || ++intentos > 20) clearInterval(esperarMenu);
                }, 500);

                const observer = new MutationObserver((mutations) => {
                    for (const mutation of mutations) {
                        for (const node of mutation.addedNodes) {
                            if (node.nodeType !== 1) continue;
                            if (node.nodeName === 'YT-LIVE-CHAT-TEXT-MESSAGE-RENDERER') enviar(node);
                            else node.querySelectorAll('yt-live-chat-text-message-renderer').forEach(enviar);
                        }
                    }
                });
                observer.observe(document.body, { childList: true, subtree: true });

                setTimeout(() => {
                    document.querySelectorAll('yt-live-chat-text-message-renderer').forEach(enviar);
                    listo = true;
                }, 4000);";
}
