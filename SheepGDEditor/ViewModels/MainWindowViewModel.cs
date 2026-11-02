using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Text.Json;
using SheepGDEditor.Models;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using SheepGDEditor.Services;

namespace SheepGDEditor.ViewModels;

/// <summary>
/// Una imagen de la lista "Imágenes predefinidas" de la caja de herramientas: el nombre que se
/// enseña y la ruta completa del archivo.
/// </summary>
public record PredefinedImageItem(string FileName, string FullPath);
/// <summary>
/// Una tarjeta "fantasma": el rectángulo, en coordenadas del lienzo, que ocuparía una de las
/// tarjetas repetidas de la cola o de las notificaciones. El lienzo las dibuja como referencia
/// para ver cómo quedará el widget con varios niveles o varias notificaciones.
/// </summary>
public record QueueGhostBox(double X, double Y, double Width, double Height);

/// <summary>
/// El ViewModel de la ventana principal del Editor, y el centro de casi todo: los cuatro lienzos
/// con sus objetos, la selección, el historial de deshacer y rehacer, el guardado y la carga de
/// temas (.zip), la vista previa animada con datos de demostración y la vista previa en el
/// navegador.
///
/// Cómo está organizado, de arriba abajo:
///   - Estado general: panel visible, nombre y autor del tema, pestaña (lienzo) activa, y los
///     ajustes globales de la cola y de las notificaciones.
///   - Datos de demostración, que rotan cada 3 segundos en el lienzo y en la vista previa.
///   - Constructor y temporizadores.
///   - Comandos de la interfaz: añadir objetos, cambiar de panel, zoom, elegir archivos...
///   - Guardado (ZIP) y vista previa en el navegador.
///   - Carga de temas.
///   - Portapapeles, historial de deshacer, capas, alineación y vínculos.
///   - theme.json: su generación y el editor de código.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// El panel que se ve en la columna derecha: la caja de herramientas, las propiedades o el
    /// editor de código. La vista de cada uno la eligen las DataTemplates del
    /// TransitioningContentControl de MainWindow.axaml. La caja de herramientas es el propio
    /// ViewModel (this): no tiene estado propio y todos sus botones hablan con él.
    /// </summary>
    [ObservableProperty]
    private ObservableObject _currentSidePanel;

    /// <summary>
    /// Panel de propiedades del objeto seleccionado. Se crea una vez y se reutiliza.
    /// </summary>
    public PropertiesViewModel PropertiesVM { get; } = new();
    /// <summary>
    /// Editor de código del tema (index.html, engine.js...). Se crea una vez y se reutiliza.
    /// </summary>
    public CodeEditorSideViewModel CodeEditorVM { get; } = new();

    /// <summary>Si se ve el lienzo. Se alterna con <see cref="IsCodeEditorVisible"/>.</summary>
    [ObservableProperty] private bool _isCanvasVisible = true;
    /// <summary>Si se ve el editor de código en lugar del lienzo.</summary>
    [ObservableProperty] private bool _isCodeEditorVisible = false;
    /// <summary>Nombre del tema (va a theme.json y a los menús de SheepGDRequest).</summary>
    [ObservableProperty] private string _themeName = EditorI18n.Tr("Default_ThemeName");
    /// <summary>Autor del tema (va a theme.json).</summary>
    [ObservableProperty] private string _themeAuthor = EditorI18n.Tr("Default_ThemeAuthor");

    // ==========================================
    // PESTAÑA ACTIVA Y AJUSTES DEL WIDGET
    // ==========================================

    /// <summary>
    /// Pestaña (lienzo) que se está editando: 0 = Playing, 1 = Queue, 2 = Notification,
    /// 3 = Unified. La opción Unified solo aparece cuando hace falta (ver
    /// RefreshWidgetTypeOptions). Al cambiar, avisa a todas las propiedades que dependen de la
    /// pestaña.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLevelDataVisible))]
    [NotifyPropertyChangedFor(nameof(IsCanvasSizeVisible))]
    [NotifyPropertyChangedFor(nameof(IsNotificationWidgetVisible))]
    [NotifyPropertyChangedFor(nameof(IsQueueSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(CurrentCanvasObjects))]
    [NotifyPropertyChangedFor(nameof(IsUnifiedCanvasActive))]
    [NotifyPropertyChangedFor(nameof(IsQueueGhostsVisible))]
    [NotifyPropertyChangedFor(nameof(IsNotifGhostsVisible))]
    private int _selectedWidgetTypeIndex = 0;

    // --- Cola: ajustes globales (van a theme.json) ---

    /// <summary>Cola: cuántos niveles se muestran a la vez como máximo.</summary>
    [ObservableProperty] private int _queueMaxItems = 4;
    /// <summary>Cola: 0 = tarjetas en columna (vertical), 1 = en fila (horizontal).</summary>
    [ObservableProperty] private int _queueDirectionIndex = 0;
    /// <summary>Cola: separación entre tarjetas, en píxeles.</summary>
    [ObservableProperty] private double _queueSpacing = 20;

    /// <summary>
    /// Si el lienzo DIBUJA las tarjetas fantasma de la cola (la casilla "Mostrar tarjetas
    /// fantasma"). Solo el dibujo: las tarjetas se calculan igual, porque cuentan para el tamaño
    /// exportado (ComputeExportedCanvasSize). Antes la casilla las vaciaba, y guardar con ella
    /// desmarcada exportaba un widget donde solo cabía la primera tarjeta.
    /// </summary>
    [ObservableProperty] private bool _isQueuePreviewVisible = true;
    /// <summary>
    /// Las tarjetas fantasma de la cola: dónde caería cada tarjeta repetida, según los ajustes de
    /// arriba. Las recalcula UpdateQueuePreviewBoxes.
    /// </summary>
    public ObservableCollection<QueueGhostBox> QueuePreviewBoxes { get; } = new();

    // Qué se enseña en la interfaz según la pestaña activa.

    /// <summary>Los ajustes de la cola solo se ven en la pestaña Queue.</summary>
    public bool IsQueueSettingsVisible => SelectedWidgetTypeIndex == 1;
    /// <summary>Los botones de datos del nivel se ocultan en la pestaña Notification.</summary>
    public bool IsLevelDataVisible => SelectedWidgetTypeIndex != 2;
    /// <summary>
    /// El tamaño del lienzo solo se elige en Unified; en los demás se calcula al guardar.
    /// </summary>
    public bool IsCanvasSizeVisible => SelectedWidgetTypeIndex == 3;
    /// <summary>
    /// El botón del cuadro de notificación solo se ve en la pestaña Notification.
    /// </summary>
    public bool IsNotificationWidgetVisible => SelectedWidgetTypeIndex == 2;

    // --- Notificaciones: ajustes globales (van a theme.json) ---

    /// <summary>Notificaciones: cuántas se muestran a la vez como máximo.</summary>
    [ObservableProperty] private int _notificationMaxItems = 3;
    /// <summary>
    /// Notificaciones: 0 = tarjetas en columna (vertical), 1 = en fila (horizontal).
    /// </summary>
    [ObservableProperty] private int _notificationDirectionIndex = 0;
    /// <summary>Notificaciones: separación entre tarjetas, en píxeles.</summary>
    [ObservableProperty] private double _notificationSpacing = 20;
    /// <summary>
    /// Notificaciones: 0 = simultáneo (todas descuentan su tiempo a la vez), 1 = en cascada
    /// (solo descuenta la más antigua).
    /// </summary>
    [ObservableProperty] private int _notificationTimerModeIndex = 1;

    /// <summary>
    /// Si el lienzo DIBUJA las tarjetas fantasma de las notificaciones. Solo el dibujo, como
    /// IsQueuePreviewVisible.
    /// </summary>
    [ObservableProperty] private bool _isNotificationPreviewVisible = true;
    /// <summary>
    /// Las tarjetas fantasma de las notificaciones. Las recalcula UpdateNotificationPreviewBoxes.
    /// </summary>
    public ObservableCollection<QueueGhostBox> NotificationPreviewBoxes { get; } = new();

    /// <summary>
    /// Tamaño del lienzo de trabajo, en píxeles. Sirve para centrar los objetos nuevos y para
    /// que no se salgan del lienzo. No es lo mismo que el tamaño que se exporta en un tema de un
    /// solo tipo, que se calcula al guardar (ver ComputeExportedCanvasSize).
    /// </summary>
    [ObservableProperty] private double _canvasWidth = 1920;
    [ObservableProperty] private double _canvasHeight = 1080;

    // ==========================================
    // AJUSTES QUE NO SON OBJETOS DEL LIENZO
    // ==========================================
    // Estas once propiedades no son objetos del lienzo, así que no pasaban por SaveState(), que
    // es lo que marca el tema como modificado: cambiabas el espaciado o el tamaño del lienzo,
    // cerrabas, y no aparecía el aviso de cambios sin guardar. Por eso se enganchan aquí.
    //
    // El historial se guarda desde OnXChanging y NO desde OnXChanged, y es lo único delicado:
    // SaveState() significa "guarda el estado ANTES de cambiarlo" (todos los comandos del lienzo
    // lo llaman antes de tocar nada), pero OnXChanged corre DESPUÉS de asignar el valor. Desde
    // ahí, la foto llevaría ya el valor nuevo y Ctrl+Z devolvería el mismo: harían falta dos
    // pulsaciones para ver algo.
    //
    // ThemeName y ThemeAuthor NO guardan historial a propósito: son cuadros de texto, y en
    // Avalonia el binding de Text se actualiza en cada pulsación, así que escribir un nombre de 20
    // letras metería 20 entradas y enterraría el historial del lienzo. Sí van DENTRO de cada foto,
    // así que un Ctrl+Z que venga de otro cambio los restaura; lo que no hacen es crear entradas
    // propias.
    partial void OnQueueMaxItemsChanging(int value) => SaveState();
    partial void OnQueueDirectionIndexChanging(int value) => SaveState();
    partial void OnQueueSpacingChanging(double value) => SaveState();

    partial void OnNotificationMaxItemsChanging(int value) => SaveState();
    partial void OnNotificationDirectionIndexChanging(int value) => SaveState();
    partial void OnNotificationSpacingChanging(double value) => SaveState();
    partial void OnNotificationTimerModeIndexChanging(int value) => SaveState();

    partial void OnCanvasWidthChanging(double value) => SaveState();
    partial void OnCanvasHeightChanging(double value) => SaveState();

    // Y todas marcan el tema como modificado. Para las nueve de arriba es redundante (SaveState ya
    // lo hace), pero para el nombre y el autor es lo único que los marca. LoadTheme asigna varias
    // de estas y termina con IsDirty = false, así que abrir un tema no lo deja marcado.
    partial void OnQueueMaxItemsChanged(int value) => IsDirty = true;
    partial void OnQueueDirectionIndexChanged(int value) => IsDirty = true;
    partial void OnQueueSpacingChanged(double value) => IsDirty = true;

    partial void OnNotificationMaxItemsChanged(int value) => IsDirty = true;
    partial void OnNotificationDirectionIndexChanged(int value) => IsDirty = true;
    partial void OnNotificationSpacingChanged(double value) => IsDirty = true;
    partial void OnNotificationTimerModeIndexChanged(int value) => IsDirty = true;

    partial void OnCanvasWidthChanged(double value) => IsDirty = true;
    partial void OnCanvasHeightChanged(double value) => IsDirty = true;
    partial void OnThemeNameChanged(string value) => IsDirty = true;
    partial void OnThemeAuthorChanged(string value) => IsDirty = true;

    // ==========================================
    // CAMBIOS SIN GUARDAR: LA RED DE SEGURIDAD
    // ==========================================
    // Además de los ajustes de arriba, el tema queda marcado como modificado cuando cambia algo
    // de un objeto que se guarda, cuando se añaden o se quitan objetos y cuando se escribe en el
    // editor de código, venga el cambio de donde venga. Antes dependía de que cada control y cada
    // gesto llamara a SaveState, y la mitad del panel de propiedades, las flechas y los tiradores
    // de tamaño no lo hacían: guardar, retocar con ellos y cerrar perdía el retoque sin ningún
    // aviso. SaveState sigue marcándolo también, al EMPEZAR cada cambio. Abrir un tema pasa por
    // aquí, y por eso LoadTheme termina con IsDirty = false.

    /// <summary>
    /// Las propiedades de un objeto que acaban dentro del tema, en theme.json o en assets/ (las
    /// que lee GenerateThemeJson). Cambiar cualquiera de ellas marca el tema como modificado. Las
    /// demás son del propio Editor (la selección, los datos de demostración, el halo que se ve, la
    /// marquesina...) y algunas cambian solas todo el rato, así que no pueden contar.
    ///
    /// OJO: una propiedad nueva que se guarde en el tema tiene que entrar también aquí (lista de
    /// comprobación del CLAUDE.md). Si falta, cambiarla con un control que no llame a SaveState no
    /// marca el tema.
    /// </summary>
    private static readonly HashSet<string> PropiedadesQueSeGuardan = new()
    {
        // Qué es, dónde está y su grupo de vínculo.
        nameof(WidgetItemViewModel.ObjectType), nameof(WidgetItemViewModel.ObjectName),
        nameof(WidgetItemViewModel.X), nameof(WidgetItemViewModel.Y),
        nameof(WidgetItemViewModel.Width), nameof(WidgetItemViewModel.Height),
        nameof(WidgetItemViewModel.LayerIndex), nameof(WidgetItemViewModel.RotationAngle),
        nameof(WidgetItemViewModel.CropTop), nameof(WidgetItemViewModel.CropBottom),
        nameof(WidgetItemViewModel.CropLeft), nameof(WidgetItemViewModel.CropRight),
        nameof(WidgetItemViewModel.LinkedGroupId),

        // Colores, fondo, opacidad y esquinas.
        nameof(WidgetItemViewModel.TextColorHex), nameof(WidgetItemViewModel.BackgroundColorHex),
        nameof(WidgetItemViewModel.BackgroundTypeIndex), nameof(WidgetItemViewModel.ForegroundColorHex),
        nameof(WidgetItemViewModel.Transparency), nameof(WidgetItemViewModel.BorderRoundness),

        // El texto: fuente, alineación, margen, salto de línea y contorno.
        nameof(WidgetItemViewModel.FontSize), nameof(WidgetItemViewModel.FontBlur),
        nameof(WidgetItemViewModel.FontFamilyName), nameof(WidgetItemViewModel.CustomFontPath),
        nameof(WidgetItemViewModel.UseCustomFont), nameof(WidgetItemViewModel.TextAlignmentIndex),
        nameof(WidgetItemViewModel.VerticalTextAlignmentIndex), nameof(WidgetItemViewModel.TextPadding),
        nameof(WidgetItemViewModel.IsTextWrapped), nameof(WidgetItemViewModel.HasTextOutline),
        nameof(WidgetItemViewModel.TextOutlineSize), nameof(WidgetItemViewModel.TextOutlineColorHex),

        // El cuadro de notificación, y cuánto se ve cada objeto en las notificaciones.
        nameof(WidgetItemViewModel.RequesterColorHex), nameof(WidgetItemViewModel.LevelDataColorHex),
        nameof(WidgetItemViewModel.NotificationDuration),

        // Imágenes, vídeos y el halo de la cara de dificultad.
        nameof(WidgetItemViewModel.ImageFilePath), nameof(WidgetItemViewModel.StretchIndex),
        nameof(WidgetItemViewModel.VideoLoop), nameof(WidgetItemViewModel.VideoMuted),
        nameof(WidgetItemViewModel.VideoPosterPath), nameof(WidgetItemViewModel.ShowHalo),
        nameof(WidgetItemViewModel.HaloScale),
    };

    /// <summary>
    /// Va en el CollectionChanged de las cuatro colecciones de objetos (constructor). Engancha
    /// <see cref="OnPropiedadDeUnObjetoCambiada"/> a los objetos que entran en un lienzo, lo suelta
    /// de los que salen, y marca el tema: añadir, quitar o reordenar objetos también lo cambia.
    /// </summary>
    private void OnObjetosDeUnLienzoCambiados(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (WidgetItemViewModel w in e.OldItems)
                w.PropertyChanged -= OnPropiedadDeUnObjetoCambiada;

        // El -= antes del += es para no engancharlo dos veces si un objeto vuelve a entrar.
        if (e.NewItems != null)
            foreach (WidgetItemViewModel w in e.NewItems)
            {
                w.PropertyChanged -= OnPropiedadDeUnObjetoCambiada;
                w.PropertyChanged += OnPropiedadDeUnObjetoCambiada;
            }

        // Clear() no dice qué objetos había (OldItems llega vacío), así que esos se quedan
        // enganchados. No hace daño: si alguien llegara a tocar uno de esos objetos sueltos, lo
        // peor sería marcar el tema de más; y el enganche no los mantiene vivos, porque es el
        // objeto el que apunta aquí, no al revés.
        IsDirty = true;
    }

    /// <summary>
    /// Cuando cambia una propiedad de un objeto de cualquiera de los cuatro lienzos: si es de las
    /// que se guardan (<see cref="PropiedadesQueSeGuardan"/>), el tema queda marcado como
    /// modificado.
    /// </summary>
    private void OnPropiedadDeUnObjetoCambiada(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null || !PropiedadesQueSeGuardan.Contains(e.PropertyName)) return;

        // Si el panel de propiedades había preparado una foto (una tecla en uno de sus controles,
        // ver PrepararFoto), es la de antes de este cambio: ahora sí va al historial.
        if (_fotoPreparada != null && !_isRestoringState)
        {
            _undoStack.Push(_fotoPreparada);
            _redoStack.Clear();
            _fotoPreparada = null;
        }

        IsDirty = true;
    }

    /// <summary>
    /// Escribir en el editor de código también cambia el tema. Solo cuenta si el documento ha
    /// dejado de coincidir con lo que hay en memoria para ese archivo: abrir un archivo pone su
    /// texto en el documento, y eso no tiene que marcar nada; escribir, sí. theme.json no cuenta,
    /// porque no se edita a mano: se genera.
    /// </summary>
    private void OnTextoDelEditorDeCodigoCambiado(object? sender, EventArgs e)
    {
        if (IsDirty) return; // ya está marcado: no hace falta comparar en cada tecla

        var editor = CodeEditorVM;
        if (!editor.IsDocumentLoaded || editor.CurrentFileName == "theme.json") return;

        if (!editor.FileBuffers.TryGetValue(editor.CurrentFileName, out var enMemoria) ||
            enMemoria != editor.EditorDocument.Text)
        {
            IsDirty = true;
        }
    }

    // ==========================================
    // ZOOM, PANEO E IMÁGENES PREDEFINIDAS
    // ==========================================

    /// <summary>Zoom del lienzo: 1.0 = 100 %.</summary>
    [ObservableProperty] private double _canvasZoom = 1.0;

    /// <summary>Desplazamiento horizontal del lienzo (paneo), en píxeles de pantalla.</summary>
    [ObservableProperty] private double _canvasOffsetX = 0;
    /// <summary>Desplazamiento vertical del lienzo (paneo), en píxeles de pantalla.</summary>
    [ObservableProperty] private double _canvasOffsetY = 0;

    /// <summary>
    /// Las imágenes de la lista "Imágenes predefinidas": los dos fondos de la carpeta Assets del
    /// Editor y los PNG de Assets/GDassets. Se llena en el constructor.
    /// </summary>
    [ObservableProperty] private ObservableCollection<PredefinedImageItem> _predefinedImages = new();

    // ==========================================
    // DATOS DE DEMOSTRACIÓN
    // ==========================================
    // Lo que se ve en el lienzo y en la vista previa del navegador en lugar de datos reales. Rota
    // cada 3 segundos (ver el temporizador del constructor).

    /// <summary>Cuántas veces ha rotado la demostración; las listas lo usan como índice.</summary>
    private int _previewIndex = 0;

    // Caras de dificultad que rotan. Las rutas empiezan por "Assets" con mayúscula: es el nombre
    // real de la carpeta, y Linux distingue mayúsculas.
    private readonly string[] _facePaths = new[] {
        "Assets/GDassets/faces/auto.png", "Assets/GDassets/faces/unrated.png", "Assets/GDassets/faces/easy.png",
        "Assets/GDassets/faces/hard.png", "Assets/GDassets/faces/harder.png", "Assets/GDassets/faces/insane.png",
        "Assets/GDassets/faces/demon.png", "Assets/GDassets/faces/demon-easy.png", "Assets/GDassets/faces/demon-medium.png",
        "Assets/GDassets/faces/demon-hard.png", "Assets/GDassets/faces/demon-insane.png", "Assets/GDassets/faces/demon-extreme.png"
    };

    // ==========================================
    // VISTA PREVIA EN EL NAVEGADOR
    // ==========================================
    // Carpeta temporal donde se vuelca el tema para servirlo al navegador. Se crea la primera vez,
    // y es de ESTE Editor: ver OpenBrowserPreview.
    private string? _previewFolder;

    // Instante (segundos Unix) en que acaba el auto-skip de demostración. Se calcula UNA vez y se
    // deja correr: así el reloj del widget baja de verdad y se ve desaparecer al llegar a cero. Si
    // se recalculara en cada ciclo, saltaría hacia atrás cada 3 segundos.
    private long _previewSkipDeadline;

    // ==========================================
    // HALOS DE LA CARA DE DIFICULTAD
    // ==========================================
    // Los halos que rotan en modo automático. La posición 0 es "sin halo": así la demostración
    // alterna niveles normales con niveles featured, epic, legendary y mythic, como en un directo.
    private readonly string?[] _haloPaths = new string?[] {
        null,
        "Assets/GDassets/faces/featured.png",
        "Assets/GDassets/faces/epic.png",
        "Assets/GDassets/faces/legendary.png",
        "Assets/GDassets/faces/mythic.png"
    };

    // Imágenes ya leídas, por ruta, para no volver a abrir el mismo PNG cada 3 segundos. Guarda
    // también los null de las que no existen, para no reintentarlas.
    private readonly Dictionary<string, Bitmap?> _bitmapCache = new();

    /// <summary>
    /// Devuelve la imagen de una ruta relativa a la carpeta del Editor, leyéndola solo la primera
    /// vez. Null si la ruta está vacía, no existe o no se puede abrir.
    /// </summary>
    private Bitmap? GetCachedBitmap(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;
        if (_bitmapCache.TryGetValue(relativePath, out var cached)) return cached;

        Bitmap? bmp = null;
        try
        {
            string full = ResolveAssetPath(relativePath);
            if (File.Exists(full)) bmp = new Bitmap(full);
        }
        catch { bmp = null; }

        _bitmapCache[relativePath] = bmp;
        return bmp;
    }

    /// <summary>
    /// La imagen del halo que toca dibujar detrás de la cara, según la opción del panel.
    /// </summary>
    /// <param name="haloIndex">0 = automático (rota), 1 = ninguno, 2..5 = featured, epic,
    /// legendary o mythic fijo.</param>
    /// <param name="autoStep">En modo automático, el paso de la rotación.</param>
    private Bitmap? ResolveHaloBitmap(int haloIndex, int autoStep)
    {
        if (haloIndex == 1) return null;

        if (haloIndex == 0)
            return GetCachedBitmap(_haloPaths[autoStep % _haloPaths.Length]);

        int fixedIndex = haloIndex - 1; // 2 -> featured, que es la posición 1 de _haloPaths
        if (fixedIndex < 1 || fixedIndex >= _haloPaths.Length) return null;
        return GetCachedBitmap(_haloPaths[fixedIndex]);
    }

    // Los datos de demostración que rotan en los objetos (los textos de notificación van
    // traducidos al idioma del Editor).
    private readonly string[] _previewLevelNames = { "Stereo Madness", "Back On Track", "Double Dash", "Kocchi Miku Baby", "Without U", "Deadlocked", "Bloodbath", "Slaughterhouse", "Fingerdash", "Geometrical Dominator", "Dream Flower", "Future Funk" };
    private readonly string[] _previewNotificationTexts = {
        EditorI18n.Tr("Preview_NotifNotAnId"),
        EditorI18n.Tr("Preview_NotifUserLimit"),
        EditorI18n.Tr("Preview_NotifQueueFull"),
        EditorI18n.Tr("Preview_NotifTooHard"),
        EditorI18n.Tr("Preview_NotifTimeExpired"),
        EditorI18n.Tr("Preview_NotifNotFound"),
        EditorI18n.Tr("Preview_NotifAdded"),
        EditorI18n.Tr("Preview_NotifSkipping"),
        EditorI18n.Tr("Preview_NotifUnrated"),
        EditorI18n.Tr("Preview_NotifNext"),
        EditorI18n.Tr("Preview_NotifQueueEmpty"),
        EditorI18n.Tr("Preview_NotifConnectionLost"),
        EditorI18n.Tr("Preview_NotifPriority"),
        EditorI18n.Tr("Preview_NotifRequestsOpened"),
        EditorI18n.Tr("Preview_NotifRequestsPaused"),
        EditorI18n.Tr("Preview_NotifRequestIgnored")
    };

    // Reloj de las notificaciones del LIENZO: cuánto lleva la actual y cuál toca.
    private double _notifPreviewElapsed = 0;
    private int _notifTextIndex = 0;

    // Reloj PROPIO de la vista previa del navegador. Va aparte del del lienzo, y con su propio
    // índice, por dos motivos:
    //  1. El del lienzo solo corre en la pestaña Notification. El navegador enseña el widget
    //     UNIFICADO, así que tiene que recibir notificaciones estés en la pestaña que estés.
    //  2. Van a ritmos distintos. Si compartieran _notifTextIndex, el texto del lienzo daría
    //     saltos en mitad de su propio fundido cada vez que el navegador emitiera.
    private double _previewNotifElapsed = 0;
    private int _previewNotifIndex = 0;
    private int _previewNotifBurst = 0; // cuántas van de la ráfaga inicial al abrir

    // El resto de datos de demostración: del nivel, de quien lo pidió y de la plataforma.
    private readonly string[] _previewCreators = { "Danolex", "FlozWer", "Lapalomakotori", "PakeMPC", "Guamaro", "Mayerosa", "Nelog987", "EriOswong", "Jjheysonn", "DexterGVt18" };
    private readonly string[] _previewLevelIds = { "116284799", "55274070", "38912874", "44062068", "92894241", "93316755", "120285800" };
    private readonly string[] _previewRequesters = { "Nelog987", "IcedHyperion", "Brayanewe", "JackZP7w7", "Neossj", "EDRK", "KaixoGD", "PakeMPC", "Sabananas", "Security7u7" };
    private readonly string[] _previewPlatformIcons = { "Assets/GDassets/icons/twitch.png", "Assets/GDassets/icons/youtube.png", "Assets/GDassets/icons/discord.png", "Assets/GDassets/icons/kick.png", "Assets/GDassets/icons/tiktok.png", "Assets/GDassets/icons/facebook.png", "Assets/GDassets/icons/steam.png", "Assets/GDassets/icons/global.png" };
    private readonly string[] _previewPlatformNames = { "Twitch", "Youtube", "discord", "Facebook", "Kick", "Tiktok", "Trovo", "Global", "Steam" };
    private readonly string[] _previewRequestStatus = { EditorI18n.Tr("Preview_RequestsEnabled"), EditorI18n.Tr("Preview_RequestsDisabled") };
    private readonly string[] _previewTimers = { "00:30", "20:00", "01:20:10", "00:10", "00:09", "00:08", "10:00" };
    private readonly string[] _previewRateTexts = { "Easy", "Normal", "Easy Demon", "Hard Demon", "Extreme Demon", "Insane Demon", "Hard", "Harder", "Insane", "Demon" };
    private readonly string[] _previewStars = { "10", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
    private readonly string[] _previewDownloads = { "1,000,000", "10,000,000", "2,000", "20", "300,000", "12,000" };
    private readonly string[] _previewLikes = { "2,000", "20", "300,000", "12,000", "1,000,000" };
    private readonly string[] _previewLikeIcons = { "Assets/GDassets/icons/like.png", "Assets/GDassets/icons/dislike.png" };
    private readonly string[] _previewStarIcons = { "Assets/GDassets/icons/star.png", "Assets/GDassets/icons/moon.png" };
    private readonly string[] _previewOrbs = { "20", "200", "500" };
    // "Plat" está para que la demostración enseñe también la luna: el icono de estrella decide
    // entre estrella y luna por este mismo dato (si contiene "PLAT"), así que sin un valor de
    // plataformas nunca se vería la luna.
    private readonly string[] _previewDurations = { "Tiny", "Long", "XL", "Plat" };
    private readonly string[] _previewAredlPos = { "1", "20", "100", "300", "1000", "1050" };
    private readonly string[] _previewGddlRate = { "1", "10.5", "20.1", "31", "15.7" };

    /// <summary>
    /// Convierte una ruta relativa de los recursos del Editor ("Assets/...") en la ruta completa
    /// junto al ejecutable. Si no existe ahí, devuelve la ruta tal cual, y quien la use se
    /// encontrará con que no existe (todos lo comprueban).
    ///
    /// Los recursos del Editor van SUELTOS junto al ejecutable: el .csproj los copia con
    /// CopyToOutputDirectory, en desarrollo, en Release y en el paquete.
    ///
    /// Antes había dos intentos más (el directorio de trabajo y un "../../.." desde él), y esos
    /// atajos escondieron un fallo durante meses: desde bin/Debug o bin/Release, "../../.." llega
    /// a la carpeta del PROYECTO, donde sí están los archivos, así que en el IDE todo funcionaba y
    /// en cualquier instalación el Editor se quedaba sin imágenes y guardaba temas sin dislike.png
    /// ni moon.png. Con un solo sitio donde buscar, desarrollo y paquete se comportan IGUAL: si la
    /// copia se rompe algún día, se rompe también en el IDE, que es donde se ve.
    /// </summary>
    private string ResolveAssetPath(string path)
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath)) return fullPath;

        // Se devuelve tal cual: todos los que llaman comprueban después File.Exists o
        // Directory.Exists, y así se quedan sin el recurso sin que nada falle.
        return path;
    }

    // ==========================================
    // CONSTRUCTOR Y TEMPORIZADORES
    // ==========================================

    /// <summary>
    /// Prepara el Editor: llena la lista de imágenes predefinidas, arranca los dos
    /// temporizadores de la demostración y engancha los avisos que mantienen al día el menú de
    /// pestañas.
    /// </summary>
    public MainWindowViewModel()
    {
        CurrentSidePanel = this;

        // Imágenes predefinidas: los dos fondos de Assets y todo lo de Geometry Dash
        // (Assets/GDassets: caras, iconos de plataforma, de estadísticas y de las listas). El
        // resto de PNG de Assets son los iconos de los botones del propio Editor (save.png,
        // trash.png, plus.png...), que no pintan nada en un tema; antes también salían en la
        // lista, porque se cogían todos los PNG de Assets.
        string assetsDir = ResolveAssetPath("Assets");
        if (Directory.Exists(assetsDir)) {
            var archivos = new List<string>();
            foreach (var fondo in new[] { "16-9BG.png", "placeholder_bg.png" })
            {
                string ruta = Path.Combine(assetsDir, fondo);
                if (File.Exists(ruta)) archivos.Add(ruta);
            }

            string carpetaGD = Path.Combine(assetsDir, "GDassets");
            if (Directory.Exists(carpetaGD))
                archivos.AddRange(Directory.GetFiles(carpetaGD, "*.png", SearchOption.AllDirectories));

            foreach (var file in archivos) {
                PredefinedImages.Add(new PredefinedImageItem(Path.GetFileName(file), file));
            }
        }

        // Temporizador de la demostración, cada 3 segundos: pone el siguiente dato de muestra en
        // cada objeto que muestra datos del nivel, y envía el mismo estado a la vista previa del
        // navegador si está abierta.
        var previewTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        previewTimer.Tick += (s, e) =>
        {
            _previewIndex++;
            // El siguiente elemento de una lista, dando la vuelta al llegar al final.
            string GetPreview(string[] arr) => arr[_previewIndex % arr.Length];

            // Las imágenes salen de la caché. (Antes se creaba un Bitmap nuevo cada 3 segundos sin
            // liberar el anterior, y en una sesión larga eso se iba comiendo la memoria.)
            Bitmap? nextFaceBmp = GetCachedBitmap(_facePaths[_previewIndex % _facePaths.Length]);
            Bitmap? nextIconBmp = GetCachedBitmap(_previewPlatformIcons[_previewIndex % _previewPlatformIcons.Length]);
            Bitmap? nextLikeIconBmp = GetCachedBitmap(_previewLikeIcons[_previewIndex % _previewLikeIcons.Length]);

            // La estrella o luna NO rota por su cuenta: se decide con la misma duración que se
            // está enseñando en ese ciclo. Si rotara sola se vería la luna junto a un "XL" y
            // parecería un fallo, cuando la regla real es que la luna es de los niveles de
            // plataformas.
            bool esPlatPreview = _previewDurations[_previewIndex % _previewDurations.Length]
                .ToUpperInvariant().Contains("PLAT");
            Bitmap? nextStarIconBmp = GetCachedBitmap(esPlatPreview ? _previewStarIcons[1] : _previewStarIcons[0]);

            // Los cuatro lienzos, también el Unified. (Antes se saltaba el Unified, y un objeto
            // creado directamente en él no cambiaba de datos de demostración.)
            foreach (var widget in TodosLosObjetos())
            {
                // Si el usuario apagó la vista previa de este objeto en Propiedades, se salta.
                if (!widget.IsPreviewEnabled) continue;

                switch (widget.ObjectType)
                {
                    case WidgetObjectType.LevelName: widget.PreviewText = GetPreview(_previewLevelNames); break;
                    case WidgetObjectType.CreatorName: widget.PreviewText = GetPreview(_previewCreators); break;
                    case WidgetObjectType.LevelId: widget.PreviewText = GetPreview(_previewLevelIds); break;
                    case WidgetObjectType.SentBy: widget.PreviewText = GetPreview(_previewRequesters); break;
                    case WidgetObjectType.PlatformName: widget.PreviewText = GetPreview(_previewPlatformNames); break;
                    case WidgetObjectType.RequestStatus: widget.PreviewText = GetPreview(_previewRequestStatus); break;
                    case WidgetObjectType.Timer: widget.PreviewText = GetPreview(_previewTimers); break;
                    case WidgetObjectType.RateText: widget.PreviewText = GetPreview(_previewRateTexts); break;
                    case WidgetObjectType.Stars: widget.PreviewText = GetPreview(_previewStars); break;
                    case WidgetObjectType.Downloads: widget.PreviewText = GetPreview(_previewDownloads); break;
                    case WidgetObjectType.LikesAndDislikes: widget.PreviewText = GetPreview(_previewLikes); break;
                    case WidgetObjectType.Orbs: widget.PreviewText = GetPreview(_previewOrbs); break;
                    case WidgetObjectType.Duration: widget.PreviewText = GetPreview(_previewDurations); break;
                    case WidgetObjectType.AredlPos: widget.PreviewText = GetPreview(_previewAredlPos); break;
                    case WidgetObjectType.GddlRate: widget.PreviewText = GetPreview(_previewGddlRate); break;

                    case WidgetObjectType.RateFace: widget.PreviewImageBitmap = nextFaceBmp; break;
                    case WidgetObjectType.PlatformIcon: widget.PreviewImageBitmap = nextIconBmp; break;
                    case WidgetObjectType.LikeIcon: widget.PreviewImageBitmap = nextLikeIconBmp; break;
                    case WidgetObjectType.StarIcon: widget.PreviewImageBitmap = nextStarIconBmp; break;
                }
            }
            // Si hay una pestaña de vista previa abierta, recibe los mismos datos que el lienzo.
            if (PreviewServerService.Instance.IsRunning)
                _ = PreviewServerService.Instance.BroadcastAsync(BuildPreviewStateJson());
        };
        // Temporizador rápido, cada 50 ms: recalcula las tarjetas fantasma, anima los fundidos de
        // las notificaciones y las marquesinas del lienzo, rota los halos y refresca theme.json
        // si está abierto en el editor de código.
        var queuePreviewTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        queuePreviewTimer.Tick += (s, e) => {
            UpdateQueuePreviewBoxes();
            UpdateNotificationPreviewBoxes();
            UpdateNotificationPreviewFades();      // fundidos en el LIENZO (solo en Notification)
            UpdatePreviewNotificationBroadcast();  // envíos al NAVEGADOR (en cualquier pestaña)
            UpdateMarqueeAnimations();
            UpdateHaloBitmaps();
            RefreshLiveThemeJson();                // theme.json en vivo, si se está viendo
        };
        queuePreviewTimer.Start();

        previewTimer.Start();

        // IsUnifiedTabVisible se calcula sobre las cuatro colecciones, y añadir o quitar objetos no
        // avisa a nadie por su cuenta: sin esto, la pestaña Unified no aparecería hasta que otra
        // cosa refrescara el binding por casualidad.
        foreach (var coleccion in new[] { PlayingObjects, QueueObjects, NotificationObjects, UnifiedObjects })
        {
            // Durante un Ctrl+Z o Ctrl+Y no: la colección pasa un momento por vacía, eso puede
            // esconder la pestaña Unified y echarte de ella a mitad de la restauración.
            // AplicarFoto recalcula el menú una sola vez, al final, con el estado ya completo.
            coleccion.CollectionChanged += (_, _) =>
            {
                if (!_isRestoringState) RefreshWidgetTypeOptions();
            };

            // La red de seguridad de los cambios sin guardar (ver PropiedadesQueSeGuardan).
            coleccion.CollectionChanged += OnObjetosDeUnLienzoCambiados;
        }

        // Y la del editor de código. EditorDocument no se sustituye nunca (solo cambia su Text),
        // así que basta con engancharlo una vez.
        CodeEditorVM.EditorDocument.TextChanged += OnTextoDelEditorDeCodigoCambiado;
    }

    // ==========================================
    // PANELES, ZOOM Y SELECCIÓN
    // ==========================================

    /// <summary>Muestra la caja de herramientas y el lienzo.</summary>
    [RelayCommand]
    private void ShowToolbox()
    {
        CurrentSidePanel = this;
        IsCanvasVisible = true;
        IsCodeEditorVisible = false;
    }

    /// <summary>Muestra el panel de propiedades y el lienzo.</summary>
    [RelayCommand]
    private void ShowProperties()
    {
        CurrentSidePanel = PropertiesVM;
        IsCanvasVisible = true;
        IsCodeEditorVisible = false;
    }

    /// <summary>
    /// Muestra el editor de código en lugar del lienzo, con el archivo que estuviera abierto (si
    /// era theme.json, regenerado).
    /// </summary>
    [RelayCommand]
    private void ShowCodeEditor()
    {
        // Se vuelve a abrir el archivo actual para que se actualice (theme.json se regenera).
        OpenCodeFile(CodeEditorVM.CurrentFileName == "theme.json" ? "theme.json" : CodeEditorVM.CurrentFileName);

        CurrentSidePanel = CodeEditorVM;
        IsCanvasVisible = false;
        IsCodeEditorVisible = true;
    }

    /// <summary>
    /// Acerca el lienzo un 10 %, hasta el 300 %: los mismos límites que Ctrl+rueda
    /// (MainWindow.OnCanvasPointerWheelChanged). Antes el botón "+" no tenía tope.
    /// </summary>
    [RelayCommand] private void ZoomIn() { CanvasZoom = Math.Clamp(CanvasZoom + 0.1, 0.2, 3.0); }
    /// <summary>Aleja el lienzo un 10 %, hasta el 20 %.</summary>
    [RelayCommand] private void ZoomOut() { CanvasZoom = Math.Clamp(CanvasZoom - 0.1, 0.2, 3.0); }

    /// <summary>Selecciona todos los objetos del lienzo actual (Ctrl+A).</summary>
    [RelayCommand]
    private void SelectAllObjects()
    {
        foreach (var w in CurrentCanvasObjects) w.IsSelected = true;
        UpdateSelection();
    }

    /// <summary>
    /// Las fuentes instaladas en el sistema, por orden alfabético. Es la lista del panel de
    /// propiedades.
    /// </summary>
    public System.Collections.Generic.IEnumerable<string> SystemFonts => FontManager.Current.SystemFonts.Select(f => f.Name).OrderBy(n => n);

    /// <summary>
    /// Abre el diálogo para elegir un archivo de fuente (.ttf u .otf) para el objeto seleccionado,
    /// y lo activa como su fuente propia.
    /// </summary>
    /// <param name="parentWindow">Ventana dueña del diálogo.</param>
    [RelayCommand]
    private async Task SelectCustomFont(Window parentWindow)
    {
        if (parentWindow == null || PropertiesVM.SelectedWidget == null) return;
        var files = await parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EditorI18n.Tr("UI_PickFontTitle"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(EditorI18n.Tr("UI_FilterFonts")) { Patterns = new[] { "*.ttf", "*.otf" } } }
        });

        if (files != null && files.Count > 0)
        {
            // El paso del historial, solo si se eligió un archivo: cancelar el diálogo no cambia
            // nada. (Antes iba antes de abrirlo.)
            SaveState();
            PropertiesVM.SelectedWidget.CustomFontPath = files[0].Path.LocalPath;
            PropertiesVM.SelectedWidget.UseCustomFont = true; // se activa la casilla sola
        }
    }

    // ==========================================
    // LOS LIENZOS
    // ==========================================
    // Un lienzo por pestaña, cada uno con sus objetos. Una colección nueva hay que darla de alta
    // en varios sitios (lista de comprobación del CLAUDE.md): ColeccionDe, el CollectionChanged
    // del constructor, el Clear() de LoadTheme, TodosLosObjetos y GenerateThemeJson, entre otros.

    /// <summary>Objetos del widget del nivel en juego.</summary>
    public ObservableCollection<WidgetItemViewModel> PlayingObjects { get; } = new();
    /// <summary>Objetos de la plantilla de tarjeta de la cola.</summary>
    public ObservableCollection<WidgetItemViewModel> QueueObjects { get; } = new();
    /// <summary>Objetos de la plantilla de tarjeta de las notificaciones.</summary>
    public ObservableCollection<WidgetItemViewModel> NotificationObjects { get; } = new();
    /// <summary>
    /// Objetos que solo existen en el widget unificado. No se ven en las páginas playing, queue y
    /// notification, porque engine.js filtra por canvasOrigin en las páginas individuales; en la
    /// unificada se dibujan como objetos fijos del lienzo.
    /// </summary>
    public ObservableCollection<WidgetItemViewModel> UnifiedObjects { get; } = new();

    /// <summary>Los objetos del lienzo de la pestaña que se está mirando.</summary>
    public ObservableCollection<WidgetItemViewModel> CurrentCanvasObjects => ColeccionDe(SelectedWidgetTypeIndex);

    /// <summary>
    /// La colección de cualquier pestaña, no solo de la que se está mirando. La usa el historial:
    /// una foto tiene que volver a SU lienzo, aunque ya estés en otro.
    /// </summary>
    private ObservableCollection<WidgetItemViewModel> ColeccionDe(int pestana) => pestana switch
    {
        0 => PlayingObjects,
        1 => QueueObjects,
        2 => NotificationObjects,
        3 => UnifiedObjects,
        _ => PlayingObjects
    };

    /// <summary>
    /// Si hay que enseñar la pestaña Unified: cuando el tema ya es unificado (objetos en más de un
    /// lienzo) y TAMBIÉN mientras queden objetos en el lienzo Unified, aunque el tema haya dejado
    /// de serlo. Sin esa segunda parte, borrar objetos hasta dejar un solo lienzo escondería la
    /// pestaña con trabajo dentro y sin forma de llegar a él.
    /// </summary>
    public bool IsUnifiedTabVisible =>
        ((PlayingObjects.Count > 0 ? 1 : 0) + (QueueObjects.Count > 0 ? 1 : 0) + (NotificationObjects.Count > 0 ? 1 : 0)) > 1
        || UnifiedObjects.Count > 0;

    /// <summary>
    /// Las opciones del menú "Tipo de widget". Es una lista de verdad, y no cuatro elementos fijos
    /// en el XAML, porque el ComboBox de Avalonia NO oculta dentro del desplegable un elemento con
    /// IsVisible=false: "Unified" seguía saliendo aunque el binding dijera que no.
    ///
    /// Los índices son estables porque "Unified" va siempre la última: 0, 1 y 2 no se mueven
    /// nunca, así que SelectedWidgetTypeIndex == 3 significa siempre lo mismo.
    /// </summary>
    public ObservableCollection<string> WidgetTypeOptions { get; } = new() { EditorI18n.Tr("UI_WidgetType_Playing"), EditorI18n.Tr("UI_WidgetType_Queue"), EditorI18n.Tr("UI_WidgetType_Notification") };

    /// <summary>
    /// Pone o quita la opción "Unified" del menú según <see cref="IsUnifiedTabVisible"/>, y
    /// actualiza los objetos de fondo del lienzo Unified.
    /// </summary>
    public void RefreshWidgetTypeOptions()
    {
        OnPropertyChanged(nameof(IsUnifiedTabVisible));

        bool debeEstar = IsUnifiedTabVisible;
        bool yaEsta = WidgetTypeOptions.Count > 3;

        if (debeEstar && !yaEsta)
        {
            WidgetTypeOptions.Add(EditorI18n.Tr("UI_WidgetType_Unified"));
        }
        else if (!debeEstar && yaEsta)
        {
            // Primero se saca al usuario de la pestaña y DESPUÉS se quita la opción. Al revés, el
            // ComboBox se queda sin selección (-1) y el lienzo sin colección que enseñar.
            if (SelectedWidgetTypeIndex == 3) SelectedWidgetTypeIndex = 0;
            WidgetTypeOptions.RemoveAt(3);
        }

        RefreshUnifiedGhosts();
    }

    /// <summary>
    /// Los objetos de los otros tres lienzos, que se ven de fondo mientras se compone en Unified.
    /// Son las MISMAS instancias, no copias: si mueves algo en la pestaña Queue y vuelves a
    /// Unified, el fantasma ya está en su sitio sin reconstruir nada.
    /// </summary>
    public ObservableCollection<WidgetItemViewModel> UnifiedGhosts { get; } = new();

    /// <summary>
    /// Vuelve a llenar <see cref="UnifiedGhosts"/> con los objetos de los otros tres lienzos, o la
    /// deja vacía si no se está en la pestaña Unified.
    /// </summary>
    public void RefreshUnifiedGhosts()
    {
        UnifiedGhosts.Clear();

        // Fuera del lienzo unificado no hay nada que enseñar, y dejar la lista vacía evita que el
        // ItemsControl siga enlazado a objetos que no se están viendo.
        if (SelectedWidgetTypeIndex != 3) return;

        foreach (var w in PlayingObjects.Concat(QueueObjects).Concat(NotificationObjects))
            UnifiedGhosts.Add(w);
    }

    /// <summary>
    /// Doble clic en el lienzo Unified: busca un objeto de fondo en esas coordenadas y, si lo hay,
    /// salta a su pestaña con ese objeto ya seleccionado. Devuelve si ha saltado, para que la
    /// vista sepa si el doble clic se ha consumido.
    /// </summary>
    /// <param name="x">Coordenada horizontal, en píxeles del lienzo.</param>
    /// <param name="y">Coordenada vertical, en píxeles del lienzo.</param>
    public bool JumpToGhostAt(double x, double y)
    {
        if (SelectedWidgetTypeIndex != 3) return false;

        // Gana el que se dibuja ENCIMA, que es el que el usuario cree estar tocando: primero por
        // capa y, a igualdad de capa, el último de la lista (se pinta después). El recorte y la
        // rotación no se tienen en cuenta: para un gesto de "llévame ahí", el rectángulo sin rotar
        // es suficiente.
        var objetivo = UnifiedGhosts
            .Select((g, i) => (Ghost: g, Indice: i))
            .Where(t => x >= t.Ghost.X && x <= t.Ghost.X + t.Ghost.Width &&
                        y >= t.Ghost.Y && y <= t.Ghost.Y + t.Ghost.Height)
            .OrderByDescending(t => t.Ghost.LayerIndex)
            .ThenByDescending(t => t.Indice)
            .Select(t => t.Ghost)
            .FirstOrDefault();

        if (objetivo == null) return false;

        int destino = PlayingObjects.Contains(objetivo) ? 0
            : QueueObjects.Contains(objetivo) ? 1
            : NotificationObjects.Contains(objetivo) ? 2
            : -1;

        if (destino < 0) return false;

        // Ojo con el orden: al cambiar de pestaña se dispara RefreshUnifiedGhosts y la lista de
        // fantasmas se vacía. Por eso el objetivo se busca ANTES de tocar el índice.
        SelectedWidgetTypeIndex = destino;

        foreach (var w in CurrentCanvasObjects) w.IsSelected = false;
        objetivo.IsSelected = true;
        UpdateSelection();

        return true;
    }

    // Al cambiar de pestaña hay que rehacer los objetos de fondo. CommunityToolkit genera este
    // método parcial a partir de [ObservableProperty] _selectedWidgetTypeIndex.
    partial void OnSelectedWidgetTypeIndexChanged(int value) => RefreshUnifiedGhosts();

    /// <summary>Si se está editando el lienzo unificado. Lo usan el panel y el lienzo.</summary>
    public bool IsUnifiedCanvasActive => SelectedWidgetTypeIndex == 3;

    /// <summary>
    /// Las tarjetas fantasma de la cola se ven en su pestaña y también en Unified, que es donde
    /// tiene sentido ver la composición entera.
    /// </summary>
    public bool IsQueueGhostsVisible => SelectedWidgetTypeIndex == 1 || SelectedWidgetTypeIndex == 3;
    /// <summary>Lo mismo para las tarjetas fantasma de las notificaciones.</summary>
    public bool IsNotifGhostsVisible => SelectedWidgetTypeIndex == 2 || SelectedWidgetTypeIndex == 3;

    // ==========================================
    // AÑADIR Y BORRAR OBJETOS
    // ==========================================

    /// <summary>
    /// Añade al lienzo actual un objeto nuevo del tipo indicado, con su tamaño e imagen por
    /// defecto, centrado en la parte del lienzo que se está viendo. Lo usan los botones de la caja
    /// de herramientas.
    /// </summary>
    /// <param name="typeString">Nombre del tipo, tal como está en WidgetObjectType ("Text",
    /// "LevelName"...). Un nombre desconocido no hace nada.</param>
    [RelayCommand]
    private void AddWidgetObject(string typeString)
    {
        if (Enum.TryParse<WidgetObjectType>(typeString, out var type))
        {
            SaveState();

            var newItem = new WidgetItemViewModel
            {
                ObjectType = type,
                ObjectName = EditorI18n.Tr("UI_Obj_" + typeString),
                Width = 250, Height = 60 // la posición se calcula más abajo
            };

            // Imágenes y vídeos: sin fondo, y cada tipo con su tamaño y su imagen de partida.
            if (newItem.IsMedia)
            {
                newItem.BackgroundTypeIndex = 1;

                // El fondo del nivel: 16:9, con la imagen por defecto del Editor.
                if (type == WidgetObjectType.LevelBackground)
                {
                    newItem.Width = 320; newItem.Height = 180;
                    string bgPath = ResolveAssetPath("Assets/16-9BG.png");
                    if (File.Exists(bgPath)) {
                        newItem.ImageFilePath = bgPath;
                        newItem.ImageBitmap = new Avalonia.Media.Imaging.Bitmap(bgPath);
                    }
                }
                // La cara de dificultad, con la primera cara de la demostración.
                else if (type == WidgetObjectType.RateFace)
                {
                    newItem.Width = 120; newItem.Height = 120;
                    string facePath = ResolveAssetPath(_facePaths[0]);
                    if (File.Exists(facePath)) {
                        newItem.ImageFilePath = facePath;
                        newItem.ImageBitmap = new Avalonia.Media.Imaging.Bitmap(facePath);
                    }
                    // El halo arranca en modo automático.
                    newItem.HaloIndex = 0;
                    newItem.HaloBitmap = ResolveHaloBitmap(0, _previewIndex);
                }
                else if (type == WidgetObjectType.Video)
                {
                    // El vídeo nace en 16:9, que es lo que casi siempre se va a poner.
                    newItem.Width = 320; newItem.Height = 180;
                }
                // El icono de like y el de estrella nacen con su imagen fija.
                else if (type == WidgetObjectType.LikeIcon)
                {
                    newItem.Width = 60; newItem.Height = 60;
                    string iconPath = ResolveAssetPath("Assets/GDassets/icons/like.png");
                    if (File.Exists(iconPath)) {
                        newItem.ImageFilePath = iconPath;
                        newItem.ImageBitmap = new Avalonia.Media.Imaging.Bitmap(iconPath);
                    }
                }
                else if (type == WidgetObjectType.StarIcon)
                {
                    newItem.Width = 60; newItem.Height = 60;
                    string starPath = ResolveAssetPath("Assets/GDassets/icons/star.png");
                    if (File.Exists(starPath)) {
                        newItem.ImageFilePath = starPath;
                        newItem.ImageBitmap = new Avalonia.Media.Imaging.Bitmap(starPath);
                    }
                }
                // El resto (imagen, icono de plataforma...): un cuadrado vacío.
                else
                {
                    newItem.Width = 150; newItem.Height = 150;
                }
            }

            // Se coloca en el centro de la parte del lienzo que se está viendo:
            // 1. El centro visual de la pantalla, descontando el paneo.
            double visualCenterX = (CanvasWidth / 2.0) - CanvasOffsetX;
            double visualCenterY = (CanvasHeight / 2.0) - CanvasOffsetY;

            // 2. Se deshace el zoom y se centra el objeto en ese punto.
            double targetX = (visualCenterX / CanvasZoom) - (newItem.Width / 2.0);
            double targetY = (visualCenterY / CanvasZoom) - (newItem.Height / 2.0);

            // 3. Y se limita para que no nazca fuera del lienzo: entre 0 y el borde menos su
            //    propio tamaño.
            newItem.X = Math.Clamp(targetX, 0, Math.Max(0, CanvasWidth - newItem.Width));
            newItem.Y = Math.Clamp(targetY, 0, Math.Max(0, CanvasHeight - newItem.Height));

            CurrentCanvasObjects.Add(newItem);
        }
    }

    /// <summary>
    /// Borra los objetos seleccionados del lienzo actual (tecla Supr) y vuelve a la caja de
    /// herramientas.
    /// </summary>
    [RelayCommand]
    private void DeleteSelectedObject()
    {
        // 1. Todos los objetos seleccionados.
        var selected = CurrentCanvasObjects.Where(w => w.IsSelected).ToList();

        if (selected.Count > 0)
        {
            SaveState(); // foto para el Ctrl+Z, antes de tocar nada

            // 2. Se quitan uno por uno.
            foreach (var w in selected)
            {
                CurrentCanvasObjects.Remove(w);
            }

            // 3. Sin selección, el panel de propiedades no tiene nada que enseñar.
            UpdateSelection();
            ShowToolboxCommand.Execute(null);
        }
    }

    // ==========================================
    // GUARDADO
    // ==========================================

    /// <summary>
    /// Ruta del último guardado, o null si el tema aún no se ha guardado. Es lo que permite que
    /// Ctrl+S guarde sin volver a preguntar.
    ///
    /// Es una propiedad observable y no un campo porque el aviso de guardado depende de ella, y
    /// como campo nadie avisaba al cambiarla. WriteThemeZip pone IsDirty = false ANTES de que el
    /// llamador apunte la ruta, así que tras el PRIMER guardado de un tema el aviso se calculaba
    /// con la ruta todavía vacía y se quedaba en blanco en vez de decir "Guardado". Por eso se
    /// escribe siempre a través de LastSavePath, nunca en el campo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatusText))]
    [NotifyPropertyChangedFor(nameof(SaveStatusTip))]
    private string? _lastSavePath;

    /// <summary>
    /// Si hay cambios sin guardar. Lo usan el aviso de guardado y el diálogo de cerrar.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatusText))]
    private bool _isDirty;

    /// <summary>
    /// El aviso de guardado de la ventana: "sin guardar", "guardado" o nada si es un tema nuevo.
    /// </summary>
    public string SaveStatusText => IsDirty ? EditorI18n.Tr("UI_SaveStatusDirty") : (LastSavePath == null ? "" : EditorI18n.Tr("UI_SaveStatusSaved"));

    /// <summary>
    /// Dónde se guardó, como ToolTip del aviso. El primer Ctrl+S de un tema nuevo guarda sin
    /// diálogo en la carpeta de temas, y sin esto no había forma de saber dónde. Null = sin
    /// ToolTip.
    /// </summary>
    public string? SaveStatusTip => LastSavePath;

    /// <summary>
    /// El nombre de archivo que se propone al guardar: el nombre del tema, con los caracteres que
    /// el sistema no admite cambiados por "_". (Antes proponía siempre el mismo nombre fijo.)
    /// </summary>
    private string SuggestedThemeFileName()
    {
        string baseName = string.IsNullOrWhiteSpace(ThemeName) ? EditorI18n.Tr("Default_ThemeName") : ThemeName.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        return baseName + ".zip";
    }

    /// <summary>
    /// La carpeta donde SheepGDRequest BUSCA los temas. Se crea si no existe: un diálogo que
    /// empieza en una carpeta inexistente no sirve de nada, y en ella tampoco se puede guardar.
    ///
    /// Se calcula con el AppPaths de SheepGDRequest (el mismo archivo, enlazado en el .csproj)
    /// porque la carpeta de datos es de todo el ecosistema y no de cada programa. Aquí solo se LEE:
    /// llamar a AppPaths.Initialize() desde el Editor migraría y prepararía los datos tomando como
    /// origen la carpeta del ejecutable del Editor, que es el sitio equivocado.
    ///
    /// Devuelve null si no se pudo preparar, y entonces quien la llama sigue como si no hubiera
    /// carpeta, en lugar de fallar.
    /// </summary>
    private static string? CarpetaDeTemas()
    {
        try
        {
            Directory.CreateDirectory(SheepGDRequest.Services.AppPaths.WidgetThemesDir);
            return SheepGDRequest.Services.AppPaths.WidgetThemesDir;
        }
        catch (Exception ex)
        {
            Console.WriteLine(EditorI18n.Trf("Log_ThemesFolderError", ex.Message));
            return null;
        }
    }

    /// <summary>
    /// La misma carpeta, como el IStorageFolder que piden los diálogos de Avalonia, o null si no
    /// se pudo.
    ///
    /// Dos trampas fáciles de repetir:
    ///  - En Avalonia 12, TryGetFolderFromPathAsync pide un Uri, no un string.
    ///  - El Uri se construye con new Uri(ruta), NUNCA pegando "file://" delante: en Windows eso da
    ///    dos barras, la unidad acaba interpretada como el servidor de la URI, y no lanza ningún
    ///    error, así que falla sin avisar.
    ///
    /// Y para no perder tiempo depurándolo: en Linux, el portal XDG IGNORA la carpeta inicial que
    /// se le sugiere al diálogo. Esto ayuda en Windows y macOS; lo que funciona en los tres es el
    /// primer Ctrl+S de SaveTheme, que guarda directamente en la carpeta de temas.
    /// </summary>
    private static async Task<IStorageFolder?> CarpetaDeTemasComoOrigen(Window ventana)
    {
        string? carpeta = CarpetaDeTemas();
        if (carpeta == null) return null;

        try { return await ventana.StorageProvider.TryGetFolderFromPathAsync(new Uri(carpeta)); }
        catch { return null; }
    }

    /// <summary>
    /// Guardar (Ctrl+S): guarda donde ya se guardó. La primera vez de un tema nuevo lo guarda
    /// directamente en la carpeta de temas de SheepGDRequest, y solo si ahí ya hay un archivo con
    /// ese nombre abre "Guardar como".
    /// </summary>
    /// <param name="parentWindow">Ventana dueña del diálogo, si hay que abrirlo.</param>
    [RelayCommand]
    private async Task SaveTheme(Window parentWindow)
    {
        if (string.IsNullOrEmpty(LastSavePath))
        {
            // Primer Ctrl+S de un tema nuevo: va DIRECTO a la carpeta de temas, sin diálogo. No es
            // por comodidad: en Linux el portal XDG ignora la carpeta inicial que se le sugiere al
            // diálogo, así que este es el único camino que cae en el sitio correcto en los tres
            // sistemas.
            string? carpeta = CarpetaDeTemas();
            if (carpeta != null)
            {
                string destino = Path.Combine(carpeta, SuggestedThemeFileName());

                // Si ya hay un tema con ese nombre NO se pisa: se abre el diálogo para que el
                // usuario decida. Un atajo que machaca otro tema sin preguntar es justo lo que no
                // se quiere, y el nombre sale del nombre del tema, que se repite con facilidad.
                if (!File.Exists(destino))
                {
                    // Si falla no se abre el diálogo: WriteThemeZip ya ha puesto el error en
                    // pantalla, y encadenar un diálogo encima solo confunde. LastSavePath se queda
                    // en null, así que el siguiente Ctrl+S vuelve a intentarlo.
                    if (await WriteThemeZip(destino)) LastSavePath = destino;
                    return;
                }
            }

            await SaveThemeAs(parentWindow);
            return;
        }

        await WriteThemeZip(LastSavePath);
    }

    /// <summary>
    /// Guardar como: pide dónde guardar, empezando en la carpeta de temas, y después abre
    /// theme.json en el editor de código.
    /// </summary>
    /// <param name="parentWindow">Ventana dueña del diálogo.</param>
    [RelayCommand]
    private async Task SaveThemeAs(Window parentWindow)
    {
        if (parentWindow == null) return;

        var file = await parentWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = EditorI18n.Tr("UI_SaveThemeTitle"),
            DefaultExtension = ".zip",
            SuggestedFileName = SuggestedThemeFileName(),
            SuggestedStartLocation = await CarpetaDeTemasComoOrigen(parentWindow),
            FileTypeChoices = new[] { new FilePickerFileType(EditorI18n.Tr("UI_FilterThemeZip")) { Patterns = new[] { "*.zip" } } }
        });

        if (file == null) return;   // el usuario cerró el diálogo sin guardar

        // La ruta se apunta SOLO si el guardado sale bien. Si se apuntara antes, un fallo dejaría
        // a Ctrl+S apuntando a un sitio donde nunca se llegó a guardar nada.
        string destino = file.Path.LocalPath;
        if (!await WriteThemeZip(destino)) return;   // el error ya está en pantalla
        LastSavePath = destino;

        // Solo "Guardar como" salta al editor de código. Ctrl+S no: sacarte del lienzo cada vez que
        // guardas sería insoportable, ahora que se guarda a menudo.
        CurrentSidePanel = CodeEditorVM;
        IsCanvasVisible = false;
        IsCodeEditorVisible = true;
        OpenCodeFile("theme.json");
    }

    /// <summary>
    /// Escribe el tema como ZIP en la ruta indicada: theme.json, los archivos de código y los
    /// recursos en assets/. No sabe nada de errores ni de mensajes: de eso se encarga
    /// WriteThemeZip, que la llama con una ruta temporal. Por eso aquí NO se marca IsDirty ni se
    /// escribe ningún mensaje: hasta que no se renombre encima del destino, este guardado todavía
    /// puede fallar.
    /// </summary>
    /// <param name="destino">Ruta del archivo que se crea (se sobrescribe si existe).</param>
    private async Task EscribirZip(string destino)
    {
        // 1. Con qué nombre viaja cada recurso, decidido UNA vez. Lo leen el theme.json y el paso C
        //    de abajo, así que no pueden contradecirse. Va antes del primer await: si se toca algo
        //    mientras se escribe el ZIP, el JSON y los recursos siguen siendo del mismo instante.
        var nombres = CalcularNombresDeAssets();

        // 2. El theme.json sale de GenerateThemeJson(), que es la única fuente de verdad.
        string jsonText = GenerateThemeJson(nombres);

        // 3. El ZIP se escribe directamente en la ruta, y no en un archivo del diálogo: es lo que
        //    permite repetir el guardado sin volver a preguntar.
        await using var stream = File.Create(destino);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        // --- A) theme.json ---
        var jsonEntry = archive.CreateEntry("theme.json", CompressionLevel.NoCompression);
        using (var entryStream = jsonEntry.Open())
        using (var writer = new StreamWriter(entryStream))
        {
            await writer.WriteAsync(jsonText);
        }

        // --- B) Los archivos de código (HTML, JS, CSS) ---
        // Antes, lo que esté escrito ahora mismo en el editor (theme.json no, que se genera).
        if (CodeEditorVM.IsDocumentLoaded &&
            CodeEditorVM.CurrentFileName != "theme.json" &&
            CodeEditorVM.FileBuffers.ContainsKey(CodeEditorVM.CurrentFileName))
        {
            CodeEditorVM.FileBuffers[CodeEditorVM.CurrentFileName] = CodeEditorVM.EditorDocument.Text;
        }

        foreach (var fileBuffer in CodeEditorVM.FileBuffers)
        {
            if (fileBuffer.Key == "theme.json") continue;
            if (string.IsNullOrEmpty(fileBuffer.Value)) continue; // los archivos vacíos no entran

            var codeEntry = archive.CreateEntry(fileBuffer.Key, CompressionLevel.NoCompression);
            using (var entryStream = codeEntry.Open())
            using (var writer = new StreamWriter(entryStream))
            {
                await writer.WriteAsync(fileBuffer.Value);
            }
        }

        // --- C) Los recursos en assets/, con los MISMOS nombres que lleva theme.json ---
        // Se recorre el mapa y no los objetos: así no hay un segundo cálculo que pueda salir
        // distinto. Ya trae imágenes, vídeos, fotogramas, fuentes y las dos variantes de los iconos
        // alternantes. Dos rutas pueden compartir nombre cuando son el mismo archivo: ese nombre se
        // escribe una sola vez.
        var escritos = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (origen, nombre) in nombres)
        {
            // El theme.json nombra también los que ya no están en disco; esos no se pueden meter.
            if (!File.Exists(origen) || !escritos.Add(nombre)) continue;

            var entrada = archive.CreateEntry($"assets/{nombre}", CompressionLevel.NoCompression);
            using (var entryStream = entrada.Open())
            using (var fileStream = File.OpenRead(origen))
            {
                await fileStream.CopyToAsync(entryStream);
            }
        }
    }

    /// <summary>
    /// Todos los objetos del tema, de las cuatro colecciones. OJO: el otro sitio que las enumera
    /// es el allWidgetsWithFolders de GenerateThemeJson, así que una colección nueva hay que darla
    /// de alta en los dos (lista de comprobación del CLAUDE.md). De aquí sale también qué recursos
    /// entran en el ZIP: si una colección faltara aquí, el theme.json nombraría sus imágenes y el
    /// ZIP no las llevaría.
    /// </summary>
    private IEnumerable<WidgetItemViewModel> TodosLosObjetos() =>
        PlayingObjects.Concat(QueueObjects).Concat(NotificationObjects).Concat(UnifiedObjects);

    /// <summary>
    /// Antes de guardar, convierte la fuente del SISTEMA de cada texto en una fuente propia del
    /// tema: extrae su archivo (FuentesDelSistema.Extraer) y hace que el objeto apunte a él. A
    /// partir de ahí, el camino de siempre (empaquetar, exportar, cargar) la mete en el ZIP sin
    /// tocar nada más. Así el texto se ve igual en el equipo donde corra OBS, tenga o no esa
    /// fuente instalada.
    /// </summary>
    /// <returns>Las familias que se incrustaron y las que no se pudieron, para el mensaje de
    /// estado.</returns>
    private (List<string> Incrustadas, List<string> SinIncrustar) IncrustarFuentesDelSistema()
    {
        var incrustadas = new List<string>();
        var sinIncrustar = new List<string>();

        // La multiselección (OnTrackedWidgetPropertyChanged) copia POR REFLEXIÓN cualquier
        // propiedad que cambie en un objeto seleccionado a los demás seleccionados. Sin esta
        // bandera, con dos textos seleccionados en Arial y Verdana, convertir el primero le pondría
        // Arial al segundo, y el bucle se lo saltaría por tener ya fuente propia: GUARDAR cambiaría
        // la fuente de un objeto, sin avisar.
        // Va con try/finally: una excepción a media asignación dejaría la bandera en true para
        // siempre, y la multiselección dejaría de funcionar sin ningún síntoma que apunte aquí.
        _isSyncingProperties = true;
        try
        {
            foreach (var w in TodosLosObjetos())
            {
                if (!w.IsText || w.UseCustomFont) continue;

                string familia = w.FontFamilyName;
                string? ruta = FuentesDelSistema.Extraer(familia);

                if (ruta == null)
                {
                    if (!sinIncrustar.Contains(familia, StringComparer.OrdinalIgnoreCase)) sinIncrustar.Add(familia);
                    continue;
                }

                // El mismo orden que SelectCustomFont: primero la ruta (que dispara
                // OnCustomFontPathChanged y con él el registro en el FontManager) y después la
                // casilla.
                w.CustomFontPath = ruta;
                w.UseCustomFont = true;

                if (!incrustadas.Contains(familia, StringComparer.OrdinalIgnoreCase)) incrustadas.Add(familia);
            }
        }
        finally
        {
            _isSyncingProperties = false;
        }

        return (incrustadas, sinIncrustar);
    }

    /// <summary>
    /// Guarda el tema en la ruta indicada de forma segura: escribe en un ".tmp" y solo entonces lo
    /// renombra encima del destino. Es la única puerta de guardado: la usan Guardar y Guardar como.
    ///
    /// El .tmp tiene dos motivos, y el segundo es el que casi cuesta un tema:
    ///  1. File.Create VACÍA el destino antes de escribir nada, y el ZIP se arma después leyendo
    ///     los recursos del disco. Si a un tema le falta una imagen que tenía, File.OpenRead falla
    ///     con el archivo bueno YA VACIADO. Con el .tmp por medio, el original no se toca hasta que
    ///     el nuevo está entero.
    ///  2. En Linux, renombrar no respeta los bloqueos de otros procesos, así que esto funciona
    ///     aunque otro programa tenga el destino abierto (que fue el motivo de un cierre inesperado
    ///     el 2026-09-07). En Windows seguiría fallando, pero de forma limpia y con el original
    ///     intacto.
    ///
    /// Devuelve si se guardó, y NO relanza las excepciones: un fallo al guardar no puede tumbar
    /// la aplicación, que es lo que pasaba antes (un AsyncRelayCommand relanza el error en el hilo
    /// de la interfaz y nadie lo recoge). El error se muestra en el editor de código.
    /// </summary>
    /// <param name="destino">Ruta final del .zip.</param>
    private async Task<bool> WriteThemeZip(string destino)
    {
        string temporal = destino + ".tmp";

        // Antes de escribir, las fuentes del sistema pasan a viajar dentro del tema. Va AQUÍ y no
        // en SaveTheme o SaveThemeAs porque esta es la única puerta por la que pasan los dos. Y NO
        // en DumpThemeToFolder: la vista previa corre en este equipo, donde la fuente está
        // instalada, y convertir los objetos cada vez que se abre estaría mal.
        // Si el guardado falla más abajo, los objetos se quedan convertidos pero sin guardar
        // (IsDirty sigue en true): es inofensivo, y el siguiente intento ya los encuentra así.
        var (incrustadas, sinIncrustar) = IncrustarFuentesDelSistema();

        try
        {
            await EscribirZip(temporal);
            File.Move(temporal, destino, overwrite: true);
        }
        catch (Exception ex)
        {
            // Que no quede basura si el .tmp llegó a crearse. Si tampoco se puede borrar, da igual:
            // lo importante es no perder el mensaje de error de abajo.
            try { if (File.Exists(temporal)) File.Delete(temporal); } catch { }

            ReportarErrorDeGuardado(ex);
            return false;
        }

        // ¡OJO! Este mensaje va en StatusMessage y NO en CurrentFileName: cuando se escribía ahí,
        // en el siguiente cambio de archivo se colaba en el diccionario un "archivo" con el texto
        // del mensaje como nombre, y terminaba dentro del ZIP.
        IsDirty = false;
        string mensaje = EditorI18n.Trf("UI_SaveOk", Path.GetFileName(destino));
        if (incrustadas.Count > 0)
            mensaje += EditorI18n.Trf("UI_SaveFontsEmbedded", string.Join(", ", incrustadas));
        if (sinIncrustar.Count > 0)
            mensaje += EditorI18n.Trf("UI_SaveFontsNotEmbedded", string.Join(", ", sinIncrustar));
        CodeEditorVM.StatusMessage = mensaje;
        return true;
    }

    /// <summary>
    /// Enseña un error de guardado en el editor de código. Hace lo mismo que ReportarErrorDeCarga,
    /// y por el mismo motivo incluye el nombre del TIPO de excepción, que es lo que convierte el
    /// aviso en un diagnóstico: IOException "used by another process" es un bloqueo,
    /// UnauthorizedAccessException son permisos, FileNotFoundException es un recurso del tema que
    /// ya no está donde estaba.
    /// IsDirty se queda como estaba: el tema NO se ha guardado, y el aviso al cerrar sin guardar
    /// tiene que seguir saltando.
    /// </summary>
    private void ReportarErrorDeGuardado(Exception ex)
    {
        CurrentSidePanel = CodeEditorVM;
        IsCanvasVisible = false;
        IsCodeEditorVisible = true;
        CodeEditorVM.StatusMessage = EditorI18n.Trf("UI_SaveError", ex.GetType().Name, ex.Message);
    }

    /// <summary>
    /// Vuelca el tema a una carpeta de rescate y devuelve su ruta, o null si no se pudo. La llama
    /// la red de excepciones de App.axaml.cs cuando en el hilo de la interfaz salta un error que
    /// nadie ha atendido. La aplicación sigue abierta después, pero en un estado del que no se sabe
    /// nada, así que:
    ///
    ///  - NUNCA escribe en la ruta del último guardado. Tras un error así, lo último que se quiere
    ///    es machacar el último tema bueno con un estado del que no se sabe nada. Es la misma
    ///    lección que el .tmp del guardado.
    ///  - La subcarpeta lleva FECHA Y HORA, y no es por estética: DumpThemeToFolder empieza
    ///    borrando la carpeta, así que con un nombre fijo el segundo error borraría el rescate del
    ///    primero, justo cuando más falta hace, porque un fallo que se repite se suele provocar dos
    ///    veces seguidas al intentar entenderlo.
    ///  - No lanza nunca: una excepción aquí dentro se llevaría por delante la última oportunidad
    ///    de salvar el trabajo.
    /// </summary>
    /// <param name="motivo">Qué pasó, para el mensaje que se enseña.</param>
    public string? VolcarRescate(string motivo)
    {
        string? carpeta = null;

        try
        {
            carpeta = Path.Combine(
                SheepGDRequest.Services.AppPaths.DataDir,
                "rescate",
                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));

            DumpThemeToFolder(carpeta);
        }
        catch
        {
            carpeta = null;
        }

        // El aviso va en su PROPIO try, separado del de arriba: que falle pintar el mensaje no
        // debe hacer perder el dato de que el volcado sí salió bien, ni al revés. Y va por el
        // mismo canal que ReportarErrorDeGuardado, para no abrir ventanas nuevas justo cuando
        // menos se puede confiar en la interfaz.
        try
        {
            CurrentSidePanel = CodeEditorVM;
            IsCanvasVisible = false;
            IsCodeEditorVisible = true;
            CodeEditorVM.StatusMessage = carpeta != null
                ? EditorI18n.Trf("UI_RescueSaved", motivo, carpeta)
                : EditorI18n.Trf("UI_RescueFailed", motivo);
        }
        catch { /* si ni esto se puede, al menos el volcado ya esta en disco */ }

        return carpeta;
    }

    // ==========================================
    // NOMBRES DE LOS RECURSOS DENTRO DEL TEMA
    // ==========================================

    /// <summary>
    /// El nombre "limpio" de un recurso: en minúsculas, sin espacios y sin tildes, para que no dé
    /// problemas ni en Linux ni en una URL. Lo usa CalcularNombresDeAssets, que es quien decide el
    /// nombre final dentro del tema. (Hasta que existió ese mapa había tres copias de esta
    /// función, que tenían que dar lo mismo a mano.)
    /// </summary>
    private static string SanitizeFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";

        string fileName = Path.GetFileName(path).Replace(" ", "_").ToLower();

        var normalized = fileName.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>
    /// Decide con qué nombre viaja cada recurso dentro de assets/: devuelve ruta de origen →
    /// nombre. Se calcula UNA vez por guardado, y lo leen los tres que antes lo calculaban cada
    /// uno por su lado: GenerateThemeJson, EscribirZip y DumpThemeToFolder.
    ///
    /// El nombre es el de SanitizeFileName. Solo cuando dos archivos DISTINTOS acaban con el mismo
    /// nombre, el segundo lleva sufijo (fondo_2.png). Antes el segundo no entraba en el ZIP y su
    /// objeto enseñaba en OBS la imagen del primero, sin avisar. Sin choques no cambia ningún
    /// nombre, así que los temas que ya existen se guardan exactamente igual.
    ///
    /// Dos cosas que no son obvias:
    ///  - Los iconos alternantes van PRIMERO, porque engine.js los pide por nombre fijo
    ///    (assets/like.png, dislike.png, star.png, moon.png). Si una imagen tuya se llamara
    ///    like.png y entrara antes, el icono de like enseñaría tu imagen.
    ///  - Mismo nombre y mismo contenido es el mismo recurso, aunque venga de otra ruta. Pasa al
    ///    reabrir cualquier tema con un icono de like: el objeto apunta a la copia temporal de
    ///    like.png y el icono alternante, al like.png del Editor. Sin esta comprobación, cada uno
    ///    de esos temas ganaría un like_2.png duplicado la primera vez que se volviera a guardar.
    ///
    /// Entran también las rutas que ya no existen en disco: el theme.json las sigue nombrando, y
    /// así una imagen perdida no se queda con el nombre de otra que sí está.
    /// </summary>
    private Dictionary<string, string> CalcularNombresDeAssets()
    {
        var nombrePorOrigen = new Dictionary<string, string>(StringComparer.Ordinal);
        var origenPorNombre = new Dictionary<string, string>(StringComparer.Ordinal);

        // Le da nombre a una ruta, si aún no lo tiene.
        void Asignar(string? origen)
        {
            if (string.IsNullOrEmpty(origen) || nombrePorOrigen.ContainsKey(origen)) return;

            string nombre = SanitizeFileName(origen);
            string raiz = Path.GetFileNameWithoutExtension(nombre);
            string extension = Path.GetExtension(nombre);

            // fondo.png, fondo_2.png, fondo_3.png... hasta dar con uno libre o con uno que ya lleve
            // este mismo archivo.
            for (int n = 2; origenPorNombre.TryGetValue(nombre, out var ocupante) && !MismoContenido(origen, ocupante); n++)
                nombre = $"{raiz}_{n}{extension}";

            nombrePorOrigen[origen] = nombre;
            origenPorNombre.TryAdd(nombre, origen);
        }

        // Primero, las variantes de los iconos alternantes, para que se queden su nombre fijo.
        foreach (var w in TodosLosObjetos())
            foreach (var swap in SwapAssetsFor(w))
                Asignar(swap);

        // Después, los archivos de cada objeto, con las MISMAS condiciones con las que
        // GenerateThemeJson escribe cada ruta. Si un día no coinciden, el JSON nombrará un recurso
        // que el ZIP no lleva.
        foreach (var w in TodosLosObjetos())
        {
            if (w.IsMedia) Asignar(w.ImageFilePath);
            if (w.IsVideo) Asignar(w.VideoPosterPath);
            if (w.UseCustomFont) Asignar(w.CustomFontPath);
        }

        return nombrePorOrigen;
    }

    /// <summary>
    /// Si dos archivos son iguales byte a byte. Solo se llama cuando dos recursos chocan de nombre.
    /// Por encima de 8 MB ni se mira y se dan por distintos: con theme.json abierto en el editor de
    /// código, el mapa se recalcula cada medio segundo, y leer dos vídeos enteros a ese ritmo no
    /// compensa. Lo peor que sale así es un duplicado en el ZIP, nunca una imagen equivocada. Por
    /// lo mismo, si alguno no se puede leer, se dan por distintos.
    /// </summary>
    private static bool MismoContenido(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            if (fa.Length > 8 * 1024 * 1024) return false;

            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch
        {
            return false;
        }
    }

    // ==========================================
    // ICONOS QUE ALTERNAN ENTRE DOS IMÁGENES
    // ==========================================

    // Los objetos que alternan entre dos imágenes según los datos (like/dislike, estrella/luna).
    // engine.js pide las dos por nombre fijo, así que el tema tiene que llevar SIEMPRE las dos,
    // no solo la que tiene el objeto.
    private static readonly Dictionary<WidgetObjectType, string[]> SwapIconAssets = new()
    {
        [WidgetObjectType.LikeIcon] = new[] { "Assets/GDassets/icons/like.png", "Assets/GDassets/icons/dislike.png" },
        [WidgetObjectType.StarIcon] = new[] { "Assets/GDassets/icons/star.png", "Assets/GDassets/icons/moon.png" }
    };

    /// <summary>
    /// Las dos variantes de un icono alternante, con su ruta completa y solo las que existen en
    /// disco. Nada si el objeto no es de ese tipo.
    /// </summary>
    private IEnumerable<string> SwapAssetsFor(WidgetItemViewModel widget)
    {
        if (!SwapIconAssets.TryGetValue(widget.ObjectType, out var relativos)) yield break;

        foreach (var rel in relativos)
        {
            string full = ResolveAssetPath(rel);
            if (File.Exists(full)) yield return full;
        }
    }

    // ==========================================
    // VISTA PREVIA EN EL NAVEGADOR
    // ==========================================

    /// <summary>
    /// Vuelca el tema tal y como está ahora mismo a una carpeta, con la MISMA estructura que
    /// tendría dentro del ZIP. Es lo que sirve PreviewServerService, y por eso la vista previa
    /// enseña el index.html de verdad y no una maqueta aparte. También lo usa el volcado de
    /// rescate. OJO: empieza BORRANDO la carpeta si ya existe.
    /// </summary>
    /// <param name="dir">Carpeta de destino.</param>
    private void DumpThemeToFolder(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        // 1. theme.json: la misma fuente de verdad que el ZIP, y los mismos nombres de recursos.
        var nombres = CalcularNombresDeAssets();
        File.WriteAllText(Path.Combine(dir, "theme.json"), GenerateThemeJson(nombres));

        // 2. Los archivos de código, con lo que esté escrito ahora mismo en el editor.
        if (CodeEditorVM.IsDocumentLoaded &&
            CodeEditorVM.CurrentFileName != "theme.json" &&
            CodeEditorVM.FileBuffers.ContainsKey(CodeEditorVM.CurrentFileName))
        {
            CodeEditorVM.FileBuffers[CodeEditorVM.CurrentFileName] = CodeEditorVM.EditorDocument.Text;
        }

        foreach (var buffer in CodeEditorVM.FileBuffers)
        {
            if (buffer.Key == "theme.json") continue;
            if (string.IsNullOrEmpty(buffer.Value)) continue;

            // Las claves llevan carpeta dentro ("playing/index.html"), así que hay que crearla.
            string destino = Path.Combine(dir, buffer.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            File.WriteAllText(destino, buffer.Value);
        }

        // 3. Imágenes, vídeos y fuentes en assets/, igual que en el ZIP: con el mismo mapa.
        string assetsDir = Path.Combine(dir, "assets");
        Directory.CreateDirectory(assetsDir);

        foreach (var (origen, nombre) in nombres)
        {
            string destino = Path.Combine(assetsDir, nombre);
            if (File.Exists(origen) && !File.Exists(destino)) File.Copy(origen, destino);
        }
    }

    // Opciones del JSON de la vista previa: no escapa las tildes ni los símbolos, así que los
    // textos llegan al navegador tal cual.
        private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Un nivel de demostración con los mismos datos que rotan en el lienzo, con los campos que
    /// envía SheepGDRequest. Devuelve un diccionario y no un objeto anónimo porque la notificación
    /// necesita añadirle campos (usuario, mensaje), y a un objeto anónimo no se le puede.
    /// </summary>
    /// <param name="offset">0 = el nivel en juego; 1, 2... = los de la cola, que van desplazados
    /// en las listas para que no salgan todos iguales.</param>
    private Dictionary<string, object?> PreviewLevel(int offset)
    {
        // El auto-skip de demostración dura 2 minutos y vuelve a empezar al terminar.
        long ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_previewSkipDeadline <= ahora) _previewSkipDeadline = ahora + 120;

        int i = _previewIndex + offset;
        string? halo = _haloPaths[i % _haloPaths.Length];

        return new Dictionary<string, object?>
        {
            ["requestId"]          = $"preview-{offset}",
            ["levelId"]            = _previewLevelIds[i % _previewLevelIds.Length],
            ["name"]               = _previewLevelNames[i % _previewLevelNames.Length],
            ["author"]             = _previewCreators[i % _previewCreators.Length],
            ["requesterName"]      = _previewRequesters[i % _previewRequesters.Length],
            ["platformIconPath"]   = _previewPlatformIcons[i % _previewPlatformIcons.Length],
            ["difficultyFacePath"] = _facePaths[i % _facePaths.Length],
            ["difficultyName"]     = _previewRateTexts[i % _previewRateTexts.Length],
            ["stars"]              = _previewStars[i % _previewStars.Length],
            ["downloads"]          = _previewDownloads[i % _previewDownloads.Length],
            ["likes"]              = _previewLikes[i % _previewLikes.Length],
            ["orbs"]               = _previewOrbs[i % _previewOrbs.Length],
            ["length"]             = _previewDurations[i % _previewDurations.Length],
            ["aredlRank"]          = _previewAredlPos[i % _previewAredlPos.Length],
            ["gddlTier"]           = _previewGddlRate[i % _previewGddlRate.Length],
            ["isDislike"]          = i % 4 == 3,
            ["hasFeatureGlow"]     = halo != null,
            ["featureGlowPath"]    = halo ?? "",

            // Solo el nivel en juego lleva cuenta atrás; los de la cola van a 0, que es como el
            // widget sabe que ahí no se pinta reloj.
            ["autoSkipEndsAtUnix"] = offset == 0 ? _previewSkipDeadline : 0,

            // Vacío a propósito: el widget calcula su reloj por su cuenta a partir del instante
            // de arriba. Si en la vista previa se ve un '00:00' fijo, ese tema lleva dentro un
            // engine.js antiguo.
            ["timeRemainingText"]  = "",
            ["isRequestsEnabled"]  = true
        };
    }

    /// <summary>
    /// El mismo mensaje "state_updated" que enviaría SheepGDRequest por WebSocket, con los datos
    /// de demostración que rotan en el lienzo: el nivel en juego y tantos niveles de cola como
    /// muestra el tema. Así la vista previa enseña lo mismo que el lienzo, al mismo ritmo de 3
    /// segundos.
    /// </summary>
    private string BuildPreviewStateJson()
    {
        var payload = new
        {
            @event = "state_updated",
            data = new
            {
                language = EditorI18n.Idioma,
                current  = PreviewLevel(0),
                queue    = Enumerable.Range(1, Math.Max(1, QueueMaxItems)).Select(PreviewLevel).ToArray()
            }
        };

        return JsonSerializer.Serialize(payload, PreviewJsonOptions);
    }

    /// <summary>
    /// Una notificación de demostración ("new_notification"), con el nivel, el usuario y el
    /// mensaje ya montados. El lienzo simula las notificaciones por su cuenta, pero hasta que
    /// existió esto no salían por el WebSocket, y en el navegador no aparecía ninguna.
    /// </summary>
    /// <param name="idx">Qué notificación de la lista de textos toca.</param>
    private string BuildPreviewNotificationJson(int idx)
    {
        string user      = _previewRequesters[idx % _previewRequesters.Length];
        string level     = _previewLevelNames[idx % _previewLevelNames.Length];
        string plantilla = _previewNotificationTexts[idx % _previewNotificationTexts.Length];

        var datos = PreviewLevel(0);
        datos["username"]  = user;
        datos["levelData"] = level;

        // OJO: el mensaje tiene que llevar DENTRO el usuario y el nivel ya sustituidos.
        // injectNotificationData (engine.js) los busca por su texto dentro del mensaje para
        // pintarlos con sus colores; si le llegan los marcadores <USUARIO> y <NIVEL> sin sustituir,
        // no los encuentra y la notificación sale toda del mismo color.
        datos["message"]   = plantilla.Replace("<USUARIO>", user).Replace("<NIVEL>", level);

        // Que el nombre del nivel de la tarjeta cuadre con el nivel del que habla el mensaje.
        datos["name"]      = level;

        return JsonSerializer.Serialize(new { @event = "new_notification", data = datos }, PreviewJsonOptions);
    }

    /// <summary>
    /// Botón "Vista previa": vuelca el tema a la carpeta temporal, enciende el servidor si no lo
    /// estaba y abre la página en el navegador del sistema. Si ya había una pestaña abierta, le
    /// pide que se recargue con el tema nuevo. Si algo falla, lo dice en el editor de código.
    /// </summary>
    [RelayCommand]
    private void OpenBrowserPreview()
    {
        try
        {
            // Una carpeta propia para cada Editor, como las de LoadTheme. Con un nombre fijo, dos
            // Editores abiertos a la vez se pisaban los archivos (las dos pestañas acababan
            // enseñando el último tema volcado), y al cerrar uno se borraba la carpeta que el otro
            // seguía sirviendo.
            _previewFolder ??= Path.Combine(Path.GetTempPath(), "SheepGDEditor_Preview",
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}");
            DumpThemeToFolder(_previewFolder);


            var servidor = PreviewServerService.Instance;
            servidor.StateProvider = BuildPreviewStateJson;

            // Lo mismo para las notificaciones: cada pestaña que conecta recibe una nada más
            // entrar, sin esperar al siguiente ciclo. Una notificación es un evento, no un estado:
            // la que se emitió antes de que existiera esta pestaña no vuelve nunca.
            servidor.NotificationProvider = () => BuildPreviewNotificationJson(_previewNotifIndex);

            // La ráfaga inicial de notificaciones vuelve a empezar: al reabrir, la pila está vacía.
            _previewNotifBurst = 0;
            _previewNotifElapsed = 0;

            string url = servidor.IsRunning
                ? servidor.BaseUrl
                : servidor.Start(_previewFolder, ResolveAssetPath("Assets"));

            // Si ya había una pestaña abierta, que se entere del tema nuevo: engine.js responde a
            // 'reload_theme' recargando la página entera.
            _ = servidor.BroadcastAsync("{\"event\":\"reload_theme\",\"target\":\"unified\"}");

            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Nada de fallar en silencio: el error se enseña en el editor de código.
            CurrentSidePanel = CodeEditorVM;
            IsCanvasVisible = false;
            IsCodeEditorVisible = true;
            CodeEditorVM.StatusMessage = EditorI18n.Trf("UI_PreviewOpenError", ex.Message);
        }
    }

    // Las carpetas donde LoadTheme extrajo recursos en ESTA sesión. Al cerrar se borran solo
    // estas, no toda la carpeta SheepGDEditor_temas: otro Editor abierto a la vez tiene ahí las
    // suyas y las vuelve a leer al guardar.
    private readonly List<string> _carpetasDeCarga = new();

    /// <summary>
    /// Borra lo que se extrajo al abrir temas en esta sesión. Lo llama el cierre de la ventana.
    /// </summary>
    public void LimpiarCarpetasDeCarga()
    {
        foreach (var carpeta in _carpetasDeCarga)
        {
            // Una fuente puede seguir abierta por el FontManager. Lo que no se pueda borrar se
            // queda, sin más.
            try { if (Directory.Exists(carpeta)) Directory.Delete(carpeta, true); } catch { }
        }
        _carpetasDeCarga.Clear();
    }

    /// <summary>
    /// Apaga el servidor de la vista previa y borra su carpeta temporal. Lo llama el cierre de la
    /// ventana.
    /// </summary>
    public void StopBrowserPreview()
    {
        PreviewServerService.Instance.Stop();
        try
        {
            if (_previewFolder != null && Directory.Exists(_previewFolder))
                Directory.Delete(_previewFolder, true);
        }
        catch { }
    }

    // ==========================================
    // CARGA DE TEMAS
    // ==========================================

    /// <summary>
    /// Enseña un error al abrir un tema. Un tema que no se puede abrir tiene que decirlo: antes
    /// había dos salidas mudas (el ZIP sin theme.json y una excepción tragada) y en las dos el
    /// programa parecía colgado. Se salta al editor de código porque es donde vive el mensaje de
    /// estado, y porque tras un fallo de carga no hay nada que mirar en el lienzo.
    /// </summary>
    private void ReportarErrorDeCarga(string detalle)
    {
        CurrentSidePanel = CodeEditorVM;
        IsCanvasVisible = false;
        IsCodeEditorVisible = true;
        CodeEditorVM.StatusMessage = EditorI18n.Trf("UI_LoadError", detalle);
    }

    /// <summary>
    /// Abrir un tema (.zip): pide el archivo, vacía los cuatro lienzos y los reconstruye a partir
    /// de su theme.json, extrae sus recursos a una carpeta temporal propia y carga sus archivos de
    /// código. El tema abierto queda como ruta de guardado, sin cambios pendientes y sin
    /// historial. Si algo falla, lo dice en el editor de código.
    ///
    /// Si el tema actual tiene cambios sin guardar, antes de nada pregunta qué hacer con ellos,
    /// como al cerrar la ventana.
    /// </summary>
    /// <param name="parentWindow">Ventana dueña del diálogo.</param>
    [RelayCommand]
    private async Task LoadTheme(Window parentWindow)
    {
        if (parentWindow == null) return;

        // Antes de nada, los cambios sin guardar del tema actual. Abrir otro vacía los lienzos y
        // el historial, así que después ya no habría forma de recuperarlos: se pregunta lo mismo
        // que al cerrar (guardar, seguir sin guardar o cancelar). Antes los descartaba sin avisar.
        if (parentWindow is Views.MainWindow ventana &&
            !await ventana.PuedeDescartarLosCambiosAsync(EditorI18n.Tr("UI_UnsavedOpenMessage"), EditorI18n.Tr("UI_BtnOpenWithoutSaving")))
        {
            return;
        }

        // 1. Diálogo de "Abrir", empezando en la carpeta de temas.
        var files = await parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EditorI18n.Tr("UI_OpenThemeTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = await CarpetaDeTemasComoOrigen(parentWindow),
            FileTypeFilter = new[] { new FilePickerFileType(EditorI18n.Tr("UI_FilterThemeZip")) { Patterns = new[] { "*.zip" } } }
        });

        // Si el usuario cancela, no se hace nada.
        if (files == null || files.Count == 0) return;

        var file = files[0];

        try
        {
            // 2. El ZIP se lee sin descomprimirlo a disco.
            await using var stream = await file.OpenReadAsync();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            // 3. Sin theme.json no es un tema.
            var jsonEntry = archive.GetEntry("theme.json");
            if (jsonEntry == null)
            {
                ReportarErrorDeCarga(EditorI18n.Tr("UI_LoadErrorNoThemeJson"));
                return;
            }

            // 4. Se lee el theme.json y se convierte en ThemeExportData.
            using var entryStream = jsonEntry.Open();
            using var reader = new StreamReader(entryStream);
            string jsonText = await reader.ReadToEndAsync();

            // Sin distinguir mayúsculas en los nombres de los campos, para aceptar temas escritos a
            // mano o por versiones anteriores.
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var importedData = JsonSerializer.Deserialize<ThemeExportData>(jsonText, jsonOptions);
            if (importedData == null) return;

            ThemeName = importedData.ThemeName ?? EditorI18n.Tr("Default_ThemeName");
            ThemeAuthor = importedData.Author ?? EditorI18n.Tr("Default_ThemeAuthor");

            // 5. Se reconstruye el Editor: primero los ajustes del tema...
            //
            // El tamaño del lienzo depende del tipo. Un tema unificado guarda el lienzo del autor,
            // y se recupera tal cual. Uno de un solo tipo guarda el tamaño que OCUPAN sus objetos
            // (ComputeExportedCanvasSize), no el lienzo en el que se diseñó: copiarlo encogía el
            // lienzo de trabajo hasta el contenido (un texto en una esquina dejaba un lienzo de
            // 400x180, y los objetos nuevos nacían encajados ahí). Así que se queda el mayor de los
            // dos, el que ya había y el del tema, para que tampoco quede cortado un tema hecho en
            // un lienzo más grande.
            if (string.Equals(importedData.WidgetType, "unified", StringComparison.OrdinalIgnoreCase))
            {
                // Un tamaño de 0 o negativo no se copia: el lienzo desaparecería. Puede venir de un
                // theme.json sin el campo, o de cuando la casilla del tamaño admitía negativos.
                if (importedData.CanvasWidth > 0) CanvasWidth = importedData.CanvasWidth;
                if (importedData.CanvasHeight > 0) CanvasHeight = importedData.CanvasHeight;
            }
            else
            {
                CanvasWidth = Math.Max(CanvasWidth, importedData.CanvasWidth);
                CanvasHeight = Math.Max(CanvasHeight, importedData.CanvasHeight);
            }

            // ...los de la cola...
            QueueMaxItems = importedData.QueueMaxItems;
            QueueDirectionIndex = importedData.QueueDirection;
            QueueSpacing = importedData.QueueSpacing;

            // ...y los de las notificaciones. Estos cuatro faltaron durante un tiempo:
            // GenerateThemeJson los escribía desde siempre, así que el ZIP los llevaba dentro, pero
            // nadie los volvía a leer, y el síntoma no se distinguía de "no se guardan". (Es el
            // último paso de la lista de comprobación del CLAUDE.md: elegir, clonar, JSON, ZIP,
            // vista previa y CARGAR.)
            NotificationMaxItems = importedData.NotificationMaxItems;
            NotificationDirectionIndex = importedData.NotificationDirection;
            NotificationSpacing = importedData.NotificationSpacing;
            NotificationTimerModeIndex = importedData.NotificationTimerMode;

            // Se vacían TODOS los lienzos, también el Unified: cuando faltaba, los objetos unified
            // del tema anterior se colaban en el que se abría, y se guardaban con él.
            PlayingObjects.Clear();
            QueueObjects.Clear();
            NotificationObjects.Clear();
            UnifiedObjects.Clear();


            // Cada tema que se abre extrae sus recursos a su PROPIA carpeta. Antes iban todos
            // sueltos a la carpeta temporal, así que dos temas con un fondo.png distinto compartían
            // archivo y el segundo pisaba al primero. Los lienzos se vacían al cargar, pero el
            // portapapeles NO: copiar un objeto del tema A, abrir el B y pegarlo dejaba el objeto
            // apuntando al archivo del B, y era ese el que acababa dentro del ZIP.
            string carpetaCarga = Path.Combine(Path.GetTempPath(), "SheepGDEditor_temas",
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}");
            Directory.CreateDirectory(carpetaCarga);
            _carpetasDeCarga.Add(carpetaCarga);

            // Se rehace cada objeto guardado. Los de un tipo desconocido se descartan.
            foreach (var obj in importedData.Objects)
            {
                if (Enum.TryParse(obj.Type, true, out WidgetObjectType parsedType))
                {
                    var newWidget = new WidgetItemViewModel
                    {
                        ObjectType = parsedType,
                        ObjectName = obj.Name,
                        X = obj.X,
                        Y = obj.Y,
                        Width = obj.Width,
                        Height = obj.Height,
                        LayerIndex = obj.Layer,
                        TextColorHex = obj.ColorHex,
                        FontSize = obj.FontSize,
                        FontBlur = obj.FontBlur,
                        BackgroundTypeIndex = obj.BackgroundType,
                        Transparency = obj.Transparency,
                        RotationAngle = obj.RotationAngle,
                        CropTop = obj.CropTop,
                        CropBottom = obj.CropBottom,
                        CropLeft = obj.CropLeft,
                        CropRight = obj.CropRight,
                        LinkedGroupId = obj.LinkedGroupId,
                        FontFamilyName = obj.FontFamily,
                        UseCustomFont = obj.UseCustomFont,
                        StretchIndex = obj.StretchIndex,
                        BorderRoundness = obj.BorderRoundness,
                        BackgroundColorHex = string.IsNullOrEmpty(obj.BackgroundColorHex) ? "#000000" : obj.BackgroundColorHex,

                        // Ajustes del texto y del cuadro de notificación.
                        IsTextWrapped = obj.IsTextWrapped,
                        RequesterColorHex = string.IsNullOrEmpty(obj.RequesterColorHex) ? "#FF5555" : obj.RequesterColorHex,
                        LevelDataColorHex = string.IsNullOrEmpty(obj.LevelDataColorHex) ? "#55FF55" : obj.LevelDataColorHex,

                        TextAlignmentIndex = obj.TextAlignment,
                        ForegroundColorHex = string.IsNullOrEmpty(obj.ForegroundColorHex) ? "#00000000" : obj.ForegroundColorHex,

                        // Estos tres faltaron durante un tiempo: guardar y volver a abrir un tema
                        // perdía la duración de las notificaciones, la alineación vertical y el
                        // margen interior.
                        NotificationDuration = obj.NotificationDuration <= 0 ? 6.0 : obj.NotificationDuration,
                        VerticalTextAlignmentIndex = obj.VerticalTextAlignment,
                        TextPadding = obj.TextPadding,

                        // Halo de la cara de dificultad y ajustes del vídeo.
                        ShowHalo = obj.HaloEnabled,
                        HaloScale = obj.HaloScale <= 0 ? 1.5 : obj.HaloScale,
                        VideoLoop = obj.VideoLoop,
                        VideoMuted = obj.VideoMuted,

                        // Contorno del texto.
                        HasTextOutline = obj.HasTextOutline,
                        TextOutlineSize = obj.TextOutlineSize <= 0 ? 2 : obj.TextOutlineSize,
                        TextOutlineColorHex = string.IsNullOrEmpty(obj.TextOutlineColor) ? "#000000" : obj.TextOutlineColor,
                    };

                    // Los archivos del objeto se extraen a la carpeta de esta carga, y el objeto
                    // pasa a apuntar a esa copia.

                    // 1. La imagen (o el vídeo).
                    if (!string.IsNullOrEmpty(obj.ImagePath))
                    {
                        var imgEntry = archive.GetEntry(obj.ImagePath);
                        if (imgEntry != null)
                        {
                            string tempPath = Path.Combine(carpetaCarga, Path.GetFileName(obj.ImagePath));
                            using (var imgStream = imgEntry.Open())
                            using (var fileStream = File.Create(tempPath))
                            {
                                await imgStream.CopyToAsync(fileStream);
                            }
                            newWidget.ImageFilePath = tempPath;

                            // ¡OJO! Un .mp4 no es un Bitmap: intentar abrirlo lanzaba una excepción
                            // que abortaba la carga del tema entero. Los vídeos se quedan solo con
                            // la ruta y dibujan su tarjeta de marcador.
                            if (!newWidget.IsVideo)
                            {
                                try
                                {
                                    newWidget.ImageBitmap = new Avalonia.Media.Imaging.Bitmap(tempPath);
                                }
                                catch
                                {
                                    newWidget.ImageBitmap = null; // dañado o formato no soportado
                                }
                            }
                        }
                    }

                    // 1.bis El fotograma de muestra del vídeo.
                    if (!string.IsNullOrEmpty(obj.VideoPoster))
                    {
                        var posterEntry = archive.GetEntry(obj.VideoPoster);
                        if (posterEntry != null)
                        {
                            string tempPath = Path.Combine(carpetaCarga, Path.GetFileName(obj.VideoPoster));
                            using (var posterStream = posterEntry.Open())
                            using (var fileStream = File.Create(tempPath))
                            {
                                await posterStream.CopyToAsync(fileStream);
                            }
                            newWidget.VideoPosterPath = tempPath;

                            try
                            {
                                newWidget.VideoPosterBitmap = new Avalonia.Media.Imaging.Bitmap(tempPath);
                            }
                            catch
                            {
                                // Aquí SÍ hay que tragarse la excepción: un Bitmap que fallaba
                                // abortaba la carga del tema entero.
                                newWidget.VideoPosterBitmap = null;
                            }
                        }
                    }

                    // 2. La fuente propia.
                    if (obj.UseCustomFont && !string.IsNullOrEmpty(obj.CustomFontPath))
                    {
                        var fontEntry = archive.GetEntry(obj.CustomFontPath);
                        if (fontEntry != null)
                        {
                            string tempPath = Path.Combine(carpetaCarga, Path.GetFileName(obj.CustomFontPath));
                            using (var fontStream = fontEntry.Open())
                            using (var fileStream = File.Create(tempPath))
                            {
                                await fontStream.CopyToAsync(fileStream);
                            }
                            newWidget.CustomFontPath = tempPath;
                        }
                    }

                    // 3. Cada objeto, a su lienzo. Se compara en minúsculas; lo desconocido va a
                    //    Playing.
                    switch (obj.CanvasOrigin?.ToLower())
                    {
                        case "queue": QueueObjects.Add(newWidget); break;
                        case "notification": NotificationObjects.Add(newWidget); break;
                        case "unified": UnifiedObjects.Add(newWidget); break;
                        default: PlayingObjects.Add(newWidget); break;
                    }
                }
            }

            // Se avisa a la interfaz para que redibuje el lienzo que se está mirando.
            OnPropertyChanged(nameof(CurrentCanvasObjects));

            // La pestaña se elige DESPUÉS de cargar los objetos, no antes. La opción "Unified" solo
            // existe en el menú cuando el tema lo es, y al llegar aquí las colecciones ya están
            // llenas, así que RefreshWidgetTypeOptions ya la ha añadido. Hacerlo antes dejaría el
            // ComboBox sin selección al pedirle un índice 3 que todavía no existe.
            SelectedWidgetTypeIndex = importedData.WidgetType?.ToLower() switch
            {
                "playing" => 0,
                "queue" => 1,
                "notification" => 2,
                "unified" => 3,   // cuando faltaba, los temas unified se abrían en Playing
                _ => 0
            };

            // El panel de propiedades se suelta de lo que tuviera seleccionado del tema anterior
            // (los objetos del tema nuevo llegan sin seleccionar). Sin esto seguía apuntando a un
            // objeto que ya no está en ningún lienzo: lo que se cambiara en el panel iba a parar
            // a ese objeto suelto y se perdía.
            UpdateSelection();

            // 6. Los archivos de código del ZIP. Se parte de los de fábrica y encima se ponen los
            // del ZIP: así, si el tema venía sin engine.js o sin engine.css, no se queda sin ellos
            // al volver a guardarlo. (Antes se vaciaba el diccionario y esos archivos desaparecían
            // del tema para siempre.)
            var freshBuffers = CodeEditorSideViewModel.CreateDefaultBuffers();

            foreach (var entry in archive.Entries)
            {
                // Todo archivo de texto del tema: HTML, JSON, JS y CSS.
                if (entry.FullName.EndsWith(".html") ||
                    entry.FullName.EndsWith(".json") ||
                    entry.FullName.EndsWith(".js") ||
                    entry.FullName.EndsWith(".css"))
                {
                    using var textStream = entry.Open();
                    using var textReader = new StreamReader(textStream);
                    freshBuffers[entry.FullName] = await textReader.ReadToEndAsync();
                }
            }

            CodeEditorVM.FileBuffers = freshBuffers;

            // El editor arranca en index.html, con el contenido real del ZIP.
            CodeEditorVM.CurrentFileName = "index.html";
            CodeEditorVM.EditorDocument.Text = freshBuffers.TryGetValue("index.html", out var indexHtml) ? indexHtml : "";
            CodeEditorVM.SyntaxHighlighting = AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition("HTML");
            // El documento ya tiene el index.html de verdad, así que se puede volcar de vuelta al
            // diccionario. Sin esto, editar el index.html nada más cargar un tema y guardar perdía
            // la edición.
            CodeEditorVM.IsDocumentLoaded = true;

            // El tema recién abierto es su propia ruta de guardado, y no tiene cambios pendientes.
            LastSavePath = file.Path.LocalPath;
            IsDirty = false;

            // Y empieza SIN historial. Si no, un Ctrl+Z recién abierto un tema podía llevar a los
            // objetos del tema ANTERIOR, y con ellos a sus ajustes y a su nombre.
            //
            // Se vacía aquí, al final, y NO envolviendo la carga en _isRestoringState: esa bandera
            // tendría que ir con try/finally, o un fallo a media carga la dejaría en true para el
            // resto de la sesión y el historial dejaría de funcionar sin ningún síntoma que apunte
            // aquí.
            _undoStack.Clear();
            _redoStack.Clear();
            CodeEditorVM.StatusMessage = EditorI18n.Trf("UI_ThemeLoaded", file.Name);
            _lastLiveJson = ""; // que el theme.json en vivo vuelva a empezar de cero

            // Se vuelve al lienzo, para ver los objetos cargados.
            ShowToolbox();
        }
        catch (Exception ex)
        {
            // El tipo de excepción dice mucho más que el mensaje: InvalidDataException es un ZIP
            // dañado, JsonException un theme.json mal formado, IOException un problema de disco.
            ReportarErrorDeCarga($"{ex.GetType().Name} — {ex.Message}");
        }
    }

    // ==========================================
    // IMÁGENES, VÍDEOS Y FOTOGRAMAS
    // ==========================================

    /// <summary>
    /// Añade al lienzo actual una imagen de la lista "Imágenes predefinidas", centrada en la parte
    /// del lienzo que se está viendo.
    /// </summary>
    /// <param name="filePath">Ruta completa de la imagen.</param>
    [RelayCommand]
    private void AddPredefinedImage(string filePath)
    {
        SaveState();
        var newItem = new WidgetItemViewModel {
            ObjectType = WidgetObjectType.Image,
            Width = 150, Height = 150,
            ImageFilePath = filePath,
            BackgroundTypeIndex = 1
        };

        // Centrada en la parte visible del lienzo, como en AddWidgetObject.
        double visualCenterX = (CanvasWidth / 2.0) - CanvasOffsetX;
        double visualCenterY = (CanvasHeight / 2.0) - CanvasOffsetY;

        double targetX = (visualCenterX / CanvasZoom) - (newItem.Width / 2.0);
        double targetY = (visualCenterY / CanvasZoom) - (newItem.Height / 2.0);

        // Y sin salirse del lienzo.
        newItem.X = Math.Clamp(targetX, 0, Math.Max(0, CanvasWidth - newItem.Width));
        newItem.Y = Math.Clamp(targetY, 0, Math.Max(0, CanvasHeight - newItem.Height));

        if (File.Exists(filePath)) newItem.ImageBitmap = new Avalonia.Media.Imaging.Bitmap(filePath);
        CurrentCanvasObjects.Add(newItem);
    }

    /// <summary>
    /// Botón "Cambiar imagen" (o vídeo) del panel de propiedades: pide el archivo para el objeto
    /// seleccionado. Las imágenes se cargan en el lienzo y el objeto toma su tamaño en píxeles; los
    /// vídeos solo guardan la ruta, porque el lienzo no los reproduce.
    /// </summary>
    /// <param name="parentWindow">Ventana dueña del diálogo.</param>
    [RelayCommand]
    private async Task SelectImageForWidget(Window parentWindow)
    {
        if (parentWindow == null) return;
        var selected = PropertiesVM.SelectedWidget;
        if (selected == null || !selected.IsMedia) return;

        // El filtro del diálogo depende de si el objeto es un vídeo o una imagen.
        var filter = selected.IsVideo
            // Formatos que el navegador de OBS reproduce.
            ? new FilePickerFileType(EditorI18n.Tr("UI_FilterVideos")) { Patterns = new[] { "*.mp4", "*.webm", "*.mov", "*.ogv", "*.ogg" } }
            : new FilePickerFileType(EditorI18n.Tr("UI_FilterImages")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif" } };

        var files = await parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = selected.IsVideo ? EditorI18n.Tr("UI_PickVideoTitle") : EditorI18n.Tr("UI_PickImageTitle"),
            AllowMultiple = false,
            FileTypeFilter = new[] { filter }
        });

        if (files != null && files.Count > 0)
        {
            // El paso del historial, solo si se eligió un archivo, como en SelectVideoPoster.
            // (Antes iba al principio, antes incluso de comprobar que el objeto es una imagen o un
            // vídeo.)
            SaveState();
            selected.ImageFilePath = files[0].Path.LocalPath;
            if (!selected.IsVideo) {
                await using var stream = await files[0].OpenReadAsync();
                selected.ImageBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                selected.Width = selected.ImageBitmap.Size.Width;
                selected.Height = selected.ImageBitmap.Size.Height;
            } else {
                // El lienzo de Avalonia no reproduce vídeo: se quita cualquier imagen anterior
                // para que se dibuje la tarjeta de marcador con el nombre del archivo.
                selected.ImageBitmap = null;
                selected.PreviewImageBitmap = null;

                // Si el objeto no tenía tamaño, se le da uno en 16:9.
                if (selected.Width <= 0 || selected.Height <= 0)
                {
                    selected.Width = 320;
                    selected.Height = 180;
                }
                selected.ObjectName = Path.GetFileName(selected.ImageFilePath);
            }
        }
    }

    /// <summary>
    /// Botón "Elegir fotograma" de un vídeo: la imagen que se ve en el lienzo y que el widget
    /// enseña mientras el vídeo carga.
    /// </summary>
    /// <param name="parentWindow">Ventana dueña del diálogo.</param>
    [RelayCommand]
    private async Task SelectVideoPoster(Window parentWindow)
    {
        if (parentWindow == null) return;

        var selected = PropertiesVM.SelectedWidget;
        if (selected == null || !selected.IsVideo) return;

        var files = await parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EditorI18n.Tr("UI_PickPosterTitle"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(EditorI18n.Tr("UI_FilterImages")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });

        if (files == null || files.Count == 0) return;

        // SaveState() va AQUÍ y no al principio: si se abre el diálogo y se cancela, no tiene
        // sentido haber metido un paso en el historial de deshacer.
        SaveState();

        selected.VideoPosterPath = files[0].Path.LocalPath;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            selected.VideoPosterBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch
        {
            // Un PNG dañado no puede tumbar nada: el vídeo se queda sin fotograma y vuelve a
            // dibujarse la tarjeta de marcador.
            selected.VideoPosterBitmap = null;
            selected.VideoPosterPath = "";
        }
    }

    /// <summary>
    /// Botón "Quitar imagen por defecto" del fondo del nivel. Sin él, un fondo del nivel siempre
    /// tenía imagen de repuesto (la elegida, o el 16-9BG.png con el que nace) y no había forma de
    /// que se viera transparente cuando el nivel no tiene miniatura.
    /// </summary>
    [RelayCommand]
    private void ClearDefaultImage()
    {
        var selected = PropertiesVM.SelectedWidget;
        if (selected == null || !selected.IsLevelBackground) return;

        // Si no hay nada que quitar, se sale ANTES de SaveState(): no tiene sentido gastar un paso
        // del historial en un cambio que no cambia nada.
        if (string.IsNullOrEmpty(selected.ImageFilePath) && selected.ImageBitmap == null) return;

        SaveState();
        selected.ImageFilePath = "";
        selected.ImageBitmap = null;
    }

    /// <summary>Botón "Quitar fotograma" de un vídeo.</summary>
    [RelayCommand]
    private void ClearVideoPoster()
    {
        var selected = PropertiesVM.SelectedWidget;
        if (selected == null || !selected.IsVideo) return;

        // Si no hay nada que quitar, se sale ANTES de SaveState(), por lo mismo que arriba.
        if (string.IsNullOrEmpty(selected.VideoPosterPath) && selected.VideoPosterBitmap == null) return;

        SaveState();
        selected.VideoPosterPath = "";
        selected.VideoPosterBitmap = null;
    }

    // ==========================================
    // COPIAR Y CLONAR OBJETOS
    // ==========================================

    /// <summary>
    /// Copia completa de un objeto, con todas sus propiedades. La usan el historial de deshacer
    /// (cada foto lleva clones), copiar y pegar, y duplicar.
    ///
    /// OJO: es la lista de lo que sobrevive a un Ctrl+Z. Una propiedad nueva que no se copie aquí
    /// se BORRA al deshacer cualquier cambio (lista de comprobación del CLAUDE.md, paso 2).
    /// </summary>
    private WidgetItemViewModel CloneWidget(WidgetItemViewModel source)
    {
        return new WidgetItemViewModel
        {
            IsSelected = source.IsSelected,
            ObjectType = source.ObjectType,
            ObjectName = source.ObjectName,
            X = source.X, Y = source.Y, Width = source.Width, Height = source.Height,
            LayerIndex = source.LayerIndex,

            TextColorHex = source.TextColorHex,
            BackgroundColorHex = source.BackgroundColorHex,
            BackgroundTypeIndex = source.BackgroundTypeIndex,
            Transparency = source.Transparency,
            BorderRoundness = source.BorderRoundness,

            FontSize = source.FontSize,
            FontFamilyName = source.FontFamilyName,
            CustomFontPath = source.CustomFontPath,
            UseCustomFont = source.UseCustomFont,
            FontBlur = source.FontBlur,

            ImageFilePath = source.ImageFilePath,
            ImageBitmap = source.ImageBitmap,
            StretchIndex = source.StretchIndex,
            LinkedGroupId = source.LinkedGroupId,

            RotationAngle = source.RotationAngle,
            CropTop = source.CropTop,
            CropBottom = source.CropBottom,
            CropLeft = source.CropLeft,
            CropRight = source.CropRight,

            // Alineación, color de filtro y vista previa.
            TextAlignmentIndex = source.TextAlignmentIndex,
            ForegroundColorHex = source.ForegroundColorHex,
            IsPreviewEnabled = source.IsPreviewEnabled,

            // Estos faltaron durante un tiempo, y como CloneWidget alimenta el historial, un
            // Ctrl+Z borraba estos ajustes del objeto.
            IsTextWrapped = source.IsTextWrapped,
            VerticalTextAlignmentIndex = source.VerticalTextAlignmentIndex,
            TextPadding = source.TextPadding,
            NotificationDuration = source.NotificationDuration,
            RequesterColorHex = source.RequesterColorHex,
            LevelDataColorHex = source.LevelDataColorHex,

            // Halo de la cara de dificultad y ajustes del vídeo.
            HaloIndex = source.HaloIndex,
            HaloBitmap = source.HaloBitmap,
            ShowHalo = source.ShowHalo,
            HaloScale = source.HaloScale,
            VideoLoop = source.VideoLoop,
            VideoMuted = source.VideoMuted,

            VideoPosterPath = source.VideoPosterPath,
            VideoPosterBitmap = source.VideoPosterBitmap,

            HasTextOutline = source.HasTextOutline,
            TextOutlineSize = source.TextOutlineSize,
            TextOutlineColorHex = source.TextOutlineColorHex,
        };
    }

    // ==========================================
    // HISTORIAL DE DESHACER Y REHACER
    // ==========================================

    /// <summary>
    /// Una foto del estado, para el historial: los objetos de UN lienzo y todos los ajustes del
    /// tema. (Antes el historial guardaba solo la lista de objetos, y por eso los ajustes
    /// globales no se deshacían: la foto no los contenía.)
    /// </summary>
    private sealed class FotoDelTema
    {
        // De qué lienzo son los objetos. Sin esto, deshacer en Queue un cambio hecho en Playing
        // volcaba la foto de Playing encima de Queue.
        public int Pestana { get; init; }
        public List<WidgetItemViewModel> Objetos { get; init; } = new();

        // Los ajustes globales del tema en ese momento.
        public int QueueMaxItems { get; init; }
        public int QueueDirectionIndex { get; init; }
        public double QueueSpacing { get; init; }

        public int NotificationMaxItems { get; init; }
        public int NotificationDirectionIndex { get; init; }
        public double NotificationSpacing { get; init; }
        public int NotificationTimerModeIndex { get; init; }

        public double CanvasWidth { get; init; }
        public double CanvasHeight { get; init; }
        public string ThemeName { get; init; } = "";
        public string ThemeAuthor { get; init; } = "";
    }

    /// <summary>Hace una foto del estado actual, con los objetos del lienzo indicado.</summary>
    /// <param name="pestana">Lienzo cuyos objetos entran en la foto (0 a 3).</param>
    private FotoDelTema TomarFoto(int pestana) => new()
    {
        Pestana = pestana,

        // Clon completo de cada objeto. CloneWidget es quien decide qué propiedades sobreviven a
        // un Ctrl+Z: si alguna vez falta una ahí, se pierde al deshacer.
        Objetos = ColeccionDe(pestana).Select(w => CloneWidget(w)).ToList(),

        QueueMaxItems = QueueMaxItems,
        QueueDirectionIndex = QueueDirectionIndex,
        QueueSpacing = QueueSpacing,

        NotificationMaxItems = NotificationMaxItems,
        NotificationDirectionIndex = NotificationDirectionIndex,
        NotificationSpacing = NotificationSpacing,
        NotificationTimerModeIndex = NotificationTimerModeIndex,

        CanvasWidth = CanvasWidth,
        CanvasHeight = CanvasHeight,
        ThemeName = ThemeName,
        ThemeAuthor = ThemeAuthor
    };

    /// <summary>
    /// Vuelca una foto encima del estado actual. Se llama SIEMPRE con _isRestoringState en true:
    /// asignar estas propiedades dispara sus OnXChanging, y sin esa bandera cada restauración
    /// metería entradas nuevas en el historial.
    /// </summary>
    private void AplicarFoto(FotoDelTema foto)
    {
        // El lienzo de la FOTO, no el que se está mirando. Y se coge ANTES de vaciarlo:
        // CurrentCanvasObjects se recalcula en cada acceso, y el bucle de antes lo volvía a pedir
        // en cada Add, así que si algo cambiaba de pestaña a mitad, el resto caía en otro lienzo.
        var destino = ColeccionDe(foto.Pestana);
        destino.Clear();
        foreach (var item in foto.Objetos) destino.Add(item);

        QueueMaxItems = foto.QueueMaxItems;
        QueueDirectionIndex = foto.QueueDirectionIndex;
        QueueSpacing = foto.QueueSpacing;

        NotificationMaxItems = foto.NotificationMaxItems;
        NotificationDirectionIndex = foto.NotificationDirectionIndex;
        NotificationSpacing = foto.NotificationSpacing;
        NotificationTimerModeIndex = foto.NotificationTimerModeIndex;

        CanvasWidth = foto.CanvasWidth;
        CanvasHeight = foto.CanvasHeight;
        ThemeName = foto.ThemeName;
        ThemeAuthor = foto.ThemeAuthor;

        // Con el estado ya completo, el menú de pestañas se recalcula UNA vez (mientras se
        // restauraba no, ver el constructor) y se salta a la pestaña del cambio, para que el
        // Ctrl+Z se vea en vez de ocurrir en un lienzo que no se está mirando. La comprobación del
        // índice es solo un seguro: la foto se tomó con esa pestaña visible.
        RefreshWidgetTypeOptions();
        SelectedWidgetTypeIndex = foto.Pestana < WidgetTypeOptions.Count ? foto.Pestana : 0;

        // El panel de propiedades vuelve a enlazarse con los objetos recuperados. Va DESPUÉS del
        // cambio de pestaña, porque mira CurrentCanvasObjects.
        UpdateSelection();
    }

    // Las dos pilas del historial: la última foto que entra es la primera que sale.
    private Stack<FotoDelTema> _undoStack = new();
    private Stack<FotoDelTema> _redoStack = new();
    private bool _isRestoringState = false; // mientras se aplica una foto, SaveState no hace nada

    // Una foto hecha por adelantado, que todavía no está en el historial. Ver PrepararFoto.
    private FotoDelTema? _fotoPreparada;

    /// <summary>
    /// Hace la foto del estado actual pero NO la guarda todavía en el historial: solo entra si lo
    /// siguiente que pasa es un cambio de algo que se guarda (OnPropiedadDeUnObjetoCambiada). Lo
    /// usa el panel de propiedades con el teclado: una tecla en una casilla, un deslizador o un
    /// desplegable puede cambiar algo o no (una flecha en el último elemento, un Tab), y una foto
    /// guardada siempre dejaría Ctrl+Z que no hacen nada. Quien la prepara la descarta
    /// (DescartarFoto) en cuanto termina de procesar la tecla.
    /// </summary>
    public void PrepararFoto()
    {
        if (_isRestoringState) return;
        _fotoPreparada = TomarFoto(SelectedWidgetTypeIndex);
    }

    /// <summary>Tira la foto preparada, si no llegó a usarse. Ver PrepararFoto.</summary>
    public void DescartarFoto() => _fotoPreparada = null;

    /// <summary>
    /// Guarda una foto del estado ANTES de un cambio, para poder deshacerlo. Hay que llamarlo
    /// antes de tocar nada; si se llama después, la foto lleva ya el cambio y el Ctrl+Z no se nota.
    /// También marca el tema como modificado y borra lo que hubiera para rehacer.
    /// </summary>
    public void SaveState()
    {
        if (_isRestoringState) return;

        // Una foto preparada que no se usó ya no vale: la de ahora es más reciente. Si se quedara,
        // el siguiente cambio la metería en el historial DETRÁS de esta, y un Ctrl+Z saltaría dos
        // pasos atrás de golpe.
        _fotoPreparada = null;

        // Marca el tema como modificado. Salta al EMPEZAR a interactuar, así que puede marcar de
        // más (hacer clic en un campo y no tocar nada), nunca de menos: en un aviso de "vas a
        // perder cambios", equivocarse por exceso es el lado seguro. No es el único sitio que lo
        // marca: la red de seguridad (PropiedadesQueSeGuardan) recoge también los cambios que no
        // pasan por aquí.
        IsDirty = true;

        _undoStack.Push(TomarFoto(SelectedWidgetTypeIndex));
        _redoStack.Clear(); // un cambio nuevo invalida lo que había para rehacer
    }

    /// <summary>
    /// Deshacer (Ctrl+Z): vuelve a la última foto y guarda el estado actual para poder rehacerlo.
    ///
    /// El estado actual que se guarda es el del MISMO lienzo que la foto que se aplica, no el de
    /// la pestaña que se está mirando: es el que AplicarFoto va a pisar, así que es el que hay que
    /// poder devolver. Y la bandera va con try/finally: si se quedara en true, SaveState no
    /// volvería a guardar nada y el menú de pestañas dejaría de actualizarse, sin ningún síntoma
    /// que apunte aquí.
    /// </summary>
    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count == 0) return; // no hay nada que deshacer
        _fotoPreparada = null;   // es del estado de antes de deshacer: ya no vale
        _isRestoringState = true;
        try
        {
            var pasado = _undoStack.Pop();
            _redoStack.Push(TomarFoto(pasado.Pestana));   // el estado actual, para rehacer
            AplicarFoto(pasado);                          // y se vuelve a la foto
        }
        finally { _isRestoringState = false; }
    }

    /// <summary>
    /// Rehacer (Ctrl+Y): lo contrario de <see cref="Undo"/>, con las mismas reglas.
    /// </summary>
    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count == 0) return; // no hay nada que rehacer
        _fotoPreparada = null;   // ídem
        _isRestoringState = true;
        try
        {
            var futuro = _redoStack.Pop();
            _undoStack.Push(TomarFoto(futuro.Pestana));   // el estado actual, para deshacer
            AplicarFoto(futuro);                          // y se vuelve a la foto
        }
        finally { _isRestoringState = false; }
    }

    // ==========================================
    // DUPLICAR, COPIAR Y PEGAR
    // ==========================================

    // El portapapeles de objetos: copias de los que estaban seleccionados al copiar. Sobrevive a
    // abrir otro tema (por eso cada carga extrae sus recursos a su propia carpeta).
    private List<WidgetItemViewModel> _clipboardWidgets = new();

    /// <summary>
    /// Duplica los objetos seleccionados (Ctrl+D): cada copia aparece 20 píxeles más abajo y a la
    /// derecha, una capa por encima, sin seleccionar y con sus propios grupos de vínculo
    /// (RehacerGruposDeVinculo).
    /// </summary>
    [RelayCommand]
    private void DuplicateObject()
    {
        // Todos los seleccionados.
        var selected = CurrentCanvasObjects.Where(w => w.IsSelected).ToList();
        if (selected.Count == 0) return;

        // El paso del historial, DESPUÉS de comprobar que hay algo que duplicar: si no, Ctrl+D
        // sin nada seleccionado dejaba un Ctrl+Z que no hacía nada y marcaba el tema.
        SaveState();

        var copias = new List<WidgetItemViewModel>();
        foreach(var widget in selected)
        {
            var duplicate = CloneWidget(widget);
            duplicate.X += 20;
            duplicate.Y += 20;
            duplicate.LayerIndex += 1;

            // Las copias nacen sin seleccionar, para no confundirlas con los originales.
            duplicate.IsSelected = false;
            copias.Add(duplicate);
        }

        RehacerGruposDeVinculo(copias);
        foreach (var copia in copias) CurrentCanvasObjects.Add(copia);
    }

    /// <summary>
    /// Copia los objetos seleccionados al portapapeles de objetos (Ctrl+C). No pasa por el
    /// historial: copiar no cambia el tema. (Antes llamaba a SaveState, que dejaba un Ctrl+Z que
    /// no hacía nada y marcaba el tema como modificado.)
    /// </summary>
    [RelayCommand]
    private void CopyObject()
    {
        // Se guardan copias, no los objetos: si después se edita el original, lo copiado no cambia.
        var selected = CurrentCanvasObjects.Where(w => w.IsSelected).ToList();
        _clipboardWidgets = selected.Select(w => CloneWidget(w)).ToList();
    }

    /// <summary>
    /// Pega en el lienzo actual lo que haya en el portapapeles de objetos (Ctrl+V), con el mismo
    /// desplazamiento que al duplicar. Se puede pegar varias veces y en otra pestaña.
    /// </summary>
    [RelayCommand]
    private void PasteObject()
    {
        if (_clipboardWidgets.Count == 0) return;
        SaveState();

        var copias = new List<WidgetItemViewModel>();
        foreach(var clip in _clipboardWidgets)
        {
            var pastedWidget = CloneWidget(clip);
            pastedWidget.X += 20;
            pastedWidget.Y += 20;
            pastedWidget.LayerIndex += 1;
            pastedWidget.IsSelected = false;
            copias.Add(pastedWidget);
        }

        // Cada vez que se pega, grupos nuevos: pegar dos veces el mismo grupo da dos grupos.
        RehacerGruposDeVinculo(copias);
        foreach (var copia in copias) CurrentCanvasObjects.Add(copia);
    }

    /// <summary>
    /// Da grupos de vínculo NUEVOS a unas copias recién hechas (duplicar o pegar). CloneWidget
    /// copia el LinkedGroupId tal cual, y tiene que hacerlo, porque también alimenta el historial;
    /// pero una copia que se quedara con el grupo del original se seleccionaría y se movería junto
    /// a él (y pegada en otro lienzo, el grupo quedaría repartido entre los dos).
    ///
    /// Las copias que venían del mismo grupo comparten uno nuevo, así que un grupo copiado sigue
    /// vinculado entre sí, pero no con el original. Una copia que llega sola de su grupo queda
    /// sin vincular: un grupo de uno no tiene sentido.
    /// </summary>
    /// <param name="copias">Las copias, todavía sin añadir al lienzo.</param>
    private static void RehacerGruposDeVinculo(List<WidgetItemViewModel> copias)
    {
        var porGrupo = copias.Where(c => !string.IsNullOrEmpty(c.LinkedGroupId))
                             .GroupBy(c => c.LinkedGroupId)
                             .ToList();
        foreach (var grupo in porGrupo)
        {
            string nuevo = grupo.Count() > 1 ? Guid.NewGuid().ToString() : string.Empty;
            foreach (var copia in grupo) copia.LinkedGroupId = nuevo;
        }
    }

    // ==========================================
    // CAPAS
    // ==========================================

    /// <summary>Sube una capa los objetos seleccionados.</summary>
    [RelayCommand]
    private void MoveLayerUp()
    {
        var selected = CurrentCanvasObjects.Where(w => w.IsSelected).ToList();
        if (selected.Count == 0) return;

        SaveState();

        foreach (var w in selected)
        {
            w.LayerIndex++;
        }
    }

    /// <summary>Baja una capa los objetos seleccionados.</summary>
    [RelayCommand]
    private void MoveLayerDown()
    {
        var selected = CurrentCanvasObjects.Where(w => w.IsSelected).ToList();
        if (selected.Count == 0) return;

        SaveState();

        foreach (var w in selected)
        {
            w.LayerIndex--;
        }
    }

    /// <summary>
    /// "Pegar propiedades" del menú contextual: aplica a los objetos seleccionados el ESTILO del
    /// primer objeto del portapapeles (tamaño, colores, fuente, recorte, rotación, ajustes de
    /// texto, vídeo y halo). No copia la posición ni el contenido (la imagen, el vídeo o su
    /// fotograma).
    /// </summary>
    /// <param name="targetWidget">Objeto de respaldo, por si no hay ninguno seleccionado.</param>
        [RelayCommand]
    private void PasteProperties(WidgetItemViewModel? targetWidget)
    {
        if (_clipboardWidgets.Count == 0) return;

        // Se trabaja sobre la SELECCIÓN, igual que copiar y duplicar, y no sobre el parámetro.
        // Antes dependía de él y el menú contextual no lo pasaba, así que "Pegar propiedades" no
        // hacía nada y sin decir por qué. De paso, se pueden pegar propiedades a varios objetos de
        // una vez.
        var destinos = CurrentCanvasObjects.Where(w => w.IsSelected).ToList();

        // Por si alguien llama al comando con un objeto concreto y sin selección.
        if (destinos.Count == 0 && targetWidget != null) destinos.Add(targetWidget);
        if (destinos.Count == 0) return;

        SaveState();

        var source = _clipboardWidgets[0];

        // Se apaga la sincronización de la multiselección mientras se pega: aquí ya se recorren
        // todos los destinos a mano, y dejarla encendida haría que cada asignación al objeto
        // principal se propagara otra vez a los demás. Mismo resultado, el triple de trabajo, y un
        // camino más por donde puede colarse un efecto raro.
        _isSyncingProperties = true;
        try
        {
            foreach (var destino in destinos)
            {
                // Tamaño. NO se copia la posición (X/Y): pegaría el objeto encima del original.
                destino.Width = source.Width;
                destino.Height = source.Height;

                // Aspecto
                destino.TextColorHex = source.TextColorHex;
                destino.BackgroundColorHex = source.BackgroundColorHex;
                destino.BackgroundTypeIndex = source.BackgroundTypeIndex;
                destino.Transparency = source.Transparency;
                destino.BorderRoundness = source.BorderRoundness;
                destino.ForegroundColorHex = source.ForegroundColorHex;

                // Tipografía y efectos
                destino.FontSize = source.FontSize;
                destino.FontFamilyName = source.FontFamilyName;
                destino.CustomFontPath = source.CustomFontPath;
                destino.UseCustomFont = source.UseCustomFont;
                destino.FontBlur = source.FontBlur;

                // Ajuste de la imagen
                destino.StretchIndex = source.StretchIndex;

                // Transformaciones
                destino.RotationAngle = source.RotationAngle;
                destino.CropTop = source.CropTop;
                destino.CropBottom = source.CropBottom;
                destino.CropLeft = source.CropLeft;
                destino.CropRight = source.CropRight;

                // Ajustes de texto que también son "estilo"
                destino.TextAlignmentIndex = source.TextAlignmentIndex;
                destino.VerticalTextAlignmentIndex = source.VerticalTextAlignmentIndex;
                destino.TextPadding = source.TextPadding;
                destino.IsTextWrapped = source.IsTextWrapped;

                // Colores del cuadro de notificación. Faltaban aquí, y son puro estilo: es la MISMA
                // pareja que se olvidó en CloneWidget y que hacía que un Ctrl+Z los borrara.
                destino.RequesterColorHex = source.RequesterColorHex;
                destino.LevelDataColorHex = source.LevelDataColorHex;
                destino.NotificationDuration = source.NotificationDuration;

                // Ajustes de reproducción del vídeo: son ajustes, no contenido. El archivo
                // (ImageFilePath) y el fotograma (VideoPosterPath) se quedan FUERA: "pegar
                // propiedades" copia estilo, no contenido.
                destino.VideoLoop = source.VideoLoop;
                destino.VideoMuted = source.VideoMuted;

                // Contorno del texto: estilo puro, así que sí entra.
                destino.HasTextOutline = source.HasTextOutline;
                destino.TextOutlineSize = source.TextOutlineSize;
                destino.TextOutlineColorHex = source.TextOutlineColorHex;

                // Halo de la cara de dificultad (solo tiene efecto si el destino es una).
                // HaloBitmap no se copia: lo pone el temporizador de la demostración.
                destino.ShowHalo = source.ShowHalo;
                destino.HaloIndex = source.HaloIndex;
                destino.HaloScale = source.HaloScale;
            }
        }
        finally
        {
            _isSyncingProperties = false;
        }
    }

    // ==========================================
    // SELECCIÓN Y MULTISELECCIÓN
    // ==========================================
    // Con varios objetos seleccionados, el panel de propiedades enseña el PRIMERO (el "principal")
    // y lo que se cambie en él se copia a los demás (OnTrackedWidgetPropertyChanged).
    private WidgetItemViewModel? _currentTrackedWidget;
    // true mientras se copian propiedades a mano a varios objetos: la sincronización automática no
    // debe intervenir. Todo el que la pone en true la devuelve a false en un finally.
    private bool _isSyncingProperties = false;

    /// <summary>
    /// Actualiza el panel de propiedades con la selección actual del lienzo. Hay que llamarlo cada
    /// vez que cambia la selección.
    /// </summary>
    public void UpdateSelection()
    {
        var selected = CurrentCanvasObjects.Where(w => w.IsSelected).ToList();

        if (_currentTrackedWidget != null)
            _currentTrackedWidget.PropertyChanged -= OnTrackedWidgetPropertyChanged;

        if (selected.Count > 0)
        {
            _currentTrackedWidget = selected[0];
            _currentTrackedWidget.PropertyChanged += OnTrackedWidgetPropertyChanged;

            PropertiesVM.SelectedWidget = _currentTrackedWidget;
            PropertiesVM.SelectionTitle = selected.Count == 1 ? _currentTrackedWidget.ObjectName : EditorI18n.Trf("UI_MultiSelection", selected.Count);

            PropertiesVM.ShowTextProperties = selected.All(w => w.IsText);
            PropertiesVM.ShowMediaProperties = selected.All(w => w.IsMedia);
        }
        else
        {
            PropertiesVM.SelectedWidget = null;
            PropertiesVM.SelectionTitle = EditorI18n.Tr("UI_NoSelection");
            PropertiesVM.ShowTextProperties = false;
            PropertiesVM.ShowMediaProperties = false;
        }
    }

    /// <summary>
    /// Cuando cambia una propiedad del objeto principal, la copia a los demás objetos
    /// seleccionados, por reflexión. Se salta la posición, el tamaño, la rotación, el recorte y la
    /// selección, que son de cada objeto. Y si lo que cambia es el nombre, actualiza el título del
    /// panel.
    /// </summary>
    private void OnTrackedWidgetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not WidgetItemViewModel sourceWidget) return;

        // Con un solo objeto seleccionado, el título del panel es su nombre, y tiene que seguirlo
        // si cambia con el objeto ya seleccionado: al escribir el contenido de un texto, o al
        // elegir el archivo de un vídeo, que le pone el nombre del archivo. (Antes el título era
        // una copia hecha al seleccionar, y se quedaba con el nombre viejo.) Con varios, el título
        // es "Selección múltiple (N)" y no depende del nombre.
        if (e.PropertyName == nameof(WidgetItemViewModel.ObjectName) &&
            CurrentCanvasObjects.Count(w => w.IsSelected) == 1)
        {
            PropertiesVM.SelectionTitle = sourceWidget.ObjectName;
        }

        if (_isSyncingProperties) return;

        // Las propiedades que son de cada objeto no se copian: si no, mover o redimensionar el
        // principal aplastaría las medidas de los demás.
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == "X" || e.PropertyName == "Y" ||
            e.PropertyName == "Width" || e.PropertyName == "Height" ||
            e.PropertyName == "RotationAngle" ||
            e.PropertyName == "IsSelected" ||
            e.PropertyName.StartsWith("Crop")) return;

        var selectedOthers = CurrentCanvasObjects.Where(w => w.IsSelected && w != sourceWidget).ToList();
        if (selectedOthers.Count == 0) return;

        // try/finally: si un SetValue falla a mitad, la bandera tiene que bajar igual. Con la red
        // de excepciones del Editor, un fallo aquí ya no cierra la aplicación, así que sin esto la
        // bandera se quedaría en true y la multiselección dejaría de sincronizar, sin avisar, el
        // resto de la sesión. Los tres sitios que la ponen en true la devuelven en un finally.
        _isSyncingProperties = true;
        try
        {
            var prop = typeof(WidgetItemViewModel).GetProperty(e.PropertyName);
            if (prop != null && prop.CanWrite)
            {
                var value = prop.GetValue(sourceWidget);
                foreach (var target in selectedOthers)
                {
                    prop.SetValue(target, value);
                }
            }
        }
        finally
        {
            _isSyncingProperties = false;
        }
    }

    // ==========================================
    // VÍNCULOS ENTRE OBJETOS (CTRL+L)
    // ==========================================
    // Los objetos vinculados comparten un LinkedGroupId: al hacer clic en uno se seleccionan todos
    // (lo hace MainWindow.axaml.cs), así que se mueven juntos.

    /// <summary>Vincula los objetos seleccionados en un grupo nuevo, si hay al menos dos.</summary>
    [RelayCommand]
    private void LinkSelectedObjects()
    {
        var selected = CurrentCanvasObjects.Where(x => x.IsSelected).ToList();

        // Un grupo solo tiene sentido con dos objetos o más. El paso del historial va DESPUÉS de
        // comprobarlo: con menos, no cambia nada y no debe dejar un Ctrl+Z vacío.
        if (selected.Count < 2) return;
        SaveState();

        string newGroupId = Guid.NewGuid().ToString(); // identificador único del grupo
        foreach (var item in selected)
        {
            item.LinkedGroupId = newGroupId;
        }
    }

    /// <summary>Saca de su grupo a los objetos seleccionados.</summary>
    [RelayCommand]
    private void UnlinkSelectedObjects()
    {
        var selected = CurrentCanvasObjects.Where(x => x.IsSelected).ToList();

        // Si ninguno está vinculado, no hay nada que deshacer: sin paso en el historial.
        if (!selected.Any(x => !string.IsNullOrEmpty(x.LinkedGroupId))) return;
        SaveState();

        foreach (var item in selected)
        {
            item.LinkedGroupId = string.Empty;
        }
    }

    // ==========================================
    // TARJETAS FANTASMA DE LA COLA
    // ==========================================
    // Lo último que se calculó, para no rehacer las tarjetas si no ha cambiado nada.
    private Avalonia.Rect _lastQueueBoundingBox;
    private int _lastQueueMaxItems;
    private int _lastQueueDirection;
    private double _lastQueueSpacing;

    /// <summary>
    /// Recalcula las tarjetas fantasma de la cola: la caja que envuelve la plantilla y, a partir
    /// de ella, una tarjeta por cada nivel de más que cabe, en fila o en columna. Es el mismo
    /// cálculo que getBoundingBox de engine.js. Corre cada 50 ms, pero solo rehace la lista si algo
    /// ha cambiado.
    /// </summary>
    public void UpdateQueuePreviewBoxes()
    {
        // Se calculan SIEMPRE, se esté en la pestaña que se esté y esté como esté la casilla de
        // mostrarlas: el tamaño exportado de un tema de un solo tipo las necesita. Lo que depende
        // de la pestaña y de la casilla es si se VEN (el IsVisible de MainWindow.axaml), no si
        // existen.
        if (QueueObjects.Count == 0)
        {
            if (QueuePreviewBoxes.Count > 0) QueuePreviewBoxes.Clear();
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        // 1. La caja que envuelve la plantilla, contando el recorte y la rotación de cada objeto.
        foreach (var w in QueueObjects)
        {
            // La rotación gira alrededor del centro del objeto sin recortar.
            double cx = w.X + (w.Width / 2.0);
            double cy = w.Y + (w.Height / 2.0);

            // La parte visible, una vez quitado el recorte.
            double left = w.X + w.CropLeft;
            double right = w.X + w.Width - w.CropRight;
            double top = w.Y + w.CropTop;
            double bottom = w.Y + w.Height - w.CropBottom;

            // Si el recorte deja el objeto sin nada visible, no cuenta.
            if (left >= right || top >= bottom) continue;

            // Las cuatro esquinas de la parte visible.
            double[] cornersX = { left, right, left, right };
            double[] cornersY = { top, top, bottom, bottom };

            // Rotación de cada esquina alrededor del centro.
            double rad = w.RotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            for (int i = 0; i < 4; i++)
            {
                double dx = cornersX[i] - cx;
                double dy = cornersY[i] - cy;

                double rotatedX = cx + (dx * cos - dy * sin);
                double rotatedY = cy + (dx * sin + dy * cos);

                if (rotatedX < minX) minX = rotatedX;
                if (rotatedX > maxX) maxX = rotatedX;
                if (rotatedY < minY) minY = rotatedY;
                if (rotatedY > maxY) maxY = rotatedY;
            }
        }

        // Todos los objetos recortados del todo: no hay caja, y tampoco tarjetas. Se vacía la
        // lista, como cuando no hay objetos; antes se salía sin vaciarla, y se quedaban las
        // últimas tarjetas calculadas (que además inflaban el tamaño exportado).
        if (minX == double.MaxValue)
        {
            if (QueuePreviewBoxes.Count > 0) QueuePreviewBoxes.Clear();
            return;
        }

        var currentBox = new Avalonia.Rect(minX, minY, maxX - minX, maxY - minY);

        // 2. Si nada ha cambiado, no se rehace la lista: el lienzo no tiene que redibujar.
        if (currentBox == _lastQueueBoundingBox && _lastQueueMaxItems == QueueMaxItems &&
            _lastQueueDirection == QueueDirectionIndex && _lastQueueSpacing == QueueSpacing &&
            QueuePreviewBoxes.Count == (QueueMaxItems - 1))
        {
            return;
        }

        _lastQueueBoundingBox = currentBox;
        _lastQueueMaxItems = QueueMaxItems;
        _lastQueueDirection = QueueDirectionIndex;
        _lastQueueSpacing = QueueSpacing;

        // 3. Una tarjeta por cada nivel que cabe además del primero (el primero es la plantilla).
        QueuePreviewBoxes.Clear();
        for (int i = 1; i < QueueMaxItems; i++)
        {
            double offsetX = QueueDirectionIndex == 1 ? i * (currentBox.Width + QueueSpacing) : 0;
            double offsetY = QueueDirectionIndex == 0 ? i * (currentBox.Height + QueueSpacing) : 0;
            QueuePreviewBoxes.Add(new QueueGhostBox(minX + offsetX, minY + offsetY, currentBox.Width, currentBox.Height));
        }
    }

    // ==========================================
    // TARJETAS FANTASMA DE LAS NOTIFICACIONES
    // ==========================================
    // Igual que las de la cola, con los ajustes de las notificaciones.
    private Avalonia.Rect _lastNotifBoundingBox;
    private int _lastNotifMaxItems;
    private int _lastNotifDirection;
    private double _lastNotifSpacing;

    /// <summary>
    /// Recalcula las tarjetas fantasma de las notificaciones. Ver UpdateQueuePreviewBoxes.
    /// </summary>
    public void UpdateNotificationPreviewBoxes()
    {
        // Siempre, como las de la cola: la casilla solo decide si se dibujan.
        if (NotificationObjects.Count == 0)
        {
            if (NotificationPreviewBoxes.Count > 0) NotificationPreviewBoxes.Clear();
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        foreach (var w in NotificationObjects)
        {
            double cx = w.X + (w.Width / 2.0);
            double cy = w.Y + (w.Height / 2.0);
            double left = w.X + w.CropLeft;
            double right = w.X + w.Width - w.CropRight;
            double top = w.Y + w.CropTop;
            double bottom = w.Y + w.Height - w.CropBottom;

            if (left >= right || top >= bottom) continue;

            double[] cornersX = { left, right, left, right };
            double[] cornersY = { top, top, bottom, bottom };

            double rad = w.RotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            for (int i = 0; i < 4; i++)
            {
                double dx = cornersX[i] - cx;
                double dy = cornersY[i] - cy;
                double rotatedX = cx + (dx * cos - dy * sin);
                double rotatedY = cy + (dx * sin + dy * cos);

                if (rotatedX < minX) minX = rotatedX;
                if (rotatedX > maxX) maxX = rotatedX;
                if (rotatedY < minY) minY = rotatedY;
                if (rotatedY > maxY) maxY = rotatedY;
            }
        }

        // Todos recortados del todo: sin tarjetas (ver UpdateQueuePreviewBoxes).
        if (minX == double.MaxValue)
        {
            if (NotificationPreviewBoxes.Count > 0) NotificationPreviewBoxes.Clear();
            return;
        }
        var currentBox = new Avalonia.Rect(minX, minY, maxX - minX, maxY - minY);

        if (currentBox == _lastNotifBoundingBox && _lastNotifMaxItems == NotificationMaxItems &&
            _lastNotifDirection == NotificationDirectionIndex && _lastNotifSpacing == NotificationSpacing &&
            NotificationPreviewBoxes.Count == (NotificationMaxItems - 1)) return;

        _lastNotifBoundingBox = currentBox;
        _lastNotifMaxItems = NotificationMaxItems;
        _lastNotifDirection = NotificationDirectionIndex;
        _lastNotifSpacing = NotificationSpacing;

        NotificationPreviewBoxes.Clear();
        for (int i = 1; i < NotificationMaxItems; i++)
        {
            double offsetX = NotificationDirectionIndex == 1 ? i * (currentBox.Width + NotificationSpacing) : 0;
            double offsetY = NotificationDirectionIndex == 0 ? i * (currentBox.Height + NotificationSpacing) : 0;
            NotificationPreviewBoxes.Add(new QueueGhostBox(minX + offsetX, minY + offsetY, currentBox.Width, currentBox.Height));
        }
    }

    // ==========================================
    // NOTIFICACIONES DE DEMOSTRACIÓN EN EL LIENZO
    // ==========================================
    /// <summary>
    /// Anima en el lienzo las notificaciones de demostración: cada objeto se apaga cuando pasa su
    /// duración y, cuando pasa la de todos (más un segundo), entra la siguiente notificación de
    /// la lista. Corre cada 50 ms.
    /// </summary>
    public void UpdateNotificationPreviewFades()
    {

        // La condición NO es "estoy en la pestaña Notification" sino "los objetos de notificación
        // están en pantalla", que incluye el lienzo Unified: allí se dibujan como fantasmas, y
        // UnifiedGhosts guarda LAS MISMAS INSTANCIAS. Con la condición de la pestaña, el cuadro de
        // notificación enseñaba allí su texto de diseño en lugar de la notificación de muestra.
        // IsNotifGhostsVisible significa exactamente eso (pestaña 2 o 3).
        if (!IsNotifGhostsVisible || NotificationObjects.Count == 0) return;

        double maxDuration = 0;
        foreach (var w in NotificationObjects)
        {
            if (w.IsPreviewEnabled && w.NotificationDuration > maxDuration)
            {
                maxDuration = w.NotificationDuration;
            }
        }

        if (maxDuration == 0) return;

        _notifPreviewElapsed += 0.05;

        if (_notifPreviewElapsed >= maxDuration + 1.0)
        {
            _notifPreviewElapsed = 0;
            _notifTextIndex++;
        }

        string user = _previewRequesters[_notifTextIndex % _previewRequesters.Length];
        string level = _previewLevelNames[_notifTextIndex % _previewLevelNames.Length];
        string template = _previewNotificationTexts[_notifTextIndex % _previewNotificationTexts.Length];
        string currentNotifText = template.Replace("<USUARIO>", user).Replace("<NIVEL>", level);

        foreach (var w in NotificationObjects)
        {
            if (!w.IsPreviewEnabled)
            {
                w.PreviewOpacity = 1.0;
                continue;
            }

            if (w.ObjectType == WidgetObjectType.NotificationBox)
            {
                // El usuario y el nivel por separado, para que el lienzo los pinte en sus colores.
                w.PreviewUser = user;
                w.PreviewLevel = level;

                if (w.PreviewText != currentNotifText)
                {
                    w.PreviewText = currentNotifText;
                }
            }

            if (_notifPreviewElapsed >= w.NotificationDuration)
            {
                w.PreviewOpacity = 0.0;
            }
            else
            {
                w.PreviewOpacity = 1.0;
            }
        }
    }

    // ==========================================
    // NOTIFICACIONES DE DEMOSTRACIÓN EN EL NAVEGADOR
    // ==========================================

    /// <summary>
    /// Envía a la vista previa del navegador una notificación de demostración cada cierto tiempo
    /// (ver IntervaloNotificacionNavegador). Corre cada 50 ms.
    ///
    /// NO mira la pestaña activa, y es a propósito: antes este envío vivía dentro de la animación
    /// del lienzo, que no hace nada fuera de la pestaña Notification. Como la vista previa enseña
    /// el widget unificado, diseñando en Unified no salía ni una notificación hacia el navegador.
    /// De la pestaña solo debe depender lo que se VE, no lo que se calcula.
    ///
    /// Tampoco mira IsPreviewEnabled ni IsNotificationPreviewVisible: son ajustes del lienzo
    /// (opacidad y fantasmas). El navegador enseña el widget de verdad.
    /// </summary>
    public void UpdatePreviewNotificationBroadcast()
    {
        if (!PreviewServerService.Instance.IsRunning || NotificationObjects.Count == 0) return;

        double maxDur = 0;
        foreach (var w in NotificationObjects)
            if (w.NotificationDuration > maxDur) maxDur = w.NotificationDuration;

        if (maxDur <= 0) return;

        _previewNotifElapsed += 0.05;
        if (_previewNotifElapsed < IntervaloNotificacionNavegador(maxDur)) return;

        _previewNotifElapsed = 0;
        _previewNotifIndex++;
        _previewNotifBurst++;

        _ = PreviewServerService.Instance.BroadcastAsync(
                BuildPreviewNotificationJson(_previewNotifIndex));
    }

    /// <summary>
    /// Cada cuántos segundos se envía una notificación al navegador. Al abrir la vista previa se
    /// envían rápido, para LLENAR la pila (que es justo lo que se quiere comprobar: cuántas caben,
    /// la dirección y el espaciado), y después al ritmo al que engine.js las retira.
    ///
    /// Enviar siempre cada 2 segundos parece mejor y NO lo es: la cola de espera de engine.js
    /// (pendingNotifs) no tiene tope, así que en cuanto se envían más rápido de lo que caducan se
    /// acumulan, y a los pocos minutos la vista previa va enseñando notificaciones de hace un
    /// rato, cada vez más atrasada respecto al Editor.
    ///
    /// Y ese ritmo depende del MODO, no solo de la duración: en cascada (1, el de por defecto)
    /// engine.js solo descuenta el tiempo de la tarjeta más antigua, así que retira UNA por cada
    /// duración por muchos huecos que haya; en simultáneo las descuenta todas a la vez y retira
    /// tantas como caben en cada ciclo. El +0.5 es el mismo margen que suma engine.js a la
    /// duración de cada tarjeta.
    /// </summary>
    /// <param name="maxDur">La duración del objeto de notificación que más dura, en
    /// segundos.</param>
    private double IntervaloNotificacionNavegador(double maxDur)
    {
        if (_previewNotifBurst < Math.Max(1, NotificationMaxItems)) return 2.0;

        double huecos = NotificationTimerModeIndex == 1 ? 1.0 : Math.Max(1, NotificationMaxItems);
        return Math.Max(2.0, (maxDur + 0.5) / huecos);
    }

    // ==========================================
    // MARQUESINA EN EL LIENZO
    // ==========================================

    /// <summary>
    /// Desplaza en el lienzo los textos de una sola línea que no caben en su parte visible, igual
    /// que hace engine.js en el widget. Corre cada 50 ms. Recorre los cuatro lienzos, también el
    /// Unified (antes se lo saltaba, y ahí los textos largos se quedaban quietos).
    /// </summary>
    public void UpdateMarqueeAnimations()
    {
        foreach (var w in TodosLosObjetos())
        {
            // Solo los textos de una sola línea (sin salto de línea).
            if (w.IsText && !w.IsTextWrapped)
            {
                // Ancho REAL del texto, medido con su fuente. engine.js usa scrollWidth; esto
                // es lo equivalente en Avalonia, y por eso los dos coinciden.
                double estimatedWidth = w.MeasureDisplayText().W;

                // Solo se mueve si el texto mide más que la parte visible (sin el recorte).
                if (estimatedWidth > w.CroppedWidth)
                {
                    w.MarqueeOffset -= 2.0;

                    // El recorrido respeta el recorte: entra por el borde derecho visible y
                    // termina cuando la última letra sale por el izquierdo.
                    double startX = w.Width - w.CropRight;
                    double endX = w.CropLeft - estimatedWidth;

                    // Al terminar el recorrido, o si el recorte cambió de golpe, vuelve a
                    // empezar.
                    if (w.MarqueeOffset < endX || w.MarqueeOffset > startX)
                    {
                        w.MarqueeOffset = startX;
                    }
                }
                else
                {
                    // Si cabe, se queda quieto.
                    w.MarqueeOffset = 0;
                }
            }
            else
            {
                w.MarqueeOffset = 0;
            }
        }
    }

    // ==========================================
    // HALOS DE LA CARA DE DIFICULTAD EN EL LIENZO
    // ==========================================

    /// <summary>
    /// Pone a cada cara de dificultad el halo que le toca según su opción del panel. Corre en el
    /// temporizador rápido para que el cambio de opción se note al instante, y es barato: las
    /// imágenes salen de la caché, y asignar la misma no redibuja nada. Recorre los cuatro
    /// lienzos, también el Unified (antes se lo saltaba, y ahí el halo no rotaba).
    /// </summary>
    public void UpdateHaloBitmaps()
    {
        foreach (var w in TodosLosObjetos())
        {
            if (w.ObjectType != WidgetObjectType.RateFace) continue;

            // "Automático" es una simulación: solo tiene sentido con la vista previa encendida.
            if (w.HaloIndex == 0 && !w.IsPreviewEnabled)
            {
                w.HaloBitmap = null;
                continue;
            }

            w.HaloBitmap = ResolveHaloBitmap(w.HaloIndex, _previewIndex);
        }
    }

    // ==========================================
    // THEME.JSON EN VIVO
    // ==========================================
    // Contador de ciclos de 50 ms y el último JSON que se escribió en el editor.
    private int _liveJsonTicks = 0;
    private string _lastLiveJson = "";

    /// <summary>
    /// Mientras el editor de código esté mostrando theme.json, lo regenera cada medio segundo
    /// para que refleje el lienzo sin tener que cambiar de archivo. Si no ha cambiado, no toca
    /// el editor.
    /// </summary>
    public void RefreshLiveThemeJson()
    {
        if (!IsCodeEditorVisible) return;
        if (CodeEditorVM.CurrentFileName != "theme.json") return;

        // 50 ms × 10 = medio segundo. Serializar el tema entero 20 veces por segundo sería un
        // desperdicio.
        if (++_liveJsonTicks < 10) return;
        _liveJsonTicks = 0;

        string fresh = GenerateThemeJson();
        if (fresh == _lastLiveJson) return; // nada cambió: no se toca el editor

        _lastLiveJson = fresh;
        CodeEditorVM.FileBuffers["theme.json"] = fresh;
        CodeEditorVM.EditorDocument.Text = fresh;
    }

    // ==========================================
    // GENERACIÓN DEL THEME.JSON
    // ==========================================

    /// <summary>
    /// Escribe el theme.json del tema tal como está ahora (ver ThemeExport.cs): sus ajustes y
    /// todos los objetos de los cuatro lienzos, cada uno con su lienzo de origen. Es la única
    /// fuente de verdad: lo usan el guardado, la vista previa y el editor de código.
    /// </summary>
    /// <param name="nombres">Los nombres de los recursos dentro de assets/. Lo pasan EscribirZip y
    /// DumpThemeToFolder, que escriben los recursos con esos mismos nombres; quien no lo pasa (el
    /// theme.json en vivo y el editor de código) hace que se calcule aquí.</param>
    public string GenerateThemeJson(IReadOnlyDictionary<string, string>? nombres = null)
    {
        var mapa = nombres ?? CalcularNombresDeAssets();
        int activeCanvases = (PlayingObjects.Count > 0 ? 1 : 0) + (QueueObjects.Count > 0 ? 1 : 0) + (NotificationObjects.Count > 0 ? 1 : 0);

        // Es unificado si tiene objetos en más de uno de los tres lienzos normales, o si tiene
        // alguno en el lienzo Unified aunque los demás estén vacíos: esos objetos solo existen en
        // la página unificada.
        bool esUnificado = activeCanvases > 1 || UnifiedObjects.Count > 0;

        // Si no es unificado, el tipo es el del lienzo que tiene los objetos. Solo un tema vacío
        // toma el de la pestaña que se está mirando. (Antes salía siempre de la pestaña: con los
        // objetos solo en Queue, guardar mirando Playing escribía "playing", y SheepGDRequest, que
        // reparte los temas por su tipo, lo ponía en el menú que no era.)
        int lienzoDelTipo = PlayingObjects.Count > 0 ? 0
                          : QueueObjects.Count > 0 ? 1
                          : NotificationObjects.Count > 0 ? 2
                          : SelectedWidgetTypeIndex;
        string detectedWidgetType = esUnificado
            ? "unified"
            : (lienzoDelTipo switch { 0 => "playing", 1 => "queue", 2 => "notification", _ => "unknown" });

        // Tamaño exportado: el del lienzo de trabajo si es unificado; si no, el que ocupan los
        // objetos (ComputeExportedCanvasSize).
        double anchoExport = CanvasWidth, altoExport = CanvasHeight;
        if (!esUnificado)
        {
            var medido = ComputeExportedCanvasSize();
            anchoExport = medido.W;
            altoExport = medido.H;
        }

        var exportData = new ThemeExportData
        {
            ThemeName = ThemeName,
            Author = ThemeAuthor,
            WidgetType = detectedWidgetType,
            CanvasWidth = anchoExport,
            CanvasHeight = altoExport,
            QueueMaxItems = QueueMaxItems,
            QueueDirection = QueueDirectionIndex,
            QueueSpacing = QueueSpacing,

            // Ajustes globales de las notificaciones.
            NotificationMaxItems = NotificationMaxItems,
            NotificationDirection = NotificationDirectionIndex,
            NotificationSpacing = NotificationSpacing,
            NotificationTimerMode = NotificationTimerModeIndex
        };

        // Todos los objetos, cada uno con la carpeta de su lienzo (va a CanvasOrigin).
        var allWidgetsWithFolders = System.Linq.Enumerable.Concat(
            PlayingObjects.Select(w => new { Widget = w, Folder = "playing" }),
            System.Linq.Enumerable.Concat(QueueObjects.Select(w => new { Widget = w, Folder = "queue" }),
                System.Linq.Enumerable.Concat(NotificationObjects.Select(w => new { Widget = w, Folder = "notification" }),
                    UnifiedObjects.Select(w => new { Widget = w, Folder = "unified" })))
        );

        // La ruta de cada recurso sale del mapa, el mismo con el que se escriben los archivos. El
        // SanitizeFileName de respaldo no debería usarse nunca: solo se llegaría ahí si las
        // condiciones de abajo dejaran de ser las de CalcularNombresDeAssets.
        string Asset(string origen) =>
            "assets/" + (mapa.TryGetValue(origen, out var nombre) ? nombre : SanitizeFileName(origen));

        foreach (var item in allWidgetsWithFolders)
        {
            exportData.Objects.Add(new WidgetExportData
            {
                Type = item.Widget.ObjectType.ToString(),
                Name = item.Widget.ObjectName,
                X = item.Widget.X, Y = item.Widget.Y, Width = item.Widget.Width, Height = item.Widget.Height,
                Layer = item.Widget.LayerIndex, ColorHex = item.Widget.TextColorHex,
                FontSize = item.Widget.FontSize, Transparency = item.Widget.Transparency,
                FontBlur = item.Widget.FontBlur,
                BackgroundType = item.Widget.BackgroundTypeIndex,
                CanvasOrigin = item.Folder,
                FontFamily = item.Widget.FontFamilyName,
                CustomFontPath = item.Widget.UseCustomFont && !string.IsNullOrEmpty(item.Widget.CustomFontPath) ? Asset(item.Widget.CustomFontPath) : "",
                UseCustomFont = item.Widget.UseCustomFont,
                ImagePath = item.Widget.IsMedia && !string.IsNullOrEmpty(item.Widget.ImageFilePath) ? Asset(item.Widget.ImageFilePath) : "",
                StretchIndex = item.Widget.StretchIndex,
                RotationAngle = item.Widget.RotationAngle,
                CropTop = item.Widget.CropTop, CropBottom = item.Widget.CropBottom, CropLeft = item.Widget.CropLeft, CropRight = item.Widget.CropRight,
                LinkedGroupId = item.Widget.LinkedGroupId,
                BorderRoundness = item.Widget.BorderRoundness,
                BackgroundColorHex = item.Widget.BackgroundTypeIndex == 0 ? item.Widget.BackgroundColorHex : "",
                IsText = item.Widget.IsText, IsMedia = item.Widget.IsMedia, IsVideo = item.Widget.IsVideo,
                IsTextWrapped = item.Widget.IsTextWrapped,
                RequesterColorHex = item.Widget.RequesterColorHex, LevelDataColorHex = item.Widget.LevelDataColorHex,
                TextAlignment = item.Widget.TextAlignmentIndex, ForegroundColorHex = item.Widget.ForegroundColorHex,

                // Duración en las notificaciones y alineación y margen del texto.
                NotificationDuration = item.Widget.NotificationDuration,
                VerticalTextAlignment = item.Widget.VerticalTextAlignmentIndex,
                TextPadding = item.Widget.TextPadding,

                // Halo de la cara de dificultad y ajustes del vídeo.
                HaloEnabled = item.Widget.ShowHalo,
                HaloScale = item.Widget.HaloScale,
                VideoLoop = item.Widget.VideoLoop,
                VideoMuted = item.Widget.VideoMuted,
                VideoPoster = item.Widget.IsVideo && !string.IsNullOrEmpty(item.Widget.VideoPosterPath)
                    ? Asset(item.Widget.VideoPosterPath)
                    : "",

                // Contorno del texto.
                HasTextOutline = item.Widget.HasTextOutline,
                TextOutlineSize = item.Widget.TextOutlineSize,
                TextOutlineColor = item.Widget.TextOutlineColorHex
            });
        }

        // Con sangría, para que se pueda leer en el editor de código, y sin escapar las tildes.
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return JsonSerializer.Serialize(exportData, jsonOptions);
    }

    /// <summary>
    /// El tamaño con el que se EXPORTA un tema de un solo tipo: hasta el borde derecho e inferior
    /// más lejanos, contando las tarjetas fantasma de la cola y de las notificaciones.
    ///
    /// No se recorta por arriba ni por la izquierda, y es a propósito: eso obligaría a desplazar
    /// las coordenadas de todos los objetos al exportar, y engine.js ya hace su propio
    /// desplazamiento con getBoundingBox para las tarjetas de la cola y de las notificaciones.
    /// Los dos ajustes se pisarían. Se recorta lo que sobra abajo y a la derecha, y nada más.
    ///
    /// Esto NO cambia CanvasWidth ni CanvasHeight: el lienzo de trabajo tiene que seguir siendo
    /// grande, porque AddWidgetObject lo usa para centrar los objetos nuevos y para que no se
    /// salgan. Si encogiera, el lienzo se iría cerrando solo mientras se diseña. Por lo mismo,
    /// al abrir un tema de un solo tipo LoadTheme no copia este tamaño tal cual: se queda con el
    /// mayor entre él y el lienzo de trabajo que ya había.
    /// </summary>
    private (double W, double H) ComputeExportedCanvasSize()
    {
        double maxX = 0, maxY = 0;

        // Amplía el tamaño para que quepa un rectángulo.
        void Considerar(double x, double y, double w, double h)
        {
            if (x + w > maxX) maxX = x + w;
            if (y + h > maxY) maxY = y + h;
        }

        // Los objetos del lienzo Unified no cuentan: si hubiera alguno, el tema ya sería unificado
        // y no pasaría por aquí.
        foreach (var o in PlayingObjects.Concat(QueueObjects).Concat(NotificationObjects))
            Considerar(o.X, o.Y, o.Width, o.Height);

        // Las tarjetas fantasma también, para que quepan todas las tarjetas repetidas.
        foreach (var g in QueuePreviewBoxes) Considerar(g.X, g.Y, g.Width, g.Height);
        foreach (var g in NotificationPreviewBoxes) Considerar(g.X, g.Y, g.Width, g.Height);

        // Tema vacío: se devuelve el tamaño configurado, no un lienzo de 0x0.
        if (maxX <= 0 || maxY <= 0) return (CanvasWidth, CanvasHeight);

        return (Math.Ceiling(maxX), Math.Ceiling(maxY));
    }

    // ==========================================
    // EDITOR DE CÓDIGO
    // ==========================================

    /// <summary>
    /// Abre un archivo del tema en el editor de código: guarda en memoria lo escrito en el actual,
    /// regenera theme.json si es el que se abre (no se edita a mano), muestra el nuevo y elige su
    /// coloreado de sintaxis. Lo usan los botones de archivo del panel del editor de código.
    /// </summary>
    /// <param name="fileName">Ruta del archivo dentro del tema, por ejemplo
    /// "queue/index.html".</param>
    [RelayCommand]
    private void OpenCodeFile(string fileName)
    {
        // 1. Lo escrito en el archivo actual se guarda en memoria antes de cambiar. theme.json no:
        //    se genera, no se edita a mano.
        if (CodeEditorVM.IsDocumentLoaded &&
            CodeEditorVM.CurrentFileName != "theme.json" &&
            CodeEditorVM.FileBuffers.ContainsKey(CodeEditorVM.CurrentFileName))
        {
            CodeEditorVM.FileBuffers[CodeEditorVM.CurrentFileName] = CodeEditorVM.EditorDocument.Text;
        }

        // 2. theme.json se vuelve a generar a partir del lienzo cada vez que se abre.
        if (fileName == "theme.json")
        {
            string fresh = GenerateThemeJson();
            CodeEditorVM.FileBuffers["theme.json"] = fresh;
            _lastLiveJson = fresh; // así el refresco en vivo sabe que ya está al día
            _liveJsonTicks = 0;
        }

        // 3. Se muestra el archivo pedido.
        CodeEditorVM.CurrentFileName = fileName;
        CodeEditorVM.StatusMessage = fileName == "theme.json" ? EditorI18n.Tr("UI_ThemeJsonReadOnly") : "";

        if (CodeEditorVM.FileBuffers.TryGetValue(fileName, out string? savedContent))
        {
            CodeEditorVM.EditorDocument.Text = savedContent;
        }
        else
        {
            // El tema cargado no traía este archivo: se abre vacío.
            CodeEditorVM.FileBuffers[fileName] = "";
            CodeEditorVM.EditorDocument.Text = "";
        }

        // A partir de aquí el documento SÍ contiene lo que dice CurrentFileName (por cualquiera de
        // las dos ramas), así que ya se puede volcar de vuelta al diccionario sin miedo. Antes solo
        // se marcaba al abrir un archivo vacío: con el Editor recién abierto (sin haber abierto
        // ningún tema) la bandera seguía en false, y lo que se escribía en el editor de código no
        // llegaba nunca al diccionario ni al ZIP.
        CodeEditorVM.IsDocumentLoaded = true;

        // 4. Coloreado de sintaxis: theme.json y los .js usan el de JavaScript, los .css el de
        //    CSS y todo lo demás el de HTML. (El de JSON que trae AvaloniaEdit es para fondo
        //    claro: ver CodeEditorSideViewModel.SyntaxHighlighting.)
        if (fileName.EndsWith(".json") || fileName.EndsWith(".js"))
            CodeEditorVM.SyntaxHighlighting = AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition("JavaScript");
        else if (fileName.EndsWith(".css"))
            CodeEditorVM.SyntaxHighlighting = AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition("CSS");
        else
            CodeEditorVM.SyntaxHighlighting = AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition("HTML");
    }
}

