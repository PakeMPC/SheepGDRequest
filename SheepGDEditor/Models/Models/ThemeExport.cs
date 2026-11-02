using System.Collections.Generic;
using System.Text.Json.Serialization;
using SheepGDEditor.Services;

namespace SheepGDEditor.Models;

// =====================================================================================
// FORMATO DEL ARCHIVO theme.json
// -------------------------------------------------------------------------------------
// Cada tema es un .zip que lleva dentro un theme.json con la forma que describen estas
// dos clases:
//
//   - ThemeExportData:  la raíz del archivo. Datos del tema completo: nombre, tipo,
//                       tamaño y los ajustes de la cola y de las notificaciones.
//   - WidgetExportData: cada uno de los objetos que se dibujan en el widget.
//
// Quién lo usa:
//   - El Editor lo escribe al guardar (MainWindowViewModel.GenerateThemeJson) y lo lee al
//     abrir un tema (MainWindowViewModel.LoadTheme).
//   - SheepGDRequest lee "name", "author" y "type" para listar los temas en sus menús.
//   - engine.js, el script que viaja dentro de cada tema, lo lee entero para dibujar el
//     widget en OBS.
//
// Antes de modificarlo:
//   - Los nombres de [JsonPropertyName] son el contrato con los temas ya guardados y con
//     engine.js. Cambiar uno hace que los temas existentes pierdan ese dato sin ningún aviso.
//   - Al leer un tema, el campo que no venga en el JSON se queda con el valor inicial de la
//     propiedad. Por eso esos valores son, a la vez, lo que recibe un tema guardado antes de
//     que el campo existiera.
//   - Las propiedades salen en el JSON en el mismo orden en que están declaradas aquí.
//
// Convenciones de los valores:
//   - Medidas en píxeles del lienzo, salvo que se indique otra cosa.
//   - Colores como texto hexadecimal: "#RRGGBB", o "#AARRGGBB" con el alfa DELANTE, que es
//     como los escribe Avalonia. engine.js los pasa al formato de CSS al dibujarlos.
//   - Rutas de archivo relativas a la raíz del tema, por ejemplo "assets/fondo.png".
// =====================================================================================

/// <summary>
/// La raíz del theme.json: los datos del tema completo.
/// </summary>
public class ThemeExportData
{
    /// <summary>
    /// Nombre del tema. Es el que aparece en los menús de SheepGDRequest.
    /// </summary>
    [JsonPropertyName("name")]
    public string ThemeName { get; set; } = EditorI18n.Tr("Default_ThemeName");

    /// <summary>
    /// Widget para el que está hecho el tema: "playing" (nivel en juego), "queue" (cola),
    /// "notification" (notificaciones) o "unified" (varios widgets en un solo tema).
    /// Siempre en minúsculas: engine.js compara el texto exacto.
    /// SheepGDRequest ofrece los temas "unified" en todos sus menús de tema, y los demás solo
    /// en el de su widget.
    /// </summary>
    [JsonPropertyName("type")]
    public string WidgetType { get; set; } = "playing";

    /// <summary>
    /// Versión del formato del theme.json. Hoy ningún programa la lee; se guarda para poder
    /// distinguir los temas antiguos si algún día el formato cambia de forma incompatible.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Ancho del widget, en píxeles. En un tema "unified" es el tamaño de lienzo que eligió el
    /// autor. En un tema de un solo tipo lo calcula el Editor al guardar: llega hasta el borde
    /// derecho del objeto más lejano, contando las tarjetas de ejemplo de la cola y de las
    /// notificaciones. Por la izquierda nunca se recorta: el widget empieza siempre en la x = 0.
    /// El Editor lo vuelve a leer al abrir el tema; engine.js no lo usa.
    /// </summary>
    [JsonPropertyName("canvasWidth")]
    public double CanvasWidth { get; set; } = 1920;

    /// <summary>
    /// Alto del widget, en píxeles. Se calcula igual que <see cref="CanvasWidth"/>, con el
    /// borde inferior en lugar del derecho; por arriba tampoco se recorta.
    /// </summary>
    [JsonPropertyName("canvasHeight")]
    public double CanvasHeight { get; set; } = 1080;

    /// <summary>
    /// Todos los objetos del tema, de todos los lienzos en una sola lista. Cada uno indica en
    /// <see cref="WidgetExportData.CanvasOrigin"/> a qué lienzo pertenece.
    /// </summary>
    [JsonPropertyName("widgets")]
    public List<WidgetExportData> Objects { get; set; } = new();

    // Ajustes de la cola. Los objetos del lienzo "queue" forman la plantilla de UNA tarjeta,
    // que el widget repite una vez por cada nivel de la cola.

    /// <summary>Cola: cuántos niveles se muestran a la vez como máximo.</summary>
    [JsonPropertyName("queueMaxItems")] public int QueueMaxItems { get; set; } = 4;

    /// <summary>
    /// Cola: cómo se colocan las tarjetas.
    /// 0 = en columna (vertical), 1 = en fila (horizontal).
    /// </summary>
    [JsonPropertyName("queueDirection")] public int QueueDirection { get; set; } = 0;

    /// <summary>Cola: separación entre una tarjeta y la siguiente, en píxeles.</summary>
    [JsonPropertyName("queueSpacing")] public double QueueSpacing { get; set; } = 20;

    /// <summary>
    /// Autor del tema. SheepGDRequest lo muestra junto al nombre en sus menús; si está vacío,
    /// muestra solo el nombre.
    /// </summary>
    [JsonPropertyName("author")] public string Author { get; set; } = EditorI18n.Tr("Default_ThemeAuthor");

    // Ajustes de las notificaciones. Igual que en la cola, los objetos del lienzo
    // "notification" forman la plantilla de una tarjeta, que se repite por cada notificación.

    /// <summary>
    /// Notificaciones: cuántas se muestran a la vez como máximo. Las que lleguen de más esperan
    /// a que se libere un hueco.
    /// </summary>
    [JsonPropertyName("notificationMaxItems")] public int NotificationMaxItems { get; set; } = 3;

    /// <summary>
    /// Notificaciones: cómo se colocan las tarjetas.
    /// 0 = en columna (vertical), 1 = en fila (horizontal).
    /// </summary>
    [JsonPropertyName("notificationDirection")] public int NotificationDirection { get; set; } = 0;

    /// <summary>Notificaciones: separación entre una tarjeta y la siguiente, en píxeles.</summary>
    [JsonPropertyName("notificationSpacing")] public double NotificationSpacing { get; set; } = 20;

    /// <summary>
    /// Notificaciones: cómo corre el tiempo de las que están en pantalla.
    /// 0 = simultáneo: todas descuentan su tiempo a la vez.
    /// 1 = en cascada: solo descuenta la más antigua; las demás esperan a que desaparezca.
    /// </summary>
    [JsonPropertyName("notificationTimerMode")] public int NotificationTimerMode { get; set; } = 1;
}

/// <summary>
/// Un objeto del widget: un texto, una imagen, un vídeo o uno de los datos del nivel.
/// Muchos campos solo tienen sentido para algunos tipos de objeto, y los demás los ignoran.
/// Cuando un campo no se aplica a todos, su comentario empieza diciendo a cuáles.
/// </summary>
public class WidgetExportData
{
    /// <summary>
    /// Tipo de objeto, escrito igual que en el enum WidgetObjectType ("Text", "Image",
    /// "LevelName", "RateFace"...). engine.js lo usa para saber qué dato en vivo poner en cada
    /// objeto. Al abrir el tema, el Editor descarta los objetos de un tipo que no conoce.
    /// </summary>
    [JsonPropertyName("objectType")] public string Type { get; set; } = "";

    /// <summary>
    /// En los objetos de texto, el texto que se muestra. En los que muestran un dato del nivel,
    /// es lo que se ve mientras ese dato no llega. En los demás objetos es solo el nombre con el
    /// que aparecen en el Editor.
    /// </summary>
    [JsonPropertyName("objectName")] public string Name { get; set; } = "";

    /// <summary>
    /// Posición horizontal de la esquina superior izquierda, sin contar la rotación.
    /// </summary>
    [JsonPropertyName("x")] public double X { get; set; }

    /// <summary>
    /// Posición vertical de la esquina superior izquierda, sin contar la rotación.
    /// </summary>
    [JsonPropertyName("y")] public double Y { get; set; }

    /// <summary>Ancho de la caja del objeto, antes de aplicar el recorte.</summary>
    [JsonPropertyName("width")] public double Width { get; set; }

    /// <summary>Alto de la caja del objeto, antes de aplicar el recorte.</summary>
    [JsonPropertyName("height")] public double Height { get; set; }

    /// <summary>Orden de dibujo: el objeto con el número más alto queda por encima.</summary>
    [JsonPropertyName("layerIndex")] public int Layer { get; set; }

    /// <summary>Textos: color de la letra.</summary>
    [JsonPropertyName("textColor")] public string ColorHex { get; set; } = "";

    /// <summary>Textos: tamaño de la letra, en píxeles.</summary>
    [JsonPropertyName("fontSize")] public double FontSize { get; set; }

    /// <summary>
    /// OPACIDAD del objeto, de 0 (invisible) a 1 (totalmente visible). Pese al nombre del
    /// campo, 1 significa opaco.
    /// </summary>
    [JsonPropertyName("transparency")] public double Transparency { get; set; }

    /// <summary>
    /// Imágenes y vídeos: ruta, dentro del tema, del archivo del objeto (en un vídeo, el propio
    /// vídeo). Vacía si no lleva archivo. En los objetos que cambian de imagen según el nivel,
    /// como la cara de dificultad o el fondo del nivel, es la que se ve mientras no llega otra.
    /// </summary>
    [JsonPropertyName("imagePath")] public string ImagePath { get; set; } = "";

    /// <summary>
    /// Lienzo al que pertenece el objeto: "playing", "queue", "notification" o "unified".
    /// Los objetos de "queue" y "notification" no se dibujan sueltos: forman la plantilla de la
    /// tarjeta que se repite por cada nivel de la cola o cada notificación.
    /// </summary>
    [JsonPropertyName("canvasOrigin")] public string CanvasOrigin { get; set; } = "playing";

    /// <summary>
    /// Textos: familia de letra, por su nombre. Al guardar, el Editor incrusta esa fuente en el
    /// tema siempre que puede, y entonces el objeto pasa a usar <see cref="CustomFontPath"/>.
    /// Este nombre solo decide cuando no se pudo: lo resuelve el navegador de OBS, así que la
    /// fuente tiene que estar instalada en el equipo donde corre OBS.
    /// </summary>
    [JsonPropertyName("fontFamily")] public string FontFamily { get; set; } = "Inter";

    /// <summary>
    /// Textos: ruta, dentro del tema, del archivo de fuente (por ejemplo "assets/fuente.ttf").
    /// Solo se usa si <see cref="UseCustomFont"/> es true.
    /// </summary>
    [JsonPropertyName("customFontPath")] public string CustomFontPath { get; set; } = "";

    /// <summary>
    /// Textos: true si usa la fuente de <see cref="CustomFontPath"/>, que viaja dentro del
    /// tema; false si usa <see cref="FontFamily"/>.
    /// </summary>
    [JsonPropertyName("useCustomFont")] public bool UseCustomFont { get; set; }

    /// <summary>Textos: desenfoque de la letra, en píxeles. 0 = nítido.</summary>
    [JsonPropertyName("fontBlur")] public double FontBlur { get; set; }

    /// <summary>
    /// Fondo de la caja: 0 = color sólido (el de <see cref="BackgroundColorHex"/>),
    /// 1 = transparente.
    /// </summary>
    [JsonPropertyName("backgroundType")] public int BackgroundType { get; set; }

    /// <summary>
    /// Imágenes y vídeos: cómo se ajustan a su caja.
    /// 0 = mantener la proporción (se ve entera y puede sobrar espacio),
    /// 1 = estirar hasta llenar la caja (puede deformarse),
    /// 2 = llenar con zoom (mantiene la proporción y recorta lo que sobra).
    /// </summary>
    [JsonPropertyName("stretchIndex")] public int StretchIndex { get; set; }

    /// <summary>Giro del objeto, en grados, en sentido horario y alrededor de su centro.</summary>
    [JsonPropertyName("rotationAngle")] public double RotationAngle { get; set; }

    // Recorte: cuántos píxeles se ocultan por cada lado de la caja. El objeto conserva su
    // tamaño y su posición; solo deja de verse esa franja.

    /// <summary>Píxeles que se ocultan por el borde superior.</summary>
    [JsonPropertyName("cropTop")] public double CropTop { get; set; }

    /// <summary>Píxeles que se ocultan por el borde inferior.</summary>
    [JsonPropertyName("cropBottom")] public double CropBottom { get; set; }

    /// <summary>Píxeles que se ocultan por el borde izquierdo.</summary>
    [JsonPropertyName("cropLeft")] public double CropLeft { get; set; }

    /// <summary>Píxeles que se ocultan por el borde derecho.</summary>
    [JsonPropertyName("cropRight")] public double CropRight { get; set; }

    /// <summary>
    /// Grupo de objetos vinculados. Solo lo usa el Editor: al seleccionar un objeto se
    /// seleccionan todos los de su grupo, y así se mueven juntos. Vacío = sin grupo.
    /// engine.js no lo usa.
    /// </summary>
    [JsonPropertyName("linkedGroupId")] public string LinkedGroupId { get; set; } = "";

    /// <summary>
    /// Radio de las esquinas redondeadas, en píxeles. 0 = esquinas rectas. Al dibujarlo se
    /// limita a la mitad del lado visible más corto.
    /// </summary>
    [JsonPropertyName("borderRoundness")] public double BorderRoundness { get; set; }

    /// <summary>
    /// Color del fondo cuando <see cref="BackgroundType"/> es 0. El Editor lo deja vacío si el
    /// fondo es transparente.
    /// </summary>
    [JsonPropertyName("backgroundColor")] public string BackgroundColorHex { get; set; } = "";

    // Categoría del objeto. El Editor la calcula a partir del tipo y la guarda para que
    // engine.js sepa cómo dibujar cada objeto sin tener que conocer todos los tipos.

    /// <summary>El objeto es un texto (fijo o con un dato del nivel).</summary>
    [JsonPropertyName("isText")] public bool IsText { get; set; }

    /// <summary>El objeto es una imagen o un vídeo.</summary>
    [JsonPropertyName("isMedia")] public bool IsMedia { get; set; }

    /// <summary>El objeto es un vídeo. Siempre va junto con <see cref="IsMedia"/>.</summary>
    [JsonPropertyName("isVideo")] public bool IsVideo { get; set; }

    /// <summary>
    /// Textos: true si el texto se parte en varias líneas cuando no cabe a lo ancho. Con false
    /// va en una sola línea y, si no cabe, se desplaza de lado a lado (marquesina).
    /// </summary>
    [JsonPropertyName("isTextWrapped")] public bool IsTextWrapped { get; set; } = true;

    /// <summary>
    /// Cuadro de notificación: color del nombre del espectador dentro del mensaje.
    /// </summary>
    [JsonPropertyName("requesterColor")] public string RequesterColorHex { get; set; } = "#FF5555";

    /// <summary>
    /// Cuadro de notificación: color del nombre y el ID del nivel dentro del mensaje.
    /// </summary>
    [JsonPropertyName("levelDataColor")] public string LevelDataColorHex { get; set; } = "#55FF55";

    /// <summary>
    /// Textos: alineación horizontal. 0 = izquierda, 1 = centro, 2 = derecha, 3 = justificado.
    /// </summary>
    [JsonPropertyName("textAlignment")] public int TextAlignment { get; set; } = 1;

    /// <summary>
    /// Imágenes y vídeos: color que se pinta encima, como un filtro. El valor por
    /// defecto, "#00000000", es totalmente transparente: sin filtro.
    /// </summary>
    [JsonPropertyName("foregroundColor")] public string ForegroundColorHex { get; set; } = "#00000000";

    /// <summary>
    /// Objetos del lienzo de notificaciones: segundos que se ve este objeto en cada
    /// notificación. La tarjeta entera desaparece cuando ha pasado el tiempo de su objeto que
    /// más dura.
    /// </summary>
    [JsonPropertyName("notificationDuration")] public double NotificationDuration { get; set; } = 6.0;

    /// <summary>
    /// Textos: alineación vertical dentro de la caja. 0 = arriba, 1 = centro, 2 = abajo.
    /// </summary>
    [JsonPropertyName("verticalTextAlignment")] public int VerticalTextAlignment { get; set; } = 1;

    /// <summary>Textos: margen interior entre el borde de la caja y el texto, en píxeles.</summary>
    [JsonPropertyName("textPadding")] public double TextPadding { get; set; } = 0.0;

    // --- HALO DE LA CARA DE DIFICULTAD (solo RateFace) -------------------------------
    // Qué halo se dibuja (featured, epic, legendary o mythic) lo decide el nivel que se esté
    // jugando. El tema solo dice si quiere mostrarlo y de qué tamaño.

    /// <summary>Si se dibuja el halo detrás de la cara cuando el nivel lo tiene.</summary>
    [JsonPropertyName("haloEnabled")] public bool HaloEnabled { get; set; } = true;

    /// <summary>Tamaño del halo respecto a la cara: 1.5 = un 150 % de la cara.</summary>
    [JsonPropertyName("haloScale")] public double HaloScale { get; set; } = 1.5;

    // --- VÍDEO -----------------------------------------------------------------------

    /// <summary>Si el vídeo vuelve a empezar al terminar.</summary>
    [JsonPropertyName("videoLoop")] public bool VideoLoop { get; set; } = true;

    /// <summary>Si el vídeo se reproduce sin sonido.</summary>
    [JsonPropertyName("videoMuted")] public bool VideoMuted { get; set; } = true;

    /// <summary>
    /// Ruta, dentro del tema, del fotograma de muestra del vídeo. En el Editor es lo que se ve
    /// en el lienzo. En OBS se muestra mientras el vídeo carga, y también si el navegador no
    /// deja que arranque solo: sin él, ahí se vería un rectángulo negro. Vacía = sin fotograma.
    /// </summary>
    [JsonPropertyName("videoPoster")] public string VideoPoster { get; set; } = "";

    // --- CONTORNO DEL TEXTO ----------------------------------------------------------
    // Un trazo alrededor de cada letra. Se dibuja por detrás de la letra, así que no la
    // adelgaza.

    /// <summary>Si el texto lleva contorno.</summary>
    [JsonPropertyName("hasTextOutline")] public bool HasTextOutline { get; set; } = false;

    /// <summary>Grosor del contorno, en píxeles.</summary>
    [JsonPropertyName("textOutlineSize")] public double TextOutlineSize { get; set; } = 2;

    /// <summary>Color del contorno.</summary>
    [JsonPropertyName("textOutlineColor")] public string TextOutlineColor { get; set; } = "#000000";
}
