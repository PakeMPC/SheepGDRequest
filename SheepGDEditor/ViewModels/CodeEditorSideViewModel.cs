using CommunityToolkit.Mvvm.ComponentModel;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using System.Collections.Generic;

namespace SheepGDEditor.ViewModels;

/// <summary>
/// Estado del editor de código del Editor: los archivos de texto del tema (index.html, engine.js,
/// engine.css...) y el que se está mostrando.
///
/// Los archivos viven en memoria, en <see cref="FileBuffers"/>, y el editor (AvaloniaEdit)
/// muestra uno cada vez a través de <see cref="EditorDocument"/>. Cambiar de archivo, volcar lo
/// escrito al diccionario y regenerar theme.json lo hace MainWindowViewModel.OpenCodeFile; al
/// guardar, EscribirZip mete estos archivos en el ZIP, menos theme.json, que se genera aparte.
///
/// También contiene la plantilla de fábrica de todo tema nuevo, incluido engine.js, el motor que
/// dibuja los widgets en OBS: ver <see cref="CreateDefaultBuffers"/>.
/// </summary>
public partial class CodeEditorSideViewModel : ObservableObject
{
    /// <summary>
    /// Ruta, dentro del tema, del archivo que se está mostrando (por ejemplo "playing/index.html").
    /// Es también su clave en <see cref="FileBuffers"/>.
    /// </summary>
    [ObservableProperty] private string _currentFileName = "index.html";

    /// <summary>
    /// El texto que muestra y edita el editor. Arranca con un HTML de relleno que no pertenece a
    /// ningún archivo: ver <see cref="IsDocumentLoaded"/>.
    /// </summary>
    [ObservableProperty] private TextDocument _editorDocument = new TextDocument(
        "<!DOCTYPE html>\n<html lang=\"es\">\n<head>\n    <meta charset=\"UTF-8\">\n    <title>Widget Base</title>\n</head>\n<body>\n    <!-- Tu código aquí -->\n</body>\n</html>");

    /// <summary>
    /// Coloreado de sintaxis del archivo actual (HTML, JavaScript o CSS). theme.json se colorea
    /// como JavaScript: AvaloniaEdit trae uno para JSON ("Json"), pero pensado para fondo claro
    /// (la puntuación en negro y las claves en magenta oscuro), y en el editor, que es oscuro, las
    /// llaves y las comas no se verían.
    /// </summary>
    [ObservableProperty] private IHighlightingDefinition? _syntaxHighlighting = HighlightingManager.Instance.GetDefinition("HTML");

    /// <summary>
    /// Mensaje corto que se muestra en la cabecera del editor ("Guardado", "Tema cargado",
    /// errores...). Va en su propia propiedad a propósito: cuando se escribía en
    /// <see cref="CurrentFileName"/>, el mensaje acababa colándose en el diccionario de archivos
    /// como si fuera uno más, y de ahí al ZIP.
    /// </summary>
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>
    /// Si <see cref="EditorDocument"/> contiene de verdad el archivo <see cref="CurrentFileName"/>.
    /// Mientras es false, lo que haya en el editor NO se vuelca al diccionario.
    ///
    /// Arranca en false a propósito. Al abrir la aplicación, CurrentFileName ya vale "index.html"
    /// pero el documento lleva el HTML de relleno de arriba. Volcarlo machacaba el index.html de
    /// verdad, y el tema salía con un HTML de 158 bytes sin la línea que carga engine.js: pantalla
    /// en blanco en la vista previa y en OBS.
    ///
    /// Lo deben poner en true quienes cargan un archivo real en el documento: LoadTheme y
    /// OpenCodeFile, los dos en MainWindowViewModel.
    /// </summary>
    public bool IsDocumentLoaded { get; set; }

    /// <summary>
    /// Los archivos de texto del tema, por su ruta dentro del ZIP ("engine.js",
    /// "queue/index.html"...). Lo que se edita vive aquí hasta que se guarda el tema.
    /// </summary>
    public Dictionary<string, string> FileBuffers { get; set; } = CreateDefaultBuffers();

    /// <summary>
    /// Los archivos de fábrica de un tema: los estilos y el motor comunes, una página por widget
    /// y un theme.json provisional. Se usan al arrancar el Editor y como base al abrir un ZIP: lo
    /// que traiga el ZIP se pone encima, y así un tema que venga sin engine.js o sin engine.css
    /// recibe los de fábrica en lugar de quedarse sin ellos.
    ///
    /// Cada llamada devuelve un diccionario nuevo, para que editar un tema no toque la plantilla.
    /// </summary>
    public static Dictionary<string, string> CreateDefaultBuffers() => new Dictionary<string, string>()
    {
        // Estilos comunes a todas las páginas: sin márgenes ni barras, fondo transparente (OBS
        // superpone el widget a la imagen del directo) y cada objeto en posición absoluta.
        { "engine.css",
            // language=css
            @"body, html { margin: 0; padding: 0; width: 100vw; height: 100vh; overflow: hidden; background-color: transparent; }
.widget-item { position: absolute; box-sizing: border-box; transform-origin: center center; }"
        },

        // El motor que dibuja los widgets. Su propia cabecera explica cómo funciona.
        //
        // Ojo al editarlo aquí: es una cadena literal de C# (@"..."), así que una comilla doble
        // sin duplicar rompe la compilación, y el error aparece lejos de donde está. Para
        // comprobarlo sin compilar: extraer el texto (tratando cada "" como una comilla), guardarlo
        // como .js y pasarle node --check.
        { "engine.js",
            //language=js
@"// =============================================================================================
// engine.js - MOTOR DE LOS WIDGETS DE SheepGD
// ---------------------------------------------------------------------------------------------
// Dibuja el tema dentro del navegador de OBS (y en la vista previa del Editor). Lo usan las
// cuatro paginas del tema: index.html (unificado), playing/, queue/ y notification/. Cada una
// llama a loadTheme('<widget>') al cargar.
//
// Como funciona, en tres pasos:
//   1. loadTheme lee theme.json, registra las fuentes del tema y dibuja los objetos fijos del
//      lienzo con buildWidgetHtml. Los objetos de la cola y de las notificaciones no se dibujan
//      sueltos: se guardan como PLANTILLA de una tarjeta.
//   2. connectWebSocket se conecta a SheepGDRequest (o a la vista previa del Editor) por /ws y
//      recibe los datos en vivo: el nivel que se esta jugando, la cola y las notificaciones.
//   3. updateWidgetsInContainer pone esos datos en cada objeto segun su tipo, y renderQueueCards
//      y renderNotificationCards repiten la plantilla una vez por nivel o por notificacion.
//
// Mensajes que llegan por el WebSocket (campo 'event'):
//   - state_updated:    estado completo; data.current es el nivel actual y data.queue la cola.
//   - now_playing:      solo el nivel actual, en data.
//   - new_notification: una notificacion nueva, en data.
//   - language_changed: el streamer cambio de idioma; data.language es 'es', 'en' o 'pt'.
//   - reload_theme:     hay que volver a leer el tema; se recarga la pagina entera.
// El unico mensaje que envia el widget es request_state, para pedir el estado actual.
//
// Los comentarios de este archivo van sin tildes ni enes a proposito: las paginas playing,
// queue y notification no declaran su codificacion, y el navegador puede leer este archivo como
// windows-1252 en lugar de UTF-8.
// =============================================================================================

// Idioma de los textos que el propio widget escribe ('es', 'en' o 'pt'). Llega con los datos de
// SheepGDRequest; hasta entonces, espanol.
window.sheepLanguage = 'es';

// ==========================================
// COLORES: DE AVALONIA A CSS
// ==========================================
// El Editor guarda los colores como los escribe Avalonia: '#AARRGGBB', con el alfa DELANTE.
// CSS espera '#RRGGBBAA', con el alfa DETRAS, asi que se mueve. Los de 6 cifras ('#RRGGBB')
// valen igual en los dos y se devuelven tal cual. Sin color, transparente.
function formatColor(colorStr) {
    if (!colorStr) return 'transparent';
    if (colorStr.startsWith('#') && colorStr.length === 9) {
        return '#' + colorStr.substring(3) + colorStr.substring(1, 3);
    }
    return colorStr;
}

// ==========================================
// CAJA QUE ENVUELVE UNA PLANTILLA
// ==========================================
// El rectangulo mas pequeno que contiene todos los objetos de una plantilla (la de la cola o la
// de las notificaciones), teniendo en cuenta su recorte y su rotacion. Es el tamano de cada
// tarjeta, y su esquina superior izquierda es el punto desde el que se colocan las tarjetas.
// isValid es false si ningun objeto tiene parte visible.
function getBoundingBox(template) {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    template.forEach(w => {
        const cx = w.x + (w.width / 2), cy = w.y + (w.height / 2);
        const cropL = w.cropLeft || 0, cropR = w.cropRight || 0, cropT = w.cropTop || 0, cropB = w.cropBottom || 0;
        // La parte visible del objeto, una vez quitado el recorte. Si no queda nada, no cuenta.
        const left = w.x + cropL, right = w.x + w.width - cropR, top = w.y + cropT, bottom = w.y + w.height - cropB;
        if (left >= right || top >= bottom) return;

        // Se giran las cuatro esquinas alrededor del centro del objeto, como hace el navegador
        // con transform: rotate, y se amplia la caja para que las contenga.
        const corners = [{x: left, y: top}, {x: right, y: top}, {x: left, y: bottom}, {x: right, y: bottom}];
        const rad = (w.rotationAngle || 0) * Math.PI / 180;
        const cos = Math.cos(rad), sin = Math.sin(rad);

        corners.forEach(p => {
            const dx = p.x - cx, dy = p.y - cy;
            const rx = cx + (dx * cos - dy * sin);
            const ry = cy + (dx * sin + dy * cos);
            if (rx < minX) minX = rx; if (rx > maxX) maxX = rx;
            if (ry < minY) minY = ry; if (ry > maxY) maxY = ry;
        });
    });
    return { minX, minY, maxX, maxY, isValid: minX !== Infinity };
}

// ==========================================
// DATOS EN VIVO (WEBSOCKET)
// ==========================================
// Se conecta al mismo servidor que sirve la pagina, en la ruta /ws, y reparte cada mensaje que
// llega. Si la conexion se corta, lo vuelve a intentar cada 3 segundos, sin limite: OBS puede
// tener el widget abierto antes de que arranque SheepGDRequest.
function connectWebSocket(widgetName) {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${protocol}//${window.location.host}/ws`;
    const ws = new WebSocket(wsUrl);

    // Al conectar se pide el estado actual, y los cuadros de notificacion que esten fijos en el
    // lienzo empiezan ocultos: se encienden cuando llega una notificacion.
    ws.onopen = () => {
        console.log('[SheepGD] Conectado al bot en vivo.');
        ws.send(JSON.stringify({ event: 'request_state' }));
        document.querySelectorAll('[data-sheep-type=\'NotificationBox\']').forEach(el => {
            el.style.opacity = 0;
            el.style.transition = 'opacity 0.4s ease-in-out';
        });
    };

    ws.onmessage = (event) => {
        const msg = JSON.parse(event.data);
        // Se recarga si el aviso es para este widget, si es para todos ('unified') o si esta es
        // la pagina unificada, que contiene a los demas.
        if (msg.event === 'reload_theme' && (msg.target === widgetName || msg.target === 'unified' || widgetName === 'unified')) {
            window.location.reload();
        }

        // Cambio de idioma: se apunta y se pide el estado otra vez para repintar los textos.
        if (msg.event === 'language_changed') {
            window.sheepLanguage = msg.data.language;
            ws.send(JSON.stringify({ event: 'request_state' }));
        }

        if (msg.event === 'now_playing' || msg.event === 'state_updated') {
            if (msg.data.language) window.sheepLanguage = msg.data.language;

            // state_updated trae el nivel actual en data.current; now_playing, en data.
            const currentData = msg.event === 'state_updated' ? msg.data.current : msg.data;

            // Se guarda el ID del nivel actual para las notificaciones que no traigan uno propio
            // (ver renderNotificationCards).
            if (currentData) {
                window.currentPlayingLevelId = currentData.levelId;
            }

            if (msg.event === 'state_updated') {
                const queueData = msg.data.queue || [];
                renderQueueCards(queueData);
            }
            updateWidgetsInContainer(currentData, document.getElementById('canvas'));
        }

        if (msg.event === 'new_notification') { pushNotification(msg.data); }
    };

    ws.onclose = () => {
        setTimeout(() => connectWebSocket(widgetName), 3000);
    };
}

// ==========================================
// NOTIFICACIONES
// ==========================================
// Las notificaciones esperan en pendingNotifs y pasan a activeNotifs (las que estan en
// pantalla) cuando hay hueco: como mucho notificationMaxItems a la vez. Cada activa guarda:
//   data:        los datos que llegaron (usuario, mensaje, nivel...).
//   elapsed:     segundos que lleva en pantalla.
//   maxDuration: cuando desaparece la tarjeta entera: la duracion de su objeto que mas dura,
//                mas medio segundo para que termine el fundido.
//   DOM:         su tarjeta en la pagina, o null si todavia no se ha dibujado.
let pendingNotifs = [];
let activeNotifs = [];

// Mete una notificacion en la cola de espera y la muestra si hay hueco.
function pushNotification(notif) {
    pendingNotifs.push(notif);
    checkNotifQueue();
}

// Pasa notificaciones de la espera a la pantalla mientras quepan, y las dibuja. Si el tema no
// tiene objetos de notificacion, no hace nada.
function checkNotifQueue() {
    if (!window.notificationTemplate || window.notificationTemplate.length === 0) return;
    const maxItems = window.sheepTheme.notificationMaxItems || 3;
    let added = false;

    while (activeNotifs.length < maxItems && pendingNotifs.length > 0) {
        const notifData = pendingNotifs.shift();

        let cardMaxDuration = 0;
        window.notificationTemplate.forEach(w => {
            const dur = w.notificationDuration !== undefined ? w.notificationDuration : 6.0;
            if (dur > cardMaxDuration) cardMaxDuration = dur;
        });

        activeNotifs.push({ data: notifData, elapsed: 0, maxDuration: cardMaxDuration + 0.5, DOM: null });
        added = true;
    }
    if (added) renderNotificationCards();
}

// Reloj de las notificaciones, cada 100 ms. Suma el tiempo, apaga cada objeto de la tarjeta
// cuando pasa su propia duracion y quita la tarjeta cuando pasa la de todos.
// notificationTimerMode: 0 = simultaneo, todas descuentan a la vez; 1 = en cascada, solo
// descuenta la mas antigua y las demas esperan su turno.
setInterval(() => {
    if (activeNotifs.length === 0) return;
    const mode = window.sheepTheme.notificationTimerMode !== undefined ? window.sheepTheme.notificationTimerMode : 1;
    const dt = 0.1;
    let needsRender = false;

    for (let i = 0; i < activeNotifs.length; i++) {
        let notif = activeNotifs[i];

        if (mode === 1 && i !== 0) continue;

        notif.elapsed += dt;

        // Los objetos de la tarjeta estan en el mismo orden que en la plantilla, asi que el
        // indice de cada uno sirve para buscar su duracion.
        if (notif.DOM) {
            window.notificationTemplate.forEach((w, index) => {
                const dur = w.notificationDuration !== undefined ? w.notificationDuration : 6.0;
                const el = notif.DOM.children[index];
                if (el && notif.elapsed >= dur && el.style.opacity !== '0') {
                    el.style.opacity = 0;
                }
            });
        }

        // Tarjeta terminada: se quita de la lista (el i-- compensa el hueco que deja).
        if (notif.elapsed >= notif.maxDuration) {
            activeNotifs.splice(i, 1);
            i--;
            needsRender = true;
        }
    }

    // Si se libero un hueco, entra la siguiente en espera y se redibuja.
    if (needsRender) {
        checkNotifQueue();
        renderNotificationCards();
    }
}, 100);

// Pone en la pagina las tarjetas de las notificaciones activas, una por notificacion, en fila o
// en columna segun el tema. Las que ya estaban dibujadas no se tocan.
function renderNotificationCards() {
    const container = document.getElementById('notification-container') || document.getElementById('unified-container');
    if (!container || !window.notificationTemplate || window.notificationTemplate.length === 0) return;

    const box = getBoundingBox(window.notificationTemplate);
    if (!box.isValid) return;
    const boxWidth = box.maxX - box.minX; const boxHeight = box.maxY - box.minY;

    let flexWrapper = container.querySelector('.notif-flex-wrapper');
    if (!flexWrapper) {
        flexWrapper = document.createElement('div');
        flexWrapper.className = 'notif-flex-wrapper';
        flexWrapper.style.position = 'absolute';
        flexWrapper.style.left = `${box.minX}px`;
        flexWrapper.style.top = `${box.minY}px`;
        container.appendChild(flexWrapper);
    }
    flexWrapper.style.display = 'flex';
    flexWrapper.style.flexDirection = window.sheepTheme.notificationDirection === 1 ? 'row' : 'column';
    flexWrapper.style.gap = `${window.sheepTheme.notificationSpacing !== undefined ? window.sheepTheme.notificationSpacing : 20}px`;

    // Solo se borran las tarjetas que ya no estan activas. Las demas se quedan como estan: asi
    // no se reinicia su fundido ni su marquesina cada vez que llega otra.
    const currentDOMs = activeNotifs.map(n => n.DOM).filter(d => d);
    Array.from(flexWrapper.children).forEach(child => {
        if (!currentDOMs.includes(child)) {
            child.remove();
        }
    });

    activeNotifs.forEach(notifItem => {
        // Solo se construyen las tarjetas que aun no existen.
        if (!notifItem.DOM) {
            const card = document.createElement('div');
            card.className = 'notif-card';
            card.style.position = 'relative';
            card.style.width = `${boxWidth}px`;
            card.style.height = `${boxHeight}px`;

            window.notificationTemplate.forEach(w => {
                // Las coordenadas pasan a ser relativas a la tarjeta, no al lienzo.
                const widgetEl = buildWidgetHtml(w, box.minX, box.minY);
                const dur = w.notificationDuration !== undefined ? w.notificationDuration : 6.0;

                // Un objeto cuyo tiempo ya paso nace apagado y sin fundido.
                if (notifItem.elapsed >= dur) {
                    widgetEl.style.transition = 'none';
                    widgetEl.style.opacity = 0;
                } else {
                    widgetEl.style.transition = 'opacity 0.4s ease-in-out';
                }
                card.appendChild(widgetEl);
            });

            // Para que el fondo del nivel tenga miniatura, hace falta un ID: si la notificacion no
            // trae uno, se busca un numero de 5 cifras o mas en levelData y, si tampoco hay, se
            // usa el del nivel que se esta jugando.
            if (!notifItem.data.levelId && notifItem.data.levelData) {
                const match = notifItem.data.levelData.match(/\b\d{5,}\b/);
                if (match) notifItem.data.levelId = match[0];
            }

            if (!notifItem.data.levelId && window.currentPlayingLevelId) {
                notifItem.data.levelId = window.currentPlayingLevelId;
            }

            updateWidgetsInContainer(notifItem.data, card);
            injectNotificationData(notifItem.data, card);

            notifItem.DOM = card;
            flexWrapper.appendChild(card);
        }
    });
    scheduleMarqueeUpdate();
}

// Cambia los caracteres que el navegador leeria como HTML por su forma escrita, para que un
// nombre como '<i>ana' salga tal cual en vez de poner en cursiva el resto del mensaje.
function escapeHtml(text) {
    return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// Donde va cada nombre dentro del mensaje: la PRIMERA vez que sale cada uno, buscando antes el
// mas largo, y sin que se pisen (si el corto solo aparece dentro del largo, se busca la siguiente
// vez que sale; si no sale mas, se queda sin color). Con dos nombres igual de largos, gana el que
// va primero en la lista. Devuelve los tramos por orden de posicion. Es el mismo calculo que
// TramosDeNombres del Editor, para que el lienzo y OBS coincidan.
function findNameRanges(text, names) {
    const ranges = [];
    names.filter(n => n.value)
        .sort((a, b) => b.value.length - a.value.length)
        .forEach(n => {
            let from = 0;
            while (true) {
                const start = text.indexOf(n.value, from);
                if (start < 0) break;
                const end = start + n.value.length;
                if (!ranges.some(r => start < r.end && end > r.start)) {
                    ranges.push({ start: start, end: end, color: n.color });
                    break;
                }
                from = start + 1;
            }
        });
    return ranges.sort((a, b) => a.start - b.start);
}

// Escribe el mensaje en los cuadros de notificacion de una tarjeta, con el nombre del usuario y
// el del nivel en sus colores. El mensaje llega ya traducido de SheepGDRequest; si no llega, se
// usa la clave de traduccion y, en ultimo caso, la palabra 'Notificacion' en el idioma actual.
// Todo lo que viene del chat (el mensaje, el usuario y el nivel) se escapa antes de meterlo como
// HTML, y los colores van por posicion (findNameRanges). Antes se sustituia cada aparicion del
// nombre dentro del mensaje sin escapar nada: un usuario corto coloreaba trozos de otras palabras,
// y un '<i>' en un nombre cambiaba el aspecto del directo.
function injectNotificationData(notif, cardDOM) {
    cardDOM.querySelectorAll('[data-sheep-type=\'NotificationBox\']').forEach(el => {
        let notifFallback = 'Notificación';
        if (window.sheepLanguage === 'en') notifFallback = 'Notification';
        if (window.sheepLanguage === 'pt') notifFallback = 'Notificação';
        const message = String(notif.message || notif.translationKey || notifFallback);

        const ranges = findNameRanges(message, [
            { value: notif.username ? String(notif.username) : '', color: el.dataset.reqColor },
            { value: notif.levelData ? String(notif.levelData) : '', color: el.dataset.lvlColor }
        ]);

        // Se va montando el HTML: el texto normal escapado y cada nombre, escapado, en su color.
        let html = '';
        let pos = 0;
        ranges.forEach(r => {
            html += escapeHtml(message.slice(pos, r.start));
            html += `<span style='color:${r.color}'>${escapeHtml(message.slice(r.start, r.end))}</span>`;
            pos = r.end;
        });
        html += escapeHtml(message.slice(pos));

        const innerContainer = el.querySelector('.inner-text-container');
        if (innerContainer) innerContainer.innerHTML = html;
    });
}

// ==========================================
// RELOJ DEL AUTO-SKIP
// ==========================================
// El widget no puede fiarse del texto de tiempo restante ('timeRemainingText') que envia
// SheepGDRequest: cuando caduca el ULTIMO nivel de la cola, la app deja de emitir (en
// AutoSkipTimer_Tick esa rama no envia nada) y el ultimo valor recibido se quedaria clavado en
// pantalla. Por eso el reloj se calcula aqui, a partir del instante absoluto en que termina,
// 'autoSkipEndsAtUnix' (segundos Unix, UTC), igual que hace el mod de Geometry Dash.

// Formatea segundos como mm:ss, o hh:mm:ss a partir de una hora. Es el mismo formato que
// UpdateTimeRemainingText() en SheepGDRequest.
function formatClock(totalSeconds) {
    const pad = (n) => String(n).padStart(2, '0');
    const h = Math.floor(totalSeconds / 3600);
    const m = Math.floor((totalSeconds % 3600) / 60);
    const s = totalSeconds % 60;
    return h > 0 ? `${pad(h)}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}

// Pinta el tiempo que le queda a un objeto Timer, o lo oculta si no hay cuenta atras.
function paintTimer(el) {
    const end = parseInt(el.dataset.skipEnd || '0', 10);

    // 0 = este nivel no tiene auto-skip. Se OCULTA el reloj en vez de pintar '00:00': un cero
    // fijo parece un fallo, y la ausencia de reloj se entiende como 'este nivel no tiene
    // auto-skip'. Es el mismo criterio que sigue el mod.
    if (!end) { el.style.visibility = 'hidden'; return; }

    const left = end - Math.floor(Date.now() / 1000);

    // Caducado: tampoco se pinta '00:00', que no se distingue de un reloj parado.
    if (left <= 0) { el.style.visibility = 'hidden'; return; }

    el.style.visibility = '';
    const inner = el.querySelector('.inner-text-container') || el;
    const txt = formatClock(left);
    if (inner.innerText !== txt) inner.innerText = txt;   // solo si cambia, para no parpadear
}

// El reloj corre por su cuenta: cuando caduca ya no llega ningun mensaje que lo mueva.
// Cada 500 ms y no cada segundo, para que el segundo que se ve no vaya hasta uno por detras;
// las pasadas de mas no cuestan nada, porque paintTimer no toca la pagina si el texto no cambia.
setInterval(() => {
    // El valor del selector va sin comillas a proposito: Timer es un identificador CSS valido, y
    // asi no choca ni con las comillas simples de JS ni con la cadena de C# que contiene este
    // archivo dentro del Editor.
    document.querySelectorAll('.widget-item[data-sheep-type=Timer]').forEach(paintTimer);
}, 500);

// ==========================================
// PONER LOS DATOS EN VIVO EN LOS OBJETOS
// ==========================================
// Recorre los objetos de un contenedor (el lienzo o una tarjeta) y a cada uno le pone el dato
// que le toca segun su tipo (data-sheep-type): el nombre del nivel, la cara de dificultad, el
// reloj... 'data' es un nivel tal como lo envia SheepGDRequest.
function updateWidgetsInContainer(data, container) {
    if (!data || !container) return;

    container.querySelectorAll('.widget-item').forEach(el => {
        const type = el.getAttribute('data-sheep-type');

        // Escribe un texto en el objeto. Si el dato llega vacio, vuelve a su texto de plantilla
        // (el que se escribio en el Editor).
        const setTxt = (txt) => {
            // Se convierte a texto para que la comparacion de abajo funcione tambien con numeros.
            const finalTxt = (txt !== undefined && txt !== null && txt !== '') ? String(txt) : el.getAttribute('data-template');
            const innerContainer = el.querySelector('.inner-text-container');
            if (innerContainer) {
                // Solo se reescribe si cambia: reescribir el mismo texto lo haria parpadear.
                if (innerContainer.innerText === finalTxt) return;
                innerContainer.innerText = finalTxt;
            } else {
                if (el.innerText === finalTxt) return;
                el.innerText = finalTxt;
            }
        };

        // Pone una imagen en el objeto. Si no tiene <img> (o el que indique childSelector), lo
        // crea. Con 'src' vacio, la oculta. 'fallback' es la imagen de repuesto si 'src' no carga;
        // hoy solo la usa el fondo del nivel, cuya miniatura viene de un servidor externo y puede
        // no existir.
        const setImg = (src, childSelector = 'img', fallback = '') => {
            let img = el.querySelector(childSelector);
            if (!img) {
                img = document.createElement('img');
                img.style.width = '100%'; img.style.height = '100%';
                // Como se ajusta la imagen lo decide el objeto (buildWidgetHtml lo deja en
                // data-fit). Sin esto, todas las imagenes creadas aqui (fondo del nivel, icono de
                // plataforma, like y estrella) se quedarian siempre en 'contain'.
                img.style.objectFit = el.dataset.fit || 'contain';
                if (childSelector.startsWith('.')) img.className = childSelector.substring(1);
                el.insertBefore(img, el.firstChild);
            }
            if (!src || src.trim() === '') {
                img.style.display = 'none';
                img.removeAttribute('src');
                img.dataset.lastSrc = ''; // se olvida la ultima imagen pedida
                img.dataset.usingFallback = '';
                return;
            }
            // SheepGDRequest envia las imagenes de su propia aplicacion como recursos de Avalonia
            // ('avares://SheepGDRequest/Assets/...'). Quitando ese prefijo queda la ruta con la que
            // su servidor de widgets las sirve.
            const cleanSrc = src.replace(/^avares:\/\/SheepGDRequest/i, '');

            // Si se pide la misma imagen que la ultima vez, no se hace nada: evita parpadeos.
            // lastSrc guarda lo que se PIDIO, no lo que se VE. Con el repuesto en pantalla sigue
            // guardando la miniatura pedida, y por eso la siguiente actualizacion del mismo nivel
            // se para aqui y no la vuelve a pedir. Si guardara el repuesto, cada estado nuevo
            // volveria a pedir la miniatura que no existe y saturaria al servidor de miniaturas.
            if (img.dataset.lastSrc === cleanSrc) return;
            img.dataset.lastSrc = cleanSrc;

            // A las rutas del propio servidor se les anade la hora, para que el navegador no use
            // una copia vieja guardada en cache.
            const url = cleanSrc.startsWith('/') ? `${cleanSrc}?t=${new Date().getTime()}` : cleanSrc;

            if (fallback) {
                // CON repuesto, la imagen se prueba FUERA de pantalla y la visible solo se cambia
                // si carga. Darle al <img> visible una direccion que va a fallar no es inofensivo:
                // al fallar, el navegador lo deja vacio antes de avisar con onerror, y aunque ahi
                // se le ponga el repuesto, ya se ha visto un fotograma en blanco. Con niveles sin
                // miniatura, eso era un parpadeo en cada cambio de nivel.
                const prueba = new Image();

                prueba.onload = () => {
                    // Respuesta tardia: entretanto ya se pidio otro nivel. Sin esta comprobacion,
                    // la miniatura de un nivel anterior podria pisar la del actual.
                    if (img.dataset.lastSrc !== cleanSrc) return;
                    img.dataset.usingFallback = '';
                    img.onload = () => { img.style.display = 'block'; };
                    img.onerror = () => { img.style.display = 'none'; };
                    img.src = url;   // la prueba ya la dejo en cache: el cambio no deja hueco
                };

                prueba.onerror = () => {
                    if (img.dataset.lastSrc !== cleanSrc) return;   // la misma comprobacion de arriba

                    // Si el repuesto YA esta en pantalla (de un nivel anterior sin miniatura, o
                    // porque buildWidgetHtml lo puso al montar el widget), no se toca NADA. Ni
                    // siquiera se vuelve a asignar el mismo src, que tambien puede costar un
                    // fotograma. Es justo lo que pasa al ir rotando niveles sin miniatura.
                    if (img.dataset.usingFallback === '1' || img.getAttribute('src') === fallback) {
                        img.dataset.usingFallback = '1';
                        return;
                    }

                    img.dataset.usingFallback = '1';
                    img.onload = () => { img.style.display = 'block'; };
                    img.onerror = () => { img.style.display = 'none'; };   // ni el repuesto carga: se oculta
                    img.src = fallback;
                };

                prueba.src = url;
                return;
            }

            // SIN repuesto: se asigna directamente. Los iconos de plataforma, like y estrella van
            // por aqui; sus imagenes son archivos locales y no tienen por que fallar.
            img.dataset.usingFallback = '';
            // La imagen anterior sigue visible hasta que la nueva termina de cargar y la sustituye.
            img.onload = () => { img.style.display = 'block'; };
            img.onerror = () => { img.style.display = 'none'; };
            img.src = url;
        };

        // El nombre de la plataforma sale del nombre del archivo de su icono:
        // '.../icons/twitch.png' -> 'Twitch'.
        let calcPlatform = '';
        if (data.platformIconPath) {
            calcPlatform = data.platformIconPath.split('/').pop().split('.')[0];
            calcPlatform = calcPlatform.charAt(0).toUpperCase() + calcPlatform.slice(1);
        }

        // Que dato recibe cada tipo de objeto. Los tipos que no aparecen aqui (Text, Image,
        // Video, NotificationBox...) no muestran datos del nivel y se quedan como estan.
        switch (type) {
            case 'LevelName': setTxt(data.name); break;
            case 'CreatorName': setTxt(data.author); break;
            case 'LevelId': setTxt(data.levelId); break;
            case 'SentBy': setTxt(data.requesterName); break;
            case 'PlatformName': setTxt(calcPlatform); break;
            case 'Duration': setTxt(data.length); break;
            case 'RateText': setTxt(data.difficultyName); break;
            case 'AredlPos': setTxt(data.aredlRank || '-'); break;
            case 'GddlRate': setTxt(data.gddlTier || '-'); break;
            case 'Timer':
                // Solo se guarda el instante en que termina; del pintado se encarga su reloj.
                el.dataset.skipEnd = data.autoSkipEndsAtUnix || 0;
                paintTimer(el);
                break;
            case 'LikesAndDislikes': setTxt(data.likes); break;
            case 'Stars': setTxt(data.stars); break;
            case 'Downloads': setTxt(data.downloads); break;
            case 'Orbs': setTxt(data.orbs); break;
            case 'PlatformIcon': setImg(data.platformIconPath); break;
            case 'LikeIcon': setImg(data.isDislike ? 'assets/dislike.png' : 'assets/like.png'); break;

            // Los niveles de plataformas dan LUNAS, no estrellas (desde Geometry Dash 2.2). Se sabe
            // por data.length, el mismo dato que usan SheepGDRequest (StarOrMoonConverter) y el
            // mod (sheep::iconStars), asi que no hace falta un campo nuevo en los datos.
            // Se compara en mayusculas porque el valor llega tal cual de la API del juego
            // ('Plat', 'Plat.'...).
            case 'StarIcon': {
                const esPlat = String(data.length || '').toUpperCase().includes('PLAT');
                setImg(esPlat ? 'assets/moon.png' : 'assets/star.png');
                break;
            }

            case 'LevelBackground': {
                // Con nivel, su miniatura, que se descarga de levelthumbs.prevter.me. Sin nivel, o
                // si la miniatura no existe, la imagen propia del objeto (su imagePath, guardado en
                // data-original-image), que es la imagen por defecto que eligio el autor del tema.
                const respaldo = el.getAttribute('data-original-image') || '';
                if (data.levelId) {
                    setImg(`https://levelthumbs.prevter.me/thumbnail/${data.levelId}`, 'img', respaldo);
                } else {
                    setImg(respaldo);
                }
                break;
            }

            case 'RateFace':
                // La cara de dificultad y, detras, el halo de featured, epic, legendary o mythic,
                // si el nivel lo tiene y el tema lo permite (haloEnabled).
                setImg(data.difficultyFacePath, '.face-layer');
                if (el.dataset.haloEnabled !== 'false') {
                    setImg((data.hasFeatureGlow && data.featureGlowPath) ? data.featureGlowPath : null, '.halo-layer');
                }
                break;

            // Si las peticiones de niveles estan abiertas o cerradas, en el idioma actual. Si el
            // dato no llega, vuelve al texto de plantilla.
            case 'RequestStatus': {
                let reqActive = 'Activadas', reqPaused = 'Desactivadas';
                if (window.sheepLanguage === 'en') { reqActive = 'Enabled'; reqPaused = 'Disabled'; }
                if (window.sheepLanguage === 'pt') { reqActive = 'Ativadas'; reqPaused = 'Desativadas'; }
                setTxt(data.isRequestsEnabled !== undefined ? (data.isRequestsEnabled ? reqActive : reqPaused) : null);
                break;
            }
        }
    });
}

// ==========================================
// MARQUESINA (TEXTO QUE SE DESPLAZA)
// ==========================================
// Los textos de una sola linea que no caben en su caja se desplazan de derecha a izquierda.
// Si caben, se quedan quietos. Se recalcula cada vez que cambian los datos.

// Agrupa las peticiones de recalculo: si llegan varias seguidas, solo se hace una, 50 ms
// despues de la ultima.
let marqueeTimeout;
function scheduleMarqueeUpdate() {
    clearTimeout(marqueeTimeout);
    marqueeTimeout = setTimeout(updateAllMarquees, 50);
}

// Mide cada texto de una linea y lo anima si no cabe en la parte visible de su caja (sin el
// recorte). El recorrido va desde el borde derecho visible hasta que el texto sale entero por
// el izquierdo, y dura 10 segundos.
function updateAllMarquees() {
    document.querySelectorAll('.widget-item').forEach(el => {
        const inner = el.querySelector('.marquee-text');
        if (!inner) return;

        const cropL = parseFloat(el.dataset.cropL) || 0;
        const cropR = parseFloat(el.dataset.cropR) || 0;
        const fullWidth = el.offsetWidth;
        const croppedWidth = fullWidth - cropL - cropR;

        if (inner.scrollWidth > croppedWidth) {
            const startX = fullWidth - cropR;
            const endX = cropL - inner.scrollWidth;

            if (!inner.marqueeAnim) {
                inner.marqueeAnim = inner.animate([
                    { transform: `translateX(${startX}px)` },
                    { transform: `translateX(${endX}px)` }
                ], { duration: 10000, iterations: Infinity });
            } else {
                inner.marqueeAnim.effect.setKeyframes([
                    { transform: `translateX(${startX}px)` },
                    { transform: `translateX(${endX}px)` }
                ]);
                if (inner.marqueeAnim.playState !== 'running') inner.marqueeAnim.play();
            }
        } else {
            if (inner.marqueeAnim) inner.marqueeAnim.cancel();
            inner.style.transform = 'translateX(0px)';
        }
    });
}

// ==========================================
// CONSTRUIR UN OBJETO
// ==========================================
// Crea el elemento de la pagina para un objeto del theme.json ('w'). offsetX/offsetY se restan
// a su posicion: se usan en las tarjetas de cola y notificaciones, cuyos objetos se colocan
// relativos a la tarjeta y no al lienzo.
function buildWidgetHtml(w, offsetX = 0, offsetY = 0) {
    const el = document.createElement('div');
    el.className = 'widget-item';
    el.setAttribute('data-sheep-type', w.objectType);

    el.style.left = `${w.x - offsetX}px`;
    el.style.top = `${w.y - offsetY}px`;
    el.style.width = `${w.width}px`;
    el.style.height = `${w.height}px`;
    el.style.zIndex = w.layerIndex || 1;
    el.style.transform = `rotate(${w.rotationAngle || 0}deg)`;

    // 'transparency' es en realidad la OPACIDAD: 1 = totalmente visible.
    const baseOpacity = w.transparency !== undefined ? w.transparency : 1;
    el.dataset.baseOpacity = baseOpacity;
    el.style.opacity = baseOpacity;

    // Recorte y esquinas redondeadas, con clip-path. Normalmente se aplican al objeto entero,
    // con dos excepciones: en la cara de dificultad van solo a la cara, para que su halo pueda
    // salirse de la caja, y a los textos con fondo transparente no se les aplican.
    let applyClipToMain = true;
    if (w.objectType === 'RateFace') applyClipToMain = false;
    if (w.isText && w.backgroundType === 1) applyClipToMain = false;

    let clipStyleString = '';
    const cropT = w.cropTop || 0, cropB = w.cropBottom || 0, cropL = w.cropLeft || 0, cropR = w.cropRight || 0, round = w.borderRoundness || 0;

    // La marquesina necesita el recorte lateral para saber cuanto ancho se ve de verdad.
    el.dataset.cropL = cropL;
    el.dataset.cropR = cropR;

    if (cropT > 0 || cropB > 0 || cropL > 0 || cropR > 0 || round > 0) {
        // El radio nunca pasa de la mitad del lado visible mas corto.
        const safeRadius = Math.min(round, Math.min(w.width - cropL - cropR, w.height - cropT - cropB) / 2);
        clipStyleString = `clip-path: inset(${cropT}px ${cropR}px ${cropB}px ${cropL}px round ${safeRadius}px);`;
        if (applyClipToMain) {
            el.style.clipPath = `inset(${cropT}px ${cropR}px ${cropB}px ${cropL}px round ${safeRadius}px)`;
        }
    }

    if (w.backgroundType === 0 && w.backgroundColor) {
        el.style.backgroundColor = formatColor(w.backgroundColor);
    }

    // --- TEXTOS ---
    if (w.isText) {
        // El texto escrito en el Editor. En los objetos con datos del nivel, es lo que se ve
        // mientras el dato no llega (updateWidgetsInContainer lo recupera de data-template).
        const textContent = w.objectName || '';
        el.setAttribute('data-template', textContent);
        el.style.color = formatColor(w.textColor);
        el.style.fontSize = `${w.fontSize}px`;

        // Contorno del texto. 'paint-order: stroke fill' es la parte importante: sin eso, el trazo
        // se pinta ENCIMA de la letra y la adelgaza por dentro, porque va centrado en el borde de
        // la letra. Con eso, el trazo queda detras y solo se ve su mitad exterior. Las dos
        // propiedades se heredan, asi que puestas aqui llegan al texto de dentro.
        if (w.hasTextOutline && w.textOutlineSize > 0) {
            el.style.webkitTextStroke = `${w.textOutlineSize}px ${formatColor(w.textOutlineColor)}`;
            el.style.paintOrder = 'stroke fill';
        }

        // Los colores del nombre del usuario y del nivel, para injectNotificationData.
        if (w.objectType === 'NotificationBox') {
            el.dataset.reqColor = formatColor(w.requesterColor);
            el.dataset.lvlColor = formatColor(w.levelDataColor);
        }

        // Una sola linea (con marquesina si no cabe) o varias lineas.
        if (!w.isTextWrapped) {
            el.style.whiteSpace = 'nowrap';
            el.innerHTML = `<div class='inner-text-container marquee-text' style='display:inline-block;'>${textContent}</div>`;
        } else {
            el.style.whiteSpace = 'normal';
            el.innerHTML = `<div class='inner-text-container wrapped-text' style='width: 100%; word-break: break-word;'>${textContent}</div>`;
        }

        // Alineacion horizontal: 0 = izquierda, 1 = centro, 2 = derecha, 3 = justificado.
        let alignStr = 'center'; let flexJustify = 'center';
        if (w.textAlignment === 0) { alignStr = 'left'; flexJustify = 'flex-start'; }
        else if (w.textAlignment === 2) { alignStr = 'right'; flexJustify = 'flex-end'; }
        else if (w.textAlignment === 3) { alignStr = 'justify'; flexJustify = 'center'; }

        // Alineacion vertical: 0 = arriba, 1 = centro, 2 = abajo.
        let flexAlignItems = 'center';
        if (w.verticalTextAlignment === 0) flexAlignItems = 'flex-start';
        else if (w.verticalTextAlignment === 2) flexAlignItems = 'flex-end';

        el.style.textAlign = alignStr;
        el.style.display = 'flex';
        el.style.alignItems = flexAlignItems;
        el.style.justifyContent = flexJustify;

        el.style.overflow = 'hidden';

        if (w.textPadding > 0) {
            el.style.padding = `${w.textPadding}px`;
            el.style.boxSizing = 'border-box';
        }

        // Con fuente propia, el nombre con que loadTheme la registro en su @font-face; si no, la
        // familia por su nombre. En los dos casos, sans-serif como ultimo recurso.
        el.style.fontFamily = w.useCustomFont && w.customFontPath ? `'font_${w.customFontPath.replace(/[^a-zA-Z0-9]/g, '')}', sans-serif` : (w.fontFamily || 'sans-serif');
        if (w.fontBlur > 0) el.style.filter = `blur(${w.fontBlur}px)`;

    // --- IMAGENES Y VIDEOS ---
    } else if (w.isMedia) {
        // stretchIndex: 0 = mantener proporcion, 1 = estirar, 2 = llenar con zoom.
        const fit = w.stretchIndex === 2 ? 'cover' : (w.stretchIndex === 1 ? 'fill' : 'contain');
        // Se deja apuntado en el elemento porque las imagenes que llegan con los datos en vivo
        // no se crean aqui sino en setImg, que no ve esta variable.
        el.dataset.fit = fit;
        // Capa de color por encima de la imagen, como un filtro, si el tema la usa.
        let overlayHtml = '';
        if (w.foregroundColor && w.foregroundColor !== '#00000000' && w.foregroundColor !== 'transparent') {
            overlayHtml = `<div style='position:absolute; top:0; left:0; width:100%; height:100%; background-color:${formatColor(w.foregroundColor)}; pointer-events:none; z-index:10;'></div>`;
        }

        // La imagen propia del objeto. Se guarda tambien en data-original-image porque es la de
        // repuesto del fondo del nivel (ver updateWidgetsInContainer).
        const finalImagePath = w.imagePath && w.imagePath.trim() !== '' ? w.imagePath : '';
        el.setAttribute('data-original-image', finalImagePath);

        if (w.isVideo && finalImagePath !== '') {
            // loop y muted vienen del tema; playsinline evita que algunos navegadores lo abran a
            // pantalla completa.
            const loopAttr = w.videoLoop === false ? '' : 'loop';
            const mutedAttr = w.videoMuted === false ? '' : 'muted';

            // El fotograma de muestra tapa el hueco entre que carga la pagina y arranca el video,
            // y es lo que se ve si el navegador bloquea la reproduccion automatica. Sin el, ahi se
            // veria un rectangulo negro.
            const posterAttr = w.videoPoster ? `poster='${w.videoPoster}'` : '';

            el.innerHTML = `<video src='${finalImagePath}' ${posterAttr} autoplay ${loopAttr} ${mutedAttr} playsinline style='width:100%;height:100%;object-fit:${fit};'></video>${overlayHtml}`;
        } else if (w.objectType === 'RateFace') {
            // Cara de dificultad: dos capas. El halo (featured, epic, legendary o mythic) va DETRAS
            // de la cara y la desborda: su tamano es haloScale veces el de la cara, centrado. Las
            // dos empiezan como el tema las dejo y updateWidgetsInContainer las cambia por nivel.
            const haloOn = w.haloEnabled !== false;
            const haloScale = (w.haloScale && w.haloScale > 0) ? w.haloScale : 1.5;
            const haloPct = haloScale * 100;
            const haloOffset = (haloScale - 1) * 50;
            el.dataset.haloEnabled = haloOn ? 'true' : 'false';

            let html = '';
            if (haloOn) html += `<img class='halo-layer' style='position:absolute; width:${haloPct}%; height:${haloPct}%; left:-${haloOffset}%; top:-${haloOffset}%; object-fit:contain; z-index:-1; display:none;'>`;
            if (finalImagePath !== '') html += `<img class='face-layer' src='${finalImagePath}' style='position:absolute; width:100%; height:100%; object-fit:${fit}; ${clipStyleString}'>${overlayHtml}`;
            else html += `<img class='face-layer' style='position:absolute; width:100%; height:100%; object-fit:${fit}; display:none; ${clipStyleString}'>`;
            el.innerHTML = html;
        // Cualquier otra imagen con archivo propio.
        } else if (finalImagePath !== '') {
            el.innerHTML = `<img src='${finalImagePath}' style='width:100%;height:100%;object-fit:${fit};'>${overlayHtml}`;
        }
    }
    return el;
}

// ==========================================
// CARGA DEL TEMA Y TARJETAS DE LA COLA
// ==========================================
// Punto de entrada: lo llama cada index.html con el nombre de su widget ('playing', 'queue',
// 'notification' o 'unified'). Si algo falla, el error queda en la consola del navegador.
async function loadTheme(widgetName) {
    try {
        const response = await fetch('theme.json');

        if (!response.ok) throw new Error('No theme.json');
        window.sheepTheme = await response.json();

        const canvas = document.getElementById('canvas');
        // Estilos que dependen del tema: un @font-face por cada fuente propia. (La marquesina no
        // es una animacion CSS: la mueve updateAllMarquees.)
        let dynamicStyles = '';

        // Un @font-face por cada fuente propia del tema. El nombre se forma con la ruta del
        // archivo sin simbolos, y buildWidgetHtml lo vuelve a formar igual al usarla.
        (window.sheepTheme.widgets || []).forEach(w => {
            if (w.useCustomFont && w.customFontPath) {
                const fontId = 'font_' + w.customFontPath.replace(/[^a-zA-Z0-9]/g, '');
                if (!dynamicStyles.includes(fontId)) dynamicStyles += `@font-face { font-family: '${fontId}'; src: url('${w.customFontPath}'); }\n`;
            }
        });
        const styleBlock = document.createElement('style');
        styleBlock.innerHTML = dynamicStyles; document.head.appendChild(styleBlock);

        // Un tema unificado sirve para todas las paginas: en playing, queue o notification solo
        // se dibujan los objetos de ese lienzo.
        let widgetsToRender = window.sheepTheme.widgets || [];
        if (window.sheepTheme.type === 'unified' && widgetName !== 'unified') {
            widgetsToRender = widgetsToRender.filter(w => w.canvasOrigin === widgetName);
        }

        // Los objetos de la cola y de las notificaciones se guardan como plantillas de tarjeta;
        // el resto se dibuja ya en el lienzo.
        window.queueTemplate = widgetsToRender.filter(w => w.canvasOrigin === 'queue');
        window.notificationTemplate = widgetsToRender.filter(w => w.canvasOrigin === 'notification');

        const staticWidgets = widgetsToRender.filter(w => w.canvasOrigin !== 'queue' && w.canvasOrigin !== 'notification');

        staticWidgets.forEach(w => canvas.appendChild(buildWidgetHtml(w)));

        connectWebSocket(widgetName);
    } catch (error) { console.error(error); }
    scheduleMarqueeUpdate();
}

// Dibuja la cola: una tarjeta por nivel, como mucho queueMaxItems, en fila o en columna segun
// el tema. Reutiliza las tarjetas que ya hay y solo crea o quita las que faltan o sobran.
function renderQueueCards(queueData) {
    const queueContainer = document.getElementById('queue-container') || document.getElementById('unified-container');
    if (!queueContainer || !window.queueTemplate || window.queueTemplate.length === 0 || !queueData) return;

    const box = getBoundingBox(window.queueTemplate);
    if (!box.isValid) return;
    const boxWidth = box.maxX - box.minX; const boxHeight = box.maxY - box.minY;

    let flexWrapper = queueContainer.querySelector('.queue-flex-wrapper');
    if (!flexWrapper) {
        flexWrapper = document.createElement('div');
        flexWrapper.className = 'queue-flex-wrapper';
        flexWrapper.style.position = 'absolute';
        flexWrapper.style.left = `${box.minX}px`;
        flexWrapper.style.top = `${box.minY}px`;
        queueContainer.appendChild(flexWrapper);
    }
    flexWrapper.style.display = 'flex';
    flexWrapper.style.flexDirection = window.sheepTheme.queueDirection === 1 ? 'row' : 'column';
    flexWrapper.style.gap = `${window.sheepTheme.queueSpacing || 20}px`;

    const maxItems = window.sheepTheme.queueMaxItems || 4;
    const dataToRender = queueData.slice(0, maxItems);

    // Las tarjetas que ya estan en la pagina.
    const existingCards = Array.from(flexWrapper.querySelectorAll('.queue-card'));

    // 1. Se quitan solo las que sobran (por ejemplo, si la cola baja de 4 niveles a 2).
    while (existingCards.length > dataToRender.length) {
        const card = existingCards.pop();
        card.remove();
    }

    // 2. Las que ya existen reciben los datos de su nivel; si la cola crecio, se crean las nuevas.
    dataToRender.forEach((qItem, index) => {
        let card;
        if (index < existingCards.length) {
            // Reutilizar la tarjeta en lugar de rehacerla evita que parpadee en cada actualizacion.
            card = existingCards[index];
            updateWidgetsInContainer(qItem, card);
        } else {
            // Tarjeta nueva, con los objetos relativos a ella y no al lienzo.
            card = document.createElement('div');
            card.className = 'queue-card';
            card.style.position = 'relative';
            card.style.width = `${boxWidth}px`;
            card.style.height = `${boxHeight}px`;

            window.queueTemplate.forEach(w => { card.appendChild(buildWidgetHtml(w, box.minX, box.minY)); });
            updateWidgetsInContainer(qItem, card);
            flexWrapper.appendChild(card);
        }
    });
    scheduleMarqueeUpdate();
}
"
        },

        // ==========================================
        // PÁGINAS DE CADA WIDGET
        // ==========================================
        // Las cuatro son iguales salvo el contenedor y el nombre que le pasan a loadTheme. Cada
        // una es la dirección que se pone en una fuente de navegador de OBS. Todas declaran su
        // codificación (<meta charset>): SheepGDRequest y la vista previa ya la mandan en la
        // cabecera de la respuesta, pero así la página no depende de quién la sirva.
        //   - #canvas: los objetos fijos del lienzo.
        //   - #<widget>-container: las tarjetas de la cola o de las notificaciones.

        // Widget del nivel en juego.
        { "playing/index.html",
            // language=html
@"<!DOCTYPE html>

<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <link rel='stylesheet' href='engine.css'>
    <style>
        #playing-container { /* Tus estilos específicos para playing */ }
    </style>
</head>
<body>
    <div id='canvas'></div>
    <div id='playing-container'></div>
    <script src='engine.js'></script>
    <script>
        window.onload = () => { loadTheme('playing'); };
    </script>
</body>
</html>"
        },

        // Widget de la cola.
        { "queue/index.html",
            // language=html
        @"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <link rel='stylesheet' href='engine.css'>
    <style>
        #queue-container { /* Tus estilos específicos para queue */ }
    </style>
</head>
<body>
    <div id='canvas'></div>
    <div id='queue-container'></div>
    <script src='engine.js'></script>
    <script>
        window.onload = () => { loadTheme('queue'); };
    </script>
</body>
</html>"
        },

        // Widget de las notificaciones.
        { "notification/index.html",
            // language=html
        @"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <link rel='stylesheet' href='engine.css'>
    <style>
        #notification-container { /* Tus estilos específicos para notification */ }
    </style>
</head>
<body>
    <div id='canvas'></div>
    <div id='notification-container'></div>
    <script src='engine.js'></script>
    <script>
        window.onload = () => { loadTheme('notification'); };
    </script>
</body>
</html>"
        },

        // Widget unificado: un solo tema con los tres. Su #unified-container recibe a la vez las
        // tarjetas de la cola y las de las notificaciones.
        { "index.html",
            // language=html
            @"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <title>SheepGDRequest - Unified Widget</title>
    <link rel='stylesheet' href='engine.css'>
    <style>
        /* Eliminamos los márgenes por defecto del navegador para OBS */
        body { margin: 0; padding: 0; overflow: hidden; background-color: transparent; }
        #unified-container { /* Tus estilos específicos para el widget unificado */ }
    </style>
</head>
<body>
    <!-- El lienzo principal -->
    <div id='canvas'></div>

    <!-- El contenedor Unificado para Fila y Notificaciones -->
    <div id='unified-container'></div>

    <script src='engine.js'></script>
    <script>
        window.onload = () => { loadTheme('unified'); };
    </script>
</body>
</html>"
        },

        // Provisional: theme.json no se edita a mano. MainWindowViewModel.GenerateThemeJson lo
        // vuelve a escribir desde el lienzo cada vez que se abre en el editor y al guardar.
        { "theme.json",
            // language=json
            "{\n    \"name\": \"Mi Tema\",\n    \"type\": \"unified\"\n}" }
    };
}
