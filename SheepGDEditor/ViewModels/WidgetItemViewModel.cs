using System;
using System.Collections.Generic;
using SkiaSharp;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Fonts;
using CommunityToolkit.Mvvm.ComponentModel;
using SheepGDEditor.Services;
namespace SheepGDEditor.ViewModels;

/// <summary>
/// Los tipos de objeto que se pueden poner en un lienzo. El nombre de cada uno es el que se guarda
/// en theme.json (objectType), y engine.js lo usa para saber qué dato en vivo poner en el objeto.
/// OJO: renombrar uno rompe los temas ya guardados, porque al abrirlos el Editor descarta los
/// objetos de un tipo que no conoce.
/// </summary>
public enum WidgetObjectType
{
    // --- Básicos: su contenido lo pone el diseñador ---
    Text,               // texto fijo, escrito a mano
    Image,              // imagen elegida por el diseñador
    Video,              // vídeo elegido por el diseñador

    // --- Datos del nivel: los pone el nivel en juego, o el de cada tarjeta de la cola ---
    LevelBackground,    // miniatura del nivel; su imagen propia es la de repuesto
    LevelName,          // nombre del nivel
    CreatorName,        // creador del nivel
    LevelId,            // ID del nivel

    // --- Datos de la petición ---
    SentBy,             // quién pidió el nivel
    PlatformIcon,       // icono de la plataforma desde la que se pidió
    PlatformName,       // nombre de esa plataforma (Twitch, YouTube...)
    RequestStatus,      // si las peticiones están abiertas o cerradas
    Timer,              // cuenta atrás del auto-skip, cuando está activo

    // --- Dificultad y estadísticas del nivel ---
    RateFace,           // cara de dificultad (imagen); puede llevar halo
    RateText,           // nombre de la dificultad (Easy, Insane Demon...)
    Stars,              // estrellas (o lunas) que da
    Downloads,          // descargas
    LikeIcon,           // icono de like o de dislike
    StarIcon,           // estrella o luna: los platformers dan lunas desde la 2.2
    LikesAndDislikes,   // likes
    Orbs,               // orbes que da
    Duration,           // duración (Tiny, Long, XL... o Plat en los platformers)

    // --- Listas externas de demons ---
    AredlPos,           // posición en la AREDL
    GddlRate,           // tier en la GDDL

    // --- Notificaciones ---
    NotificationBox     // el texto de la notificación, con el usuario y el nivel en color
}

/// <summary>
/// Un objeto del lienzo: un texto, una imagen, un vídeo o uno de los que muestran un dato en vivo
/// (ver WidgetObjectType). Guarda todo lo que el tema sabe de él, y además lo que el lienzo
/// necesita para dibujarlo: pinceles, márgenes, la geometría del contorno...
///
/// Lo que se guarda en el tema es lo que va a theme.json (ver ThemeExport.cs). El resto es del
/// Editor: la vista previa (las propiedades Preview*), la selección y los valores calculados
/// para las vistas.
///
/// OJO: una propiedad nueva que se guarde en el tema tiene que pasar también por CloneWidget (si
/// no, el historial la borra al deshacer), GenerateThemeJson, LoadTheme y
/// MainWindowViewModel.PropiedadesQueSeGuardan (si no, cambiarla no marca el tema como
/// modificado). La lista de comprobación completa está en el CLAUDE.md.
/// </summary>
public partial class WidgetItemViewModel : ObservableObject
{
    // ==========================================
    // TIPO Y NOMBRE
    // ==========================================

    /// <summary>
    /// Qué es el objeto. Casi todo lo que enseñan el lienzo y el panel depende de esto, y por eso
    /// avisa a tantas propiedades calculadas. Se fija al crear el objeto y ya no cambia.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText))]
    [NotifyPropertyChangedFor(nameof(IsImage))]
    [NotifyPropertyChangedFor(nameof(IsVideo))]
    [NotifyPropertyChangedFor(nameof(IsMedia))]
    [NotifyPropertyChangedFor(nameof(IsNotificationBox))]
    [NotifyPropertyChangedFor(nameof(IsRateFace))]
    [NotifyPropertyChangedFor(nameof(DisplayHaloImage))]
    [NotifyPropertyChangedFor(nameof(HasHalo))]
    [NotifyPropertyChangedFor(nameof(ShowMediaPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowImageLayer))]
    [NotifyPropertyChangedFor(nameof(HasVideoSource))]
    [NotifyPropertyChangedFor(nameof(MediaPlaceholderName))]
    [NotifyPropertyChangedFor(nameof(CanEditTextContent))]
    [NotifyPropertyChangedFor(nameof(CanChangeMediaSource))]
    [NotifyPropertyChangedFor(nameof(MediaSourceLabel))]
    [NotifyPropertyChangedFor(nameof(IsLevelBackground))]
    [NotifyPropertyChangedFor(nameof(CanClearDefaultImage))]
    [NotifyPropertyChangedFor(nameof(ShowTextOutline))]
    private WidgetObjectType _objectType = WidgetObjectType.Text;

    /// <summary>
    /// En el texto fijo (Text), el texto que se ve. En los que muestran un dato, lo que se ve
    /// mientras el dato no llega, y en el lienzo con la vista previa apagada. En los demás, solo
    /// el nombre con el que aparece en el Editor.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(ColoredTextSegments))]
    private string _objectName = EditorI18n.Tr("Default_ObjectName");

    // ==========================================
    // VISTA PREVIA DEL LIENZO
    // ==========================================
    // Datos de demostración que MainWindowViewModel va rotando para que el lienzo se parezca al
    // directo. Son solo del Editor: nada de esto se guarda en el tema.

    /// <summary>
    /// El interruptor "Vista previa" del panel. Apagado, el objeto deja de recibir datos de
    /// demostración y enseña su texto o su imagen de diseño.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(DisplayImage))]
    [NotifyPropertyChangedFor(nameof(ShowMediaPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowImageLayer))]
    [NotifyPropertyChangedFor(nameof(DisplayHaloImage))]
    [NotifyPropertyChangedFor(nameof(HasHalo))]
    [NotifyPropertyChangedFor(nameof(ColoredTextSegments))]
    private bool _isPreviewEnabled = true;

    /// <summary>
    /// El texto de demostración que se enseña en lugar de ObjectName: un dato del nivel, o el
    /// mensaje de una notificación. Vacío = ninguno.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(ColoredTextSegments))]
    private string _previewText = "";

    /// <summary>
    /// Cuadro de notificación: el usuario que aparece dentro del mensaje de demostración, para
    /// pintarlo con su color (ver ColoredTextSegments).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColoredTextSegments))]
    private string _previewUser = "";

    /// <summary>
    /// Cuadro de notificación: el nivel que aparece dentro del mensaje de demostración. Ver
    /// PreviewUser.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColoredTextSegments))]
    private string _previewLevel = "";

    /// <summary>
    /// La imagen de demostración (cara de dificultad, icono de plataforma, like, estrella...) que
    /// se enseña en lugar de la propia. Null = ninguna.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage))]
    [NotifyPropertyChangedFor(nameof(ShowMediaPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowImageLayer))]
    private Bitmap? _previewImageBitmap;

    /// <summary>
    /// El texto que dibuja el lienzo: el de demostración si la vista previa está encendida y hay
    /// uno; si no, ObjectName.
    /// </summary>
    public string DisplayText => IsPreviewEnabled && !string.IsNullOrEmpty(PreviewText) ? PreviewText : ObjectName;

    /// <summary>
    /// La imagen que dibuja el lienzo. En un vídeo, su fotograma de muestra, porque Avalonia no
    /// decodifica vídeo. En los demás, la de demostración si la vista previa está encendida y hay
    /// una, y si no, la propia. Null = no hay imagen, y se dibuja la tarjeta de marcador
    /// (ShowMediaPlaceholder).
    /// </summary>
    public Bitmap? DisplayImage => IsVideo
        ? VideoPosterBitmap
        : (IsPreviewEnabled && PreviewImageBitmap != null ? PreviewImageBitmap : ImageBitmap);

    // ==========================================
    // POSICIÓN, TAMAÑO, CAPA Y TRANSFORMACIÓN
    // ==========================================

    /// <summary>Posición horizontal de la esquina superior izquierda, sin contar el giro.</summary>
    [ObservableProperty] private double _x = 100;
    /// <summary>Posición vertical de la esquina superior izquierda, sin contar el giro.</summary>
    [ObservableProperty] private double _y = 100;
    /// <summary>Ancho de la caja del objeto, antes de aplicar el recorte.</summary>
    [ObservableProperty] private double _width = 250;
    /// <summary>Alto de la caja del objeto, antes de aplicar el recorte.</summary>
    [ObservableProperty] private double _height = 60;
    /// <summary>Capa: el objeto con el número más alto se dibuja por encima.</summary>
    [ObservableProperty] private int _layerIndex = 1;

    /// <summary>Textos: desenfoque de la letra, en píxeles. 0 = nítido.</summary>
    [ObservableProperty]
    private double _fontBlur = 0;

    /// <summary>
    /// Grupo de vínculo (Ctrl+L): los objetos con el mismo identificador se seleccionan y se
    /// mueven juntos. Vacío = sin vincular.
    /// </summary>
    [ObservableProperty]
    private string _linkedGroupId = string.Empty;

    /// <summary>
    /// Qué tiradores enseña el objeto seleccionado: false = los de tamaño y recorte, true = los de
    /// giro. Se alterna haciendo clic, sin arrastrar, en un objeto que ya estaba seleccionado.
    /// Solo del Editor.
    /// </summary>
    [ObservableProperty] private bool _isRotationMode = false;
    /// <summary>Giro en grados, alrededor del centro de la caja.</summary>
    [ObservableProperty] private double _rotationAngle = 0;

    /// <summary>
    /// Recorte desde el borde superior hacia dentro, en píxeles. La caja no cambia de tamaño:
    /// lo recortado deja de verse.
    /// </summary>
    [ObservableProperty] private double _cropTop = 0;
    /// <summary>Recorte desde el borde inferior. Ver CropTop.</summary>
    [ObservableProperty] private double _cropBottom = 0;
    /// <summary>Recorte desde el borde izquierdo. Ver CropTop.</summary>
    [ObservableProperty] private double _cropLeft = 0;
    /// <summary>Recorte desde el borde derecho. Ver CropTop.</summary>
    [ObservableProperty] private double _cropRight = 0;

    // ==========================================
    // AJUSTES DEL TEXTO Y DE LA NOTIFICACIÓN
    // ==========================================

    /// <summary>
    /// Textos: true = el texto se parte en varias líneas cuando no cabe a lo ancho. Con false va
    /// en una sola línea y, si no cabe, se desplaza de lado a lado (marquesina).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvaloniaTextWrapping))]
    private bool _isTextWrapped = true;

    /// <summary>
    /// Cuadro de notificación: color del usuario dentro del mensaje, como TextColorHex. El panel
    /// lo cambia a través de RequesterColor.
    ///
    /// Los avisos van aquí, en el Hex, y no en el setter de RequesterColor: la copia a los demás
    /// objetos de una selección múltiple (MainWindowViewModel.OnTrackedWidgetPropertyChanged) solo
    /// ve el cambio del Hex, y cuando los avisos estaban en el setter, el principal cambiaba de
    /// color en el lienzo y los demás no.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequesterColor))]
    [NotifyPropertyChangedFor(nameof(DisplayRequesterColor))]
    [NotifyPropertyChangedFor(nameof(ColoredTextSegments))]
    private string _requesterColorHex = "#FF5555";

    /// <summary>
    /// Cuadro de notificación: color del nombre y del ID del nivel dentro del mensaje. Lo cambia
    /// LevelDataColor, y avisa igual que RequesterColorHex.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelDataColor))]
    [NotifyPropertyChangedFor(nameof(DisplayLevelDataColor))]
    [NotifyPropertyChangedFor(nameof(ColoredTextSegments))]
    private string _levelDataColorHex = "#55FF55";

    /// <summary>
    /// Textos: alineación horizontal. 0 = izquierda, 1 = centro, 2 = derecha, 3 = justificado.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvaloniaTextAlignment))]
    private int _textAlignmentIndex = 1;

    /// <summary>VerticalTextAlignmentIndex traducido a lo que entiende Avalonia.</summary>
    public Avalonia.Layout.VerticalAlignment AvaloniaVerticalAlignment => VerticalTextAlignmentIndex switch
    {
        0 => Avalonia.Layout.VerticalAlignment.Top,
        2 => Avalonia.Layout.VerticalAlignment.Bottom,
        _ => Avalonia.Layout.VerticalAlignment.Center
    };

    /// <summary>
    /// Objetos del lienzo de notificaciones: segundos que se ve este objeto en cada notificación.
    /// La tarjeta entera desaparece cuando ha pasado el tiempo de su objeto que más dura.
    /// </summary>
    [ObservableProperty] private double _notificationDuration = 6.0;

    /// <summary>
    /// Cuánto se ha desplazado ya el texto de la marquesina en el lienzo, en píxeles. Lo mueve
    /// UpdateMarqueeAnimations. Solo del Editor.
    /// </summary>
    [ObservableProperty]
    private double _marqueeOffset = 0;

    /// <summary>
    /// Textos: alineación vertical dentro de la caja. 0 = arriba, 1 = centro, 2 = abajo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvaloniaVerticalAlignment))]
    private int _verticalTextAlignmentIndex = 1;

    /// <summary>
    /// Textos: margen interior entre el borde de la caja y el texto, en píxeles, igual por los
    /// cuatro lados.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvaloniaTextPadding))]
    private double _textPadding = 0.0;

    /// <summary>TextPadding como Thickness, para el lienzo.</summary>
    public Avalonia.Thickness AvaloniaTextPadding => new Avalonia.Thickness(TextPadding);

    /// <summary>TextAlignmentIndex traducido a lo que entiende Avalonia.</summary>
    public Avalonia.Media.TextAlignment AvaloniaTextAlignment => TextAlignmentIndex switch
    {
        0 => Avalonia.Media.TextAlignment.Left,
        2 => Avalonia.Media.TextAlignment.Right,
        3 => Avalonia.Media.TextAlignment.Justify,
        _ => Avalonia.Media.TextAlignment.Center
    };

    /// <summary>IsTextWrapped traducido a lo que entiende Avalonia.</summary>
    public Avalonia.Media.TextWrapping AvaloniaTextWrapping => IsTextWrapped ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;

    // ==========================================
    // FILTRO DE COLOR Y TROZOS DE COLOR DEL TEXTO
    // ==========================================

    /// <summary>
    /// Imágenes y vídeos: color que se pinta encima, a modo de filtro (en el formato de
    /// TextColorHex). El de por defecto, "#00000000", es totalmente transparente: sin filtro.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForegroundColor))]
    [NotifyPropertyChangedFor(nameof(DisplayForegroundColor))]
    private string _foregroundColorHex = "#00000000";

    /// <summary>ForegroundColorHex como Color, para el selector de color del panel.</summary>
    public Color ForegroundColor
    {
        get => Color.TryParse(ForegroundColorHex, out var color) ? color : Colors.Transparent;
        set
        {
            ForegroundColorHex = value.ToString();
            OnPropertyChanged(nameof(DisplayForegroundColor));
        }
    }

    /// <summary>
    /// El texto del lienzo partido en trozos de color, que TextBlockHelper convierte en Runs.
    /// Todos los textos del lienzo se dibujan así. En un cuadro de notificación con datos de
    /// demostración, el usuario y el nivel van con sus colores y el resto con el del texto; en
    /// cualquier otro caso es un solo trozo.
    ///
    /// Dónde va cada nombre lo decide TramosDeNombres, con el mismo criterio que
    /// injectNotificationData de engine.js, para que el lienzo y OBS coincidan.
    /// </summary>
    public IEnumerable<ColoredTextSegment> ColoredTextSegments
    {
        get
        {
            var list = new List<ColoredTextSegment>();
            string text = DisplayText;

            // Si no es un cuadro de notificación, o no hay datos de demostración: un solo trozo.
            if (!IsNotificationBox || string.IsNullOrEmpty(text) || string.IsNullOrEmpty(PreviewUser))
            {
                list.Add(new ColoredTextSegment(text ?? "", DisplayTextColor));
                return list;
            }

            var tramos = TramosDeNombres(text, (PreviewUser, DisplayRequesterColor), (PreviewLevel, DisplayLevelDataColor));
            int currentIndex = 0;

            foreach (var (inicio, fin, color) in tramos)
            {
                // El texto normal que hay antes, con el color del texto...
                if (inicio > currentIndex)
                    list.Add(new ColoredTextSegment(text.Substring(currentIndex, inicio - currentIndex), DisplayTextColor));

                // ...y el nombre, con el suyo.
                list.Add(new ColoredTextSegment(text.Substring(inicio, fin - inicio), color));
                currentIndex = fin;
            }

            // Lo que queda detrás del último nombre, con el color del texto.
            if (currentIndex < text.Length)
                list.Add(new ColoredTextSegment(text.Substring(currentIndex), DisplayTextColor));

            return list;
        }
    }

    /// <summary>
    /// Dónde va cada nombre dentro del mensaje de una notificación: la PRIMERA vez que sale cada
    /// uno, buscando antes el más largo, y sin que se pisen (si el corto solo aparece dentro del
    /// largo, se busca la siguiente vez que sale; si no sale más, se queda sin color). Con dos
    /// nombres igual de largos, gana el que va primero en la lista. Es el mismo cálculo que
    /// findNameRanges de engine.js.
    ///
    /// (Antes se coloreaba cada vez que salía un nombre, buscándolo por su texto: con el usuario
    /// "Al" y el nivel "Alpha 123", el "Al" de "Alpha" salía con el color del usuario, y el nivel,
    /// partido, se quedaba sin el suyo.)
    /// </summary>
    /// <param name="texto">El mensaje completo.</param>
    /// <param name="nombres">Cada nombre con su color. Los vacíos no cuentan.</param>
    /// <returns>Los tramos (inicio, fin y color), por orden de posición.</returns>
    private static List<(int Inicio, int Fin, Avalonia.Media.IBrush Color)> TramosDeNombres(
        string texto, params (string? Nombre, Avalonia.Media.IBrush Color)[] nombres)
    {
        // El más largo primero. Es una ordenación estable: los empates conservan su orden.
        var porLargo = new List<(string? Nombre, Avalonia.Media.IBrush Color)>(nombres);
        porLargo.RemoveAll(n => string.IsNullOrEmpty(n.Nombre));
        var ordenados = System.Linq.Enumerable.ToList(
            System.Linq.Enumerable.OrderByDescending(porLargo, n => n.Nombre!.Length));

        var tramos = new List<(int Inicio, int Fin, Avalonia.Media.IBrush Color)>();
        foreach (var (nombre, color) in ordenados)
        {
            int desde = 0;
            while (true)
            {
                int inicio = texto.IndexOf(nombre!, desde, StringComparison.Ordinal);
                if (inicio < 0) break;
                int fin = inicio + nombre!.Length;

                // ¿Se pisa con un nombre que ya tiene su sitio? Entonces se prueba con la siguiente
                // vez que sale.
                bool sePisa = tramos.Exists(t => inicio < t.Fin && fin > t.Inicio);
                if (!sePisa)
                {
                    tramos.Add((inicio, fin, color));
                    break;
                }
                desde = inicio + 1;
            }
        }

        tramos.Sort((a, b) => a.Inicio.CompareTo(b.Inicio));
        return tramos;
    }

    /// <summary>El filtro de color como pincel, para el lienzo.</summary>
    public Avalonia.Media.IBrush DisplayForegroundColor => new Avalonia.Media.SolidColorBrush(ForegroundColor);

    // ==========================================
    // RECORTE Y ESQUINAS EN EL LIENZO
    // ==========================================
    // El lienzo recorta con una ventana: un Border del tamaño visible (CroppedWidth x
    // CroppedHeight), metido CropMargin hacia dentro de la caja y que recorta lo que se sale. El
    // contenido va dentro con su tamaño completo, movido hacia atrás con InverseCropMargin para
    // que se quede en su sitio.

    /// <summary>Dónde empieza la ventana visible dentro de la caja: los cuatro recortes.</summary>
    public Avalonia.Thickness CropMargin => new Avalonia.Thickness(CropLeft, CropTop, CropRight, CropBottom);
    /// <summary>
    /// Lo que se mueve hacia atrás el contenido dentro de la ventana, para que el recorte no lo
    /// desplace.
    /// </summary>
    public Avalonia.Thickness InverseCropMargin => new Avalonia.Thickness(-CropLeft, -CropTop, 0, 0);

    /// <summary>Ancho que se ve después del recorte. Nunca negativo.</summary>
    public double CroppedWidth => Math.Max(0, Width - CropLeft - CropRight);
    /// <summary>Alto que se ve después del recorte. Nunca negativo.</summary>
    public double CroppedHeight => Math.Max(0, Height - CropTop - CropBottom);

    /// <summary>
    /// El radio de las esquinas que dibuja el lienzo: BorderRoundness, pero nunca más de la mitad
    /// del lado visible más corto, para que las esquinas no se deformen.
    /// </summary>
    public Avalonia.CornerRadius SafeCornerRadius
    {
        get
        {
            // El radio pedido en el panel.
            double targetRadius = ActualCornerRadius.TopLeft;

            // Nunca más de la mitad del lado visible más corto.
            double maxRadius = Math.Min(CroppedWidth, CroppedHeight) / 2.0;
            double safeRadius = Math.Min(targetRadius, maxRadius);

            return new Avalonia.CornerRadius(safeRadius);
        }
    }

    // ==========================================
    // LÍMITES DEL TAMAÑO DE LETRA Y DEL HALO
    // ==========================================
    // CommunityToolkit llama a estos métodos cada vez que cambia la propiedad.

    // El tamaño de letra nunca baja de 1: con 0 o menos no hay texto que medir ni que dibujar.
    partial void OnFontSizeChanged(double value)
    {
        if (value <= 0)
        {
            FontSize = 1;
        }
    }

    // La escala del halo nunca baja de 0.1: un halo de tamaño 0 rompe la maquetación.
    partial void OnHaloScaleChanged(double value)
    {
        if (value <= 0) HaloScale = 0.1;
    }

    // ==========================================
    // TEXTO: TAMAÑO Y COLOR
    // ==========================================

    /// <summary>Textos: tamaño de la letra, en píxeles. Nunca baja de 1.</summary>
    [ObservableProperty] private double _fontSize = 20;

    /// <summary>
    /// Textos: color de la letra. Todos los colores del objeto se guardan como texto, tal como
    /// los escribe Avalonia: "#aarrggbb", con el alfa delante, o el nombre si es un color conocido
    /// ("White", "Red"...). Los valores de fábrica están escritos a mano ("#FFFFFF"). engine.js
    /// pasa los de nueve caracteres al orden de CSS ("#rrggbbaa", en formatColor) y deja los
    /// demás tal cual, porque CSS ya los entiende.
    ///
    /// El panel lo cambia a través de TextColor. Avisa él mismo a ColoredTextSegments, el que
    /// repinta el texto en el lienzo, para que el repintado no dependa de por dónde llegue el
    /// cambio (ver RequesterColorHex).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextColor))]
    [NotifyPropertyChangedFor(nameof(DisplayTextColor))]
    [NotifyPropertyChangedFor(nameof(ColoredTextSegments))]
    private string _textColorHex = "#FFFFFF";

    /// <summary>
    /// TextColorHex como Color, para el selector del panel. Al cambiarlo se repinta el texto del
    /// lienzo (los avisos los da TextColorHex).
    /// </summary>
    public Color TextColor
    {
        get => Color.TryParse(TextColorHex, out var color) ? color : Colors.White;
        set => TextColorHex = value.ToString();
    }

    /// <summary>
    /// El color del texto como pincel: el del botón del panel y el de los trozos de
    /// ColoredTextSegments.
    /// </summary>
    public Avalonia.Media.IBrush DisplayTextColor => new Avalonia.Media.SolidColorBrush(TextColor);

    // ==========================================
    // CONTORNO DEL TEXTO
    // ==========================================
    // En el widget se hace con -webkit-text-stroke y paint-order: stroke fill, que dibuja el borde
    // POR DETRÁS de la letra en vez de comérsela por dentro. En el lienzo se hace lo mismo: un
    // Path con la geometría del texto, pintado debajo del TextBlock real y no en su lugar. Así el
    // TextBlock conserva intactos sus trozos de colores (los del cuadro de notificación).

    /// <summary>Textos: si la letra lleva contorno.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTextOutline))]
    private bool _hasTextOutline = false;

    /// <summary>
    /// Textos: grosor del contorno, en píxeles. Significa lo mismo que en CSS (ver
    /// TextOutlineThickness).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTextOutline))]
    [NotifyPropertyChangedFor(nameof(TextOutlineThickness))]
    private double _textOutlineSize = 2;

    /// <summary>Textos: color del contorno, como TextColorHex.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextOutlineColor))]
    [NotifyPropertyChangedFor(nameof(DisplayTextOutlineColor))]
    private string _textOutlineColorHex = "#000000";

    /// <summary>TextOutlineColorHex como Color, para el selector del panel.</summary>
    public Color TextOutlineColor
    {
        get => Color.TryParse(TextOutlineColorHex, out var color) ? color : Colors.Black;
        set { TextOutlineColorHex = value.ToString(); OnPropertyChanged(nameof(DisplayTextOutlineColor)); }
    }

    /// <summary>El color del contorno como pincel.</summary>
    public Avalonia.Media.IBrush DisplayTextOutlineColor => new Avalonia.Media.SolidColorBrush(TextOutlineColor);

    /// <summary>Si el lienzo dibuja el contorno: solo en textos, activado y con grosor.</summary>
    public bool ShowTextOutline => IsText && HasTextOutline && TextOutlineSize > 0;

    /// <summary>
    /// El grosor que se le da al trazo del Path: TextOutlineSize tal cual, sin doblarlo.
    ///
    /// Hubo un intento de multiplicarlo por 2, razonando que el trazo se pinta centrado en el
    /// borde de la letra y el relleno tapa la mitad de dentro. Es cierto, pero
    /// -webkit-text-stroke hace exactamente lo mismo, así que los dos pierden la misma mitad.
    /// Doblarlo solo en el lienzo lo dejaba el doble de grueso que en OBS (medido sobre capturas
    /// de los dos: 2,17x). Así, el número del panel significa lo mismo aquí que en CSS.
    /// </summary>
    public double TextOutlineThickness => TextOutlineSize;

    /// <summary>
    /// Cuánto hay que mover el contorno en horizontal para que caiga justo debajo del texto. Solo
    /// es distinto de 0 sin salto de línea: ver el ajuste horizontal en RebuildOutline.
    /// </summary>
    public double TextOutlineNudgeX { get { RebuildOutline(); return _outlineNudgeX; } }

    // La geometría y las dos correcciones se calculan juntas y se guardan: comparten el mismo
    // FormattedText, y construirlo varias veces sería repetir el shaping. _outlineClave apunta
    // con qué valores se calcularon; mientras no cambien, no se vuelve a calcular.
    private Geometry? _outlineGeom;
    private double _outlineNudgeY;
    private double _outlineNudgeX;
    private string _outlineClave = "";

    /// <summary>
    /// Recalcula la geometría del contorno y sus dos correcciones, pero solo si ha cambiado algo
    /// de lo que las afecta. Lo llaman los getters de TextOutlineGeometry, TextOutlineNudgeX y
    /// TextOutlineNudgeY.
    /// </summary>
    private void RebuildOutline()
    {
        string clave = $"{DisplayText}|{FontSize}|{DisplayFontFamily}|{IsTextWrapped}|{Width}" +
                       $"|{TextPadding}|{TextAlignmentIndex}|{VerticalTextAlignmentIndex}" +
                       $"|{HasTextOutline}|{TextOutlineSize}";
        if (clave == _outlineClave) return;

        _outlineClave = clave;
        _outlineGeom = null;
        _outlineNudgeY = 0;
        _outlineNudgeX = 0;

        if (!ShowTextOutline) return;

        string texto = DisplayText ?? "";
        if (string.IsNullOrEmpty(texto)) return;

        var ft = new FormattedText(
            texto,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(DisplayFontFamily),
            FontSize,
            Brushes.Black);

        // Solo se acota el ancho con salto de línea. Sin él el texto es UNA línea que puede
        // desbordar —para eso está la marquesina— y acotarlo lo partiría en varias.
        if (IsTextWrapped)
        {
            double disponible = Width - (TextPadding * 2);
            if (disponible > 0)
            {
                ft.MaxTextWidth = disponible;
                ft.TextAlignment = AvaloniaTextAlignment;
            }
        }

        _outlineGeom = ft.BuildGeometry(new Avalonia.Point(0, 0));
        if (_outlineGeom == null) return;

        // EL AJUSTE VERTICAL.
        //
        // Avalonia dibuja la geometría en SUS PROPIAS coordenadas: ni la normaliza a la esquina
        // del Path, ni la mete hacia dentro para reservar hueco al borde. Se comprobó midiendo el
        // contorno ya renderizado: sobresale exactamente lo mismo por arriba que por abajo, o sea
        // que está centrado en la letra. Por eso el grosor del trazo NO aparece en estas cuentas.
        //
        // Lo que sí cambia es el alto: el Path reserva del ORIGEN al borde inferior de la tinta
        // (con Inter 200 y "Text": 215,8) y el TextBlock coloca la caja de LÍNEA entera (272,4).
        // Dos alturas distintas centradas en la misma caja: ahí está el desfase.
        var tinta = _outlineGeom.Bounds;
        double alturaLinea = ft.Height;
        double tintaAbajo = tinta.Y + tinta.Height;

        _outlineNudgeY = VerticalTextAlignmentIndex switch
        {
            0 => 0,                                // Arriba: las dos cajas empiezan juntas
            2 => tintaAbajo - alturaLinea,         // Abajo: acaban juntas, va la diferencia entera
            _ => (tintaAbajo - alturaLinea) / 2.0  // Centro: la diferencia, a medias
        };

        // EL AJUSTE HORIZONTAL, por el mismo motivo que el vertical: el Path no se alinea solo,
        // se coloca a mano. Hay dos situaciones distintas:
        //
        //  - CON salto de línea, la geometría ya lleva el centrado DENTRO (MaxTextWidth +
        //    TextAlignment), así que no hay nada que corregir.
        //  - SIN salto de línea no se puede acotar el ancho —partiría el texto en varias líneas
        //    y dejaría de coincidir con el de abajo—, así que la geometría sale en coordenadas
        //    naturales y el centrado hay que ponerlo aquí.
        if (IsTextWrapped)
        {
            _outlineNudgeX = 0;
        }
        else
        {
            double sobra = (Width - TextPadding * 2) - ft.Width;
            _outlineNudgeX = TextAlignmentIndex switch
            {
                0 => 0,           // Izquierda
                2 => sobra,       // Derecha
                _ => sobra / 2.0  // Centro (y justificar)
            };
        }
    }

    /// <summary>La geometría del contorno, o null si no hay que dibujarlo.</summary>
    public Geometry? TextOutlineGeometry { get { RebuildOutline(); return _outlineGeom; } }

    /// <summary>
    /// Cuánto hay que mover el contorno en vertical. Ver el ajuste vertical en RebuildOutline.
    /// </summary>
    public double TextOutlineNudgeY { get { RebuildOutline(); return _outlineNudgeY; } }

    // ==========================================
    // FONDO DE LA CAJA
    // ==========================================

    /// <summary>
    /// Textos: color del fondo de la caja, como TextColorHex. Solo se ve si BackgroundTypeIndex
    /// es 0.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundColor))]
    private string _backgroundColorHex = "#000000";

    /// <summary>
    /// Textos: fondo de la caja. 0 = color sólido (BackgroundColorHex), 1 = transparente.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBackgroundColor))]
    private int _backgroundTypeIndex = 0;

    /// <summary>BackgroundColorHex como Color, para el selector del panel.</summary>
    public Color BackgroundColor
    {
        get => Color.TryParse(BackgroundColorHex, out var color) ? color : Colors.Black;
        set
        {
            BackgroundColorHex = value.ToString();
            OnPropertyChanged(nameof(ButtonBackgroundColor));
            OnPropertyChanged(nameof(DisplayBackgroundColor));
        }
    }

    /// <summary>
    /// El color del fondo como pincel, SIEMPRE sólido: es el del botón del panel, que tiene que
    /// verse aunque el fondo sea transparente.
    /// </summary>
    public Avalonia.Media.IBrush ButtonBackgroundColor => new Avalonia.Media.SolidColorBrush(BackgroundColor);

    /// <summary>El fondo que dibuja el lienzo: transparente si BackgroundTypeIndex es 1.</summary>
    public Avalonia.Media.IBrush DisplayBackgroundColor =>
        BackgroundTypeIndex == 1 ? Avalonia.Media.Brushes.Transparent : new Avalonia.Media.SolidColorBrush(BackgroundColor);

    // ==========================================
    // FUENTE DEL TEXTO
    // ==========================================

    /// <summary>
    /// Textos: familia de letra del sistema, por su nombre. Solo cuenta si UseCustomFont es
    /// false. Al guardar, el Editor intenta incrustarla en el tema (ver AvisoFuente).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayFontFamily))]
    [NotifyPropertyChangedFor(nameof(AvisoFuente))]
    [NotifyPropertyChangedFor(nameof(HayAvisoFuente))]
    [NotifyPropertyChangedFor(nameof(AvisoFuenteBrush))]
    private string _fontFamilyName = "Inter";

    /// <summary>
    /// Textos: ruta del archivo de la fuente propia (.ttf u .otf). Al cambiar, se registra en el
    /// FontManager para que el lienzo pueda dibujarla (OnCustomFontPathChanged).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayFontFamily))]
    [NotifyPropertyChangedFor(nameof(CustomFontButtonText))]
    private string _customFontPath = "";

    /// <summary>
    /// Textos: true = se usa la fuente propia (CustomFontPath) en lugar de la del sistema.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayFontFamily))]
    [NotifyPropertyChangedFor(nameof(AvisoFuente))]
    [NotifyPropertyChangedFor(nameof(HayAvisoFuente))]
    [NotifyPropertyChangedFor(nameof(AvisoFuenteBrush))]
    private bool _useCustomFont = false;

    // El nombre de familia que lleva dentro el archivo de la fuente propia, según SkiaSharp.
    // "?" = no se pudo leer.
    private string _internalFontName = "?";

    // La clave de la colección que se registra en el FontManager para ESTE archivo. Vale
    // cualquier Uri absoluto; "fonts:" es la convención de Avalonia. Null = no hay fuente
    // registrada, y se usa la del sistema.
    private Uri? _claveFuente;

    // Al cambiar la ruta de la fuente propia: lee el nombre de familia del archivo y lo registra
    // en el FontManager. Si algo falla, el objeto se queda con la fuente del sistema.
    partial void OnCustomFontPathChanged(string value)
    {
        // Se reinician los dos SIEMPRE. Antes no se tocaban si el archivo no existía, así que al
        // pasar de una fuente válida a una ruta muerta se quedaba el nombre de familia anterior.
        _internalFontName = "?";
        _claveFuente = null;

        if (!File.Exists(value)) return;

        try
        {
            using var typeface = SKTypeface.FromFile(value);
            _internalFontName = typeface.FamilyName;
        }
        catch
        {
            _internalFontName = "?";
            return;
        }

        // Se registra el archivo en el FontManager. SIN ESTO el lienzo solo dibujaría la fuente si
        // estuviera INSTALADA en el sistema: para Avalonia, un FontFamily cuyo nombre no lleva
        // "#" es un nombre de familia del sistema y nada más. Ver el comentario de
        // DisplayFontFamily.
        try
        {
            // La clave lleva el nombre del archivo Y una huella de su ruta completa. Solo con el
            // nombre, dos fuentes distintas que se llamaran igual —fuente.ttf de dos carpetas, o
            // fuente.ttf y fuente.otf— compartían clave, y como AddFontCollection REEMPLAZA, la
            // segunda dejaba a la primera sin fuente en el lienzo. La huella solo tiene que ser
            // estable dentro de la sesión: el mismo archivo da la misma clave, así que volver a
            // registrarlo sigue siendo seguro.
            string huella = StringComparer.Ordinal.GetHashCode(Path.GetFullPath(value)).ToString("x8");
            var clave = new Uri("fonts:sheepgd/" + Path.GetFileNameWithoutExtension(value) + "_" + huella,
                UriKind.Absolute);

            // Se apunta al ARCHIVO y no a su carpeta: FontCollectionBase sabe hacer las dos cosas,
            // pero aquí la carpeta es la temporal del sistema y arrastraría las fuentes de todos
            // los temas abiertos en la sesión. Y como AddFontCollection REEMPLAZA la colección si
            // la clave ya existe, volver a registrarla al abrir otra vez un tema es seguro.
            FontManager.Current.AddFontCollection(new EmbeddedFontCollection(clave, new Uri(value)));
            _claveFuente = clave;
        }
        catch (Exception ex)
        {
            Console.WriteLine(EditorI18n.Trf("Log_FontRegisterError", value, ex.Message));
        }
    }

    /// <summary>
    /// El texto del botón de la fuente propia: el nombre del archivo, o la invitación a elegir
    /// uno si no hay.
    /// </summary>
    public string CustomFontButtonText => string.IsNullOrEmpty(CustomFontPath)
        ? EditorI18n.Tr("UI_BrowseFontFile")
        : Path.GetFileName(CustomFontPath);

    // ==========================================
    // AVISO DE FUENTE DEL SISTEMA
    // ==========================================
    // Una fuente del sistema viaja al widget solo como NOMBRE: el navegador de OBS la busca entre
    // las fuentes instaladas de su equipo, y si no está, cae a sans-serif sin avisar. Al guardar
    // se incrusta en el tema si se puede, y este aviso lo dice AL ELEGIRLA, antes de guardar.
    //
    // FuentesDelSistema.Comprobar() es barato y guarda el resultado por familia: solo lee los 4
    // bytes de la firma del archivo. Recorrer el desplegable con las flechas no extrae nada.

    /// <summary>En qué caso está el objeto. De paso apunta el motivo en _motivoFuente.</summary>
    private FuentesDeSistemaEstado EstadoFuente
    {
        get
        {
            if (!IsText || UseCustomFont) return FuentesDeSistemaEstado.NoAplica;
            var c = SheepGDEditor.Services.FuentesDelSistema.Comprobar(FontFamilyName);
            _motivoFuente = c.Motivo;
            return c.SePuede ? FuentesDeSistemaEstado.SeIncrustara : FuentesDeSistemaEstado.NoSePuede;
        }
    }

    // NoAplica: no es un texto, o usa fuente propia. SeIncrustara: se incrustará al guardar.
    // NoSePuede: no se puede incrustar, y el aviso dice por qué.
    private enum FuentesDeSistemaEstado { NoAplica, SeIncrustara, NoSePuede }
    private string _motivoFuente = ""; // por qué no se puede; lo apunta EstadoFuente

    /// <summary>Si el panel enseña el aviso: en los textos con fuente del sistema.</summary>
    public bool HayAvisoFuente => EstadoFuente != FuentesDeSistemaEstado.NoAplica;

    /// <summary>El texto del aviso: que se incrustará al guardar, o por qué no se puede.</summary>
    public string AvisoFuente => EstadoFuente switch
    {
        FuentesDeSistemaEstado.SeIncrustara =>
            EditorI18n.Tr("UI_FontWillEmbed"),
        FuentesDeSistemaEstado.NoSePuede =>
            EditorI18n.Trf("UI_FontCannotEmbed", _motivoFuente, EditorI18n.Tr("UI_AddCustomFont")),
        _ => ""
    };

    /// <summary>
    /// Color del aviso: gris apagado si todo va bien, y el ámbar de "● Sin guardar" de la barra
    /// si no se puede, para distinguirlos sin tener que leerlos.
    /// </summary>
    public IBrush AvisoFuenteBrush => EstadoFuente == FuentesDeSistemaEstado.NoSePuede
        ? new SolidColorBrush(Color.Parse("#F9E2AF"))
        : new SolidColorBrush(Color.Parse("#7F849C"));

    /// <summary>
    /// La fuente con la que el lienzo dibuja el texto: la propia, si está activada y se pudo
    /// registrar; si no, la del sistema, por su nombre.
    /// </summary>
    public FontFamily DisplayFontFamily
    {
        get
        {
            // 1. Fuente del tema, ya registrada en el FontManager por OnCustomFontPathChanged.
            if (UseCustomFont && _claveFuente != null && _internalFontName != "?")
            {
                try
                {
                    // El origen viaja DENTRO del nombre, detrás de "#", y eso no es cosmética:
                    // comprobado en el código de Avalonia, cuando el nombre no lleva "#", el
                    // identificador se queda con Source y Key nulos y el baseUri SE DESCARTA, así
                    // que la familia se busca entre las fuentes INSTALADAS del sistema. Por eso un
                    // FontFamily(new Uri(carpeta), "Pusab") no carga nada: solo funcionaría en los
                    // equipos que tuvieran esa fuente instalada.
                    //
                    // Tampoco sirve una URI "file://": no es un origen de fuente válido para
                    // FontFamily. El archivo lo carga el FontManager, no esto.
                    return new FontFamily($"{_claveFuente}#{_internalFontName}");
                }
                catch
                {
                    return new FontFamily(FontFamilyName);
                }
            }

            // 2. Si no, la fuente instalada del sistema.
            return new FontFamily(FontFamilyName);
        }
    }

    // ==========================================
    // OPACIDAD Y ESQUINAS
    // ==========================================

    /// <summary>
    /// OPACIDAD del objeto, de 0 (invisible) a 1 (opaco). Pese a llamarse Transparency, cuanto
    /// más alto, más se ve.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalOpacity))]
    private double _transparency = 1.0;

    /// <summary>
    /// Opacidad de la vista previa, de 0 a 1: la usan los fundidos de las notificaciones de
    /// demostración. Solo del Editor.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalOpacity))]
    private double _previewOpacity = 1.0;

    /// <summary>La opacidad con la que se dibuja: Transparency por PreviewOpacity.</summary>
    public double FinalOpacity => Transparency * PreviewOpacity;

    /// <summary>
    /// Radio de las esquinas redondeadas, en píxeles. 0 = rectas. Al dibujarlo se limita a la
    /// mitad del lado visible más corto (SafeCornerRadius).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualCornerRadius))]
    private double _borderRoundness = 8;

    // ==========================================
    // IMAGEN O VÍDEO DEL OBJETO
    // ==========================================

    /// <summary>
    /// Imágenes y vídeos: ruta del archivo en este equipo (en un vídeo, el propio vídeo). Vacía
    /// si no lleva. En el fondo del nivel es la imagen por defecto: la que se ve cuando el nivel
    /// no tiene miniatura y entre peticiones.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMediaPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowImageLayer))]
    [NotifyPropertyChangedFor(nameof(HasVideoSource))]
    [NotifyPropertyChangedFor(nameof(MediaPlaceholderName))]
    [NotifyPropertyChangedFor(nameof(CanClearDefaultImage))]
    [NotifyPropertyChangedFor(nameof(LevelBgHint))]
    private string _imageFilePath = "";

    /// <summary>
    /// Imágenes: la imagen ya cargada, la que dibuja el lienzo. Null en los vídeos.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage))]
    [NotifyPropertyChangedFor(nameof(ShowMediaPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowImageLayer))]
    private Bitmap? _imageBitmap;

    /// <summary>
    /// Imágenes y vídeos: cómo se ajustan a la caja. 0 = mantener la proporción (puede sobrar
    /// espacio), 1 = estirar hasta llenarla (puede deformarse), 2 = llenar con zoom (mantiene la
    /// proporción y recorta lo que sobra).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageStretchMode))]
    private int _stretchIndex = 0;

    /// <summary>StretchIndex traducido a lo que entiende Avalonia.</summary>
    public Avalonia.Media.Stretch ImageStretchMode => StretchIndex switch
    {
        1 => Avalonia.Media.Stretch.Fill,
        2 => Avalonia.Media.Stretch.UniformToFill,
        _ => Avalonia.Media.Stretch.Uniform
    };

    // ==========================================
    // VÍDEO
    // ==========================================
    // Avalonia no reproduce vídeo, así que en el lienzo un vídeo se ve como su fotograma de
    // muestra, si tiene uno, o como una tarjeta de marcador con el nombre del archivo. Al ZIP va
    // el archivo de verdad, y engine.js lo convierte en una etiqueta <video>.

    /// <summary>Si es un vídeo con archivo elegido.</summary>
    public bool HasVideoSource => IsVideo && !string.IsNullOrEmpty(ImageFilePath);

    /// <summary>
    /// Si el lienzo dibuja la tarjeta de marcador: es una imagen o un vídeo y no hay nada que
    /// enseñar (DisplayImage es null). Un vídeo con fotograma de muestra no la necesita: se
    /// pinta como cualquier imagen.
    /// </summary>
    public bool ShowMediaPlaceholder => IsMedia && DisplayImage == null;

    /// <summary>Si el lienzo dibuja una imagen de verdad (DisplayImage).</summary>
    public bool ShowImageLayer => IsMedia && DisplayImage != null;

    /// <summary>
    /// Lo que pone dentro de la tarjeta de marcador: el nombre del archivo, o que no hay ninguno.
    /// </summary>
    public string MediaPlaceholderName => string.IsNullOrEmpty(ImageFilePath)
        ? (IsVideo ? EditorI18n.Tr("UI_NoVideo") : EditorI18n.Tr("UI_NoImage"))
        : Path.GetFileName(ImageFilePath);

    /// <summary>
    /// Vídeos: si vuelve a empezar al terminar (atributo loop del &lt;video&gt;).
    /// </summary>
    [ObservableProperty] private bool _videoLoop = true;
    /// <summary>Vídeos: si va sin sonido (atributo muted del &lt;video&gt;).</summary>
    [ObservableProperty] private bool _videoMuted = true;

    // ==========================================
    // FOTOGRAMA DE MUESTRA DEL VÍDEO
    // ==========================================
    // Avalonia no decodifica vídeo, y meter FFmpeg para sacar un fotograma costaría entre 70 y
    // 100 MB por plataforma (ver "Descartado a propósito" en la §5 del CLAUDE.md). Así que el
    // fotograma lo elige el diseñador: acierta más que una extracción automática, que casi
    // siempre se queda con el primer fotograma, y sale negro.
    //
    // Además viaja al widget como atributo poster del <video>, así que también tapa el hueco
    // entre que carga la página y arranca el vídeo en OBS.

    /// <summary>
    /// Vídeos: ruta de la imagen del fotograma de muestra. Vacía = sin fotograma.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideoPosterPath))]
    [NotifyPropertyChangedFor(nameof(VideoPosterName))]
    private string _videoPosterPath = "";

    /// <summary>
    /// Vídeos: el fotograma ya cargado. Puede ser null aunque haya ruta, si Avalonia no pudo
    /// leer la imagen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage))]
    [NotifyPropertyChangedFor(nameof(ShowMediaPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowImageLayer))]
    private Bitmap? _videoPosterBitmap;

    /// <summary>
    /// Si se enseña el botón de quitar el fotograma. Va por la RUTA y no por la imagen a
    /// propósito: al abrir un tema puede haber fotograma sin imagen cargada (un formato que
    /// Avalonia no decodifica), y ese fotograma sí se exporta, así que tiene que poder quitarse.
    /// </summary>
    public bool HasVideoPosterPath => IsVideo && !string.IsNullOrEmpty(VideoPosterPath);

    /// <summary>El nombre del fotograma que enseña el panel junto al botón, o que no hay.</summary>
    public string VideoPosterName => string.IsNullOrEmpty(VideoPosterPath)
        ? EditorI18n.Tr("UI_NoPoster")
        : Path.GetFileName(VideoPosterPath);

    // ==========================================
    // HALO DE LA CARA DE DIFICULTAD
    // ==========================================
    // En el directo, qué halo se dibuja (featured, epic, legendary o mythic) lo decide el nivel.
    // El tema solo guarda si se quiere (ShowHalo) y de qué tamaño (HaloScale). HaloIndex y
    // HaloBitmap son de la vista previa del Editor.

    /// <summary>
    /// Qué halo se enseña en el lienzo: 0 = automático (van rotando, con la vista previa
    /// encendida), 1 = ninguno, 2 = featured, 3 = epic, 4 = legendary, 5 = mythic. Solo del
    /// Editor, no se guarda.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHaloImage))]
    [NotifyPropertyChangedFor(nameof(HasHalo))]
    private int _haloIndex = 0;

    /// <summary>
    /// La imagen del halo que dibuja el lienzo. La pone MainWindowViewModel según HaloIndex
    /// (UpdateHaloBitmaps).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHaloImage))]
    [NotifyPropertyChangedFor(nameof(HasHalo))]
    private Bitmap? _haloBitmap;

    /// <summary>
    /// Si se dibuja el halo detrás de la cara cuando el nivel lo tiene. Se guarda en el tema.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHaloImage))]
    [NotifyPropertyChangedFor(nameof(HasHalo))]
    private bool _showHalo = true;

    /// <summary>
    /// Tamaño del halo respecto a la cara: 1.5 = un 150 %, igual que en el widget. Nunca baja de
    /// 0.1.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HaloWidth))]
    [NotifyPropertyChangedFor(nameof(HaloHeight))]
    private double _haloScale = 1.5;

    /// <summary>Si es la cara de dificultad, el único objeto que lleva halo.</summary>
    public bool IsRateFace => ObjectType == WidgetObjectType.RateFace;

    /// <summary>
    /// El halo que dibuja el lienzo: solo en la cara, con el halo activado y sin haber elegido
    /// "ninguno".
    /// </summary>
    public Bitmap? DisplayHaloImage =>
        IsRateFace && ShowHalo && HaloIndex != 1 ? HaloBitmap : null;

    /// <summary>Si hay halo que dibujar.</summary>
    public bool HasHalo => DisplayHaloImage != null;

    /// <summary>Ancho del halo en el lienzo: el de la cara por HaloScale.</summary>
    public double HaloWidth => Width * HaloScale;
    /// <summary>Alto del halo en el lienzo: el de la cara por HaloScale.</summary>
    public double HaloHeight => Height * HaloScale;

    /// <summary>
    /// BorderRoundness como CornerRadius. El lienzo no lo usa directamente: usa SafeCornerRadius,
    /// que parte de este.
    /// </summary>
    public CornerRadius ActualCornerRadius => new CornerRadius(BorderRoundness);

    /// <summary>Si el objeto está seleccionado en el lienzo. Solo del Editor.</summary>
    [ObservableProperty] private bool _isSelected = false;

    // ==========================================
    // QUÉ CLASE DE OBJETO ES
    // ==========================================
    // Agrupan los tipos de WidgetObjectType según cómo se dibujan y qué secciones del panel de
    // propiedades se ven.

    /// <summary>
    /// Si se dibuja como texto: el texto fijo, todos los datos que son texto y el cuadro de
    /// notificación.
    /// </summary>
    public bool IsText => ObjectType == WidgetObjectType.Text ||
                          ObjectType == WidgetObjectType.LevelName ||
                          ObjectType == WidgetObjectType.CreatorName ||
                          ObjectType == WidgetObjectType.LevelId ||
                          ObjectType == WidgetObjectType.SentBy ||
                          ObjectType == WidgetObjectType.PlatformName ||
                          ObjectType == WidgetObjectType.RequestStatus ||
                          ObjectType == WidgetObjectType.Timer ||
                          ObjectType == WidgetObjectType.RateText ||
                          ObjectType == WidgetObjectType.Stars ||
                          ObjectType == WidgetObjectType.Downloads ||
                          ObjectType == WidgetObjectType.LikesAndDislikes ||
                          ObjectType == WidgetObjectType.Orbs ||
                          ObjectType == WidgetObjectType.Duration ||
                          ObjectType == WidgetObjectType.AredlPos ||
                          ObjectType == WidgetObjectType.GddlRate||
                          ObjectType == WidgetObjectType.NotificationBox;

    /// <summary>Si es el cuadro de notificación.</summary>
    public bool IsNotificationBox => ObjectType == WidgetObjectType.NotificationBox;

    /// <summary>
    /// RequesterColorHex como Color, para el selector del panel. Al cambiarlo se repinta el texto
    /// del lienzo (los avisos los da RequesterColorHex).
    /// </summary>
    public Color RequesterColor
    {
        get => Color.TryParse(RequesterColorHex, out var color) ? color : Colors.Red;
        set => RequesterColorHex = value.ToString();
    }
    /// <summary>El color del usuario como pincel.</summary>
    public Avalonia.Media.IBrush DisplayRequesterColor => new Avalonia.Media.SolidColorBrush(RequesterColor);

    /// <summary>
    /// LevelDataColorHex como Color, para el selector del panel. Al cambiarlo se repinta el texto
    /// del lienzo (los avisos los da LevelDataColorHex).
    /// </summary>
    public Color LevelDataColor
    {
        get => Color.TryParse(LevelDataColorHex, out var color) ? color : Colors.Green;
        set => LevelDataColorHex = value.ToString();
    }
    /// <summary>El color del nivel como pincel.</summary>
    public Avalonia.Media.IBrush DisplayLevelDataColor => new Avalonia.Media.SolidColorBrush(LevelDataColor);

    /// <summary>
    /// Si se dibuja como imagen: la imagen elegida y los datos que son imágenes (fondo del nivel,
    /// icono de plataforma, cara de dificultad, like y estrella).
    /// </summary>
    public bool IsImage => ObjectType == WidgetObjectType.Image ||
                           ObjectType == WidgetObjectType.LevelBackground ||
                           ObjectType == WidgetObjectType.PlatformIcon ||
                           ObjectType == WidgetObjectType.RateFace ||
                           ObjectType == WidgetObjectType.LikeIcon ||
                           ObjectType == WidgetObjectType.StarIcon;

    /// <summary>Imagen o vídeo: comparten casi todas las opciones del panel.</summary>
    public bool IsMedia => IsImage || IsVideo;

    /// <summary>Si es un vídeo.</summary>
    public bool IsVideo => ObjectType == WidgetObjectType.Video;

    /// <summary>
    /// Si el panel deja escribir el texto: solo en el texto fijo (Text). En los demás lo pone el
    /// dato.
    /// </summary>
    public bool CanEditTextContent => ObjectType == WidgetObjectType.Text;

    /// <summary>
    /// Si el panel deja elegir un archivo del equipo. Image y Video, porque ese archivo ES su
    /// contenido. Y LevelBackground, porque para él ese archivo es su IMAGEN POR DEFECTO: el
    /// contenido de verdad es la miniatura del nivel, que llega con los datos. El Editor le pone
    /// 16-9BG.png al crearlo, y engine.js la pinta cuando el nivel no tiene miniatura y entre
    /// peticiones. El resto de objetos de datos NO: su imagen la pone el Editor (ver
    /// SwapIconAssets en MainWindowViewModel).
    /// </summary>
    public bool CanChangeMediaSource => ObjectType == WidgetObjectType.Image ||
                                        ObjectType == WidgetObjectType.Video ||
                                        ObjectType == WidgetObjectType.LevelBackground;

    /// <summary>Si es el fondo del nivel (la miniatura).</summary>
    public bool IsLevelBackground => ObjectType == WidgetObjectType.LevelBackground;

    /// <summary>
    /// Si se enseña el botón "Quitar imagen por defecto": solo en el fondo del nivel, y solo si
    /// tiene una.
    /// </summary>
    public bool CanClearDefaultImage => IsLevelBackground && !string.IsNullOrEmpty(ImageFilePath);

    /// <summary>
    /// La explicación bajo la imagen por defecto. Sin imagen, la de siempre ("se ve cuando...")
    /// hablaría de algo que ya no existe, así que dice lo que pasa entonces: no se ve nada.
    /// </summary>
    public string LevelBgHint => string.IsNullOrEmpty(ImageFilePath)
        ? EditorI18n.Tr("UI_Prop_LevelBgHintEmpty")
        : EditorI18n.Tr("UI_Prop_LevelBgHint");

    /// <summary>
    /// La etiqueta del selector de archivo, que significa cosas distintas según el objeto:
    /// "Imagen por defecto" en el fondo del nivel y "Archivo de origen" en los demás.
    /// </summary>
    public string MediaSourceLabel => IsLevelBackground ? EditorI18n.Tr("UI_DefaultImage") : EditorI18n.Tr("UI_SourceFile");

    // ==========================================
    // CONSTRUCTOR
    // ==========================================

    /// <summary>
    /// Engancha un único oyente a los cambios del propio objeto, para lo que no se puede (o no
    /// conviene) expresar con [NotifyPropertyChangedFor]: el auto-tamaño de los textos y los
    /// avisos de las propiedades calculadas del recorte, del halo y del contorno.
    /// </summary>
    public WidgetItemViewModel()
    {
        this.PropertyChanged += (sender, args) =>
        {
            // Un texto crece de alto si deja de caber (AutoSizeToFitText).
            if (IsText && (args.PropertyName == nameof(FontSize) || args.PropertyName == nameof(ObjectName)))
            {
                AutoSizeToFitText();
            }

            // Todo lo que depende del tamaño, del recorte o del redondeo de las esquinas.
            // (ActualCornerRadius no hace falta en la lista: solo cambia con BorderRoundness, y
            // cuando estaba, cada cambio del redondeo repetía estos avisos dos veces.)
            if (args.PropertyName is nameof(Width) or nameof(Height) or nameof(CropTop) or nameof(CropBottom) or nameof(CropLeft) or nameof(CropRight) or nameof(BorderRoundness))
            {
                OnPropertyChanged(nameof(CropMargin));
                OnPropertyChanged(nameof(InverseCropMargin));
                OnPropertyChanged(nameof(SafeCornerRadius));
                OnPropertyChanged(nameof(CroppedWidth));
                OnPropertyChanged(nameof(CroppedHeight));
            }

            // El halo crece y encoge con la cara.
            if (args.PropertyName is nameof(Width) or nameof(Height))
            {
                OnPropertyChanged(nameof(HaloWidth));
                OnPropertyChanged(nameof(HaloHeight));
            }

            // La geometría del contorno depende de media docena de cosas. Va en un solo sitio y no
            // como diez [NotifyPropertyChangedFor] repartidos, que es donde se olvida uno. Tiene
            // que estar todo lo que mira RebuildOutline. (VerticalTextAlignmentIndex faltó durante
            // un tiempo: cambia TextOutlineNudgeY, y al cambiar la alineación vertical el contorno
            // se quedaba unos píxeles desplazado respecto al texto.)
            if (args.PropertyName is nameof(DisplayText) or nameof(PreviewText) or nameof(ObjectName)
                or nameof(FontSize) or nameof(Width) or nameof(TextPadding) or nameof(IsTextWrapped)
                or nameof(TextAlignmentIndex) or nameof(VerticalTextAlignmentIndex)
                or nameof(HasTextOutline) or nameof(TextOutlineSize)
                or nameof(FontFamilyName) or nameof(UseCustomFont) or nameof(CustomFontPath)
                or nameof(IsPreviewEnabled) or nameof(ObjectType))
            {
                OnPropertyChanged(nameof(TextOutlineGeometry));
                OnPropertyChanged(nameof(TextOutlineNudgeX));
                OnPropertyChanged(nameof(TextOutlineNudgeY));
            }
        };
    }

    // ==========================================
    // MEDICIÓN REAL DEL TEXTO
    // ==========================================
    // Antes se estimaba con `largo * fontSize * 0.65`, un número a ojo que falla con cualquier
    // fuente que no sea la de referencia. engine.js mide de verdad con scrollWidth, así que el
    // editor y OBS no coincidían: la marquesina arrancaba aquí y allí no, o al revés.

    // La última medida, y la clave con la que se hizo (ver MeasureDisplayText).
    private string _medidaClave = "";
    private double _medidaAncho;
    private double _medidaAlto;

    /// <summary>
    /// Ancho y alto reales del texto que se está mostrando. Cachea el resultado porque
    /// FormattedText hace shaping, que no es gratis, y UpdateMarqueeAnimations pide esto para
    /// cada objeto 20 veces por segundo. La clave incluye todo lo que puede cambiar la medida.
    /// </summary>
    public (double W, double H) MeasureDisplayText()
    {
        string texto = DisplayText ?? "";
        string clave = $"{texto}|{FontSize}|{DisplayFontFamily}|{IsTextWrapped}|{Width}|{TextPadding}";

        if (clave != _medidaClave)
        {
            _medidaClave = clave;
            (_medidaAncho, _medidaAlto) = Medir(texto);
        }

        return (_medidaAncho, _medidaAlto);
    }

    /// <summary>
    /// Mide un texto con la fuente, el tamaño, el salto de línea y el ancho del objeto. Sin caché.
    /// </summary>
    private (double W, double H) Medir(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return (0, 0);

        var ft = new FormattedText(
            texto,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(DisplayFontFamily),
            FontSize,
            Brushes.Black);

        // Con salto de línea activado el alto depende del ancho disponible, así que hay que
        // acotarlo o FormattedText mediría una sola línea infinita.
        if (IsTextWrapped)
        {
            double disponible = Width - (TextPadding * 2);
            if (disponible > 0) ft.MaxTextWidth = disponible;
        }

        return (ft.Width, ft.Height);
    }

    /// <summary>
    /// Hace crecer en alto la caja de un texto cuando el texto deja de caber. Nunca la encoge ni
    /// le toca el ancho.
    /// </summary>
    private void AutoSizeToFitText()
    {
        if (string.IsNullOrEmpty(ObjectName)) return;

        // Se mide ObjectName, no DisplayText: el texto de vista previa rota cada 3 segundos y el
        // cuadro acabaría creciendo por un dato de mentira. Aquí manda el texto de diseño.
        // Sin caché a propósito: esto solo corre al cambiar FontSize u ObjectName, no en bucle.
        double alto = Medir(ObjectName).H;

        // LA REGLA: el auto-tamaño solo interviene cuando el texto NO cabe de alto, y solo crece.
        //
        // Antes reescribía Width y Height en CADA cambio de FontSize u ObjectName, así que si
        // ajustabas el ancho a mano y luego tocabas el tamaño de fuente, perdías el ajuste.
        // Ahora el ancho no se toca nunca: para el texto que se sale por los lados ya está la
        // marquesina, que es justo para eso.
        //
        // Y no hace falta ninguna bandera tipo "esto lo tocó el usuario": la regla se limita
        // sola y se vuelve a evaluar cada vez, sin estado oculto que se quede pegado.
        if (alto > Height) Height = Math.Ceiling(alto);
    }
}


/// <summary>
/// Un trozo del texto del lienzo con su color. Ver WidgetItemViewModel.ColoredTextSegments.
/// </summary>
public record ColoredTextSegment(string Text, Avalonia.Media.IBrush Brush);

/// <summary>
/// La propiedad adjunta TextBlockHelper.ColoredSegments, para los TextBlock del lienzo: recibe
/// una lista de trozos de color y la convierte en los Runs del TextBlock. Hace falta porque los
/// Inlines de un TextBlock no se pueden rellenar con un enlace a una lista del ViewModel.
/// </summary>
public class TextBlockHelper
{
    /// <summary>La propiedad adjunta: una lista de ColoredTextSegment.</summary>
    public static readonly AttachedProperty<IEnumerable<ColoredTextSegment>> ColoredSegmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlockHelper, TextBlock, IEnumerable<ColoredTextSegment>>("ColoredSegments");

    // Cada vez que cambia la lista, se vacían los Inlines del TextBlock y se rehacen, un Run por
    // trozo.
    static TextBlockHelper()
    {
        ColoredSegmentsProperty.Changed.AddClassHandler<TextBlock>((tb, e) =>
        {
            if (e.NewValue is IEnumerable<ColoredTextSegment> segments)
            {
                tb.Inlines?.Clear();
                foreach (var seg in segments)
                {
                    tb.Inlines?.Add(new Avalonia.Controls.Documents.Run
                    {
                        Text = seg.Text,
                        Foreground = seg.Brush
                    });
                }
            }
        });
    }

    /// <summary>Da valor a la propiedad adjunta. Lo usa el XAML.</summary>
    public static void SetColoredSegments(AvaloniaObject element, IEnumerable<ColoredTextSegment> value)
        => element.SetValue(ColoredSegmentsProperty, value);

    /// <summary>Lee la propiedad adjunta. Lo usa el XAML.</summary>
    public static IEnumerable<ColoredTextSegment> GetColoredSegments(AvaloniaObject element)
        => element.GetValue(ColoredSegmentsProperty);
}
