namespace SheepGDRequest.Services;

/// <summary>
/// Script que lee el chat de SocialStream.Ninja (el agregador que reúne Twitch, YouTube, Kick…
/// en una sola página). Este archivo solo aporta el JavaScript: la conexión la establece
/// SheepChatListener (rama SocialStreamNinja), que lo inyecta como init script y recibe los
/// mensajes por su puente dedicado, <c>sendDedicatedData</c>, como JSON {platform, user, text}.
///
/// Lee los atributos que SSN añade a cada mensaje: <c>data-chatname</c> (usuario),
/// <c>data-source-type</c> (plataforma) y <c>data-mid</c> (ID del mensaje). Los mensajes sin
/// <c>data-mid</c> se ignoran.
///
/// El historial se filtra por ID: cuando aparece el contenedor #output, el script memoriza los
/// mensajes que ya hay en pantalla. Como SSN podría pintar parte del historial un poco después,
/// lo que llega en los 3 s siguientes también se memoriza sin enviarse.
///
/// Si un mensaje no trae nombre, el script envía '' y C# pone el nombre por defecto traducido.
/// </summary>
public class SocialStreamListener
{
    /// <summary>
    /// Devuelve el script como texto; quien lo inyecta es SheepChatListener.
    /// </summary>
    public static string GetObserverScript()
    {
        return @"
            function inyectarOjoNinja() {
                const logContainer = document.getElementById('output');
                if (!logContainer) {
                    // Si el contenedor todavía no existe, se vuelve a intentar en medio segundo.
                    setTimeout(inyectarOjoNinja, 500);
                    return;
                }

                let procesados = new Set();
                const aceptarDesde = Date.now() + 3000;

                // 1. Historial: se memorizan los mensajes que ya hay en pantalla al cargar, para no
                // procesarlos.
                document.querySelectorAll('#output div[data-chatname]').forEach(nodo => {
                    let id = nodo.getAttribute('data-mid');
                    if (id) procesados.add(id);
                });

                // 2. Observador de mutaciones para los mensajes nuevos.
                const observer = new MutationObserver((mutations) => {
                    for (let mutation of mutations) {
                        for (let node of mutation.addedNodes) {
                            if (node.nodeType === 1) {

                                // Mensajes: cualquier elemento con el atributo data-chatname.
                                let mensajes = node.hasAttribute('data-chatname')
                                    ? [node]
                                    : Array.from(node.querySelectorAll('div[data-chatname]'));

                                mensajes.forEach(msgNode => {
                                    let id = msgNode.getAttribute('data-mid');

                                    // Sin ID, o ya procesado: se omite.
                                    if (!id || procesados.has(id)) return;

                                    procesados.add(id);

                                    // Lo que llega en los primeros 3 s también es historial.
                                    if (Date.now() < aceptarDesde) return;

                                    // Datos del mensaje, tomados de los atributos que añade SSN.
                                    let user = msgNode.getAttribute('data-chatname') || '';
                                    let platform = msgNode.getAttribute('data-source-type') || 'socialstreamninja';
                                    let textNode = msgNode.querySelector('.hl-message');

                                    if (textNode) {
                                        // textContent ignora imágenes e iconos: solo el texto.
                                        let text = textNode.textContent.trim();

                                        if (text.length > 0 && typeof window.sendDedicatedData === 'function') {
                                            let payload = JSON.stringify({ platform: platform, user: user, text: text });
                                            window.sendDedicatedData(payload);
                                        }
                                    }
                                });
                            }
                        }
                    }
                });

                observer.observe(logContainer, { childList: true, subtree: true });
                console.log('[SocialStream] Ojo inyectado y vigilando.');

                // 3. Limpieza periódica: el conjunto de IDs no crece sin límite en directos largos.
                setInterval(() => {
                    if (procesados.size > 500) {
                        let vivos = new Set();
                        document.querySelectorAll('#output div[data-chatname]').forEach(n => {
                            if (n.getAttribute('data-mid')) vivos.add(n.getAttribute('data-mid'));
                        });
                        let limpio = new Set();
                        procesados.forEach(id => { if (vivos.has(id)) limpio.add(id); });
                        procesados = limpio;
                    }
                }, 60000);
            }

            // Punto de entrada.
            inyectarOjoNinja();
        ";
    }
}
