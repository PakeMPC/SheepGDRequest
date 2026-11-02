using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SheepGDEditor.Views;

/// <summary>
/// El código del panel de propiedades (PropertiesView.axaml): las formas en que los controles
/// avisan al historial de que empieza un cambio, con el ratón y con el teclado. Todo lo demás va
/// por enlaces.
/// </summary>
public partial class PropertiesView : UserControl
{
    // La tecla que se está manteniendo pulsada en un control del panel, o null si no hay ninguna.
    // Una tecla mantenida repite la pulsación muchas veces por segundo, y no puede ser un paso del
    // historial cada una (mantener una flecha en un deslizador lo enterraría): la foto se prepara
    // en la primera, y la serie se acaba al soltar la tecla, al hacer clic en el panel o al pasar
    // la ventana a segundo plano. Lo mismo que las flechas del lienzo en MainWindow.axaml.cs.
    private Key? _teclaEnSerie;

    // La ventana en la que está el panel, para enterarse de cuándo pasa a segundo plano.
    private Window? _ventana;

    /// <summary>
    /// Carga el panel desde PropertiesView.axaml y engancha los avisos al historial: los clics, los
    /// Click de los botones y las teclas que caen dentro del panel.
    /// </summary>
    public PropertiesView()
    {
        InitializeComponent();

        // Los clics, en TÚNEL y con handledEventsToo. En túnel, el clic pasa por aquí al bajar
        // hacia el control, antes de que la casilla, el deslizador o el desplegable lo atiendan y,
        // por tanto, antes de que cambie su valor. Y con handledEventsToo llega aunque algo lo haya
        // marcado ya como atendido. Un PointerPressed puesto en el XAML no servía: esos controles
        // marcan el clic como atendido, y un evento atendido ya no llega a los manejadores del
        // XAML.
        AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel,
                   handledEventsToo: true);

        // Los botones de color, por su Click, que salta igual con el ratón que con Intro o Espacio,
        // justo antes de que se abra el selector de color.
        AddHandler(Button.ClickEvent, OnPanelClick, RoutingStrategies.Bubble,
                   handledEventsToo: true);

        // El teclado: la foto se PREPARA al bajar hacia el control (túnel), el ViewModel la guarda
        // en el historial si la tecla llega a cambiar algo que se guarda, y si no, se descarta al
        // volver a subir (burbuja). Hacen falta las dos teclas, la que baja y la que sube, porque
        // una casilla cambia al SOLTAR el Espacio; y la entrada de texto, porque un desplegable
        // salta a una opción al escribir su primera letra.
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnPanelKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TextInputEvent, OnPanelTextInput, RoutingStrategies.Tunnel,
                   handledEventsToo: true);
        AddHandler(KeyDownEvent, OnPanelKeyDownTerminado, RoutingStrategies.Bubble,
                   handledEventsToo: true);
        AddHandler(KeyUpEvent, OnPanelKeyUpTerminado, RoutingStrategies.Bubble,
                   handledEventsToo: true);
        AddHandler(TextInputEvent, OnPanelTextInputTerminado, RoutingStrategies.Bubble,
                   handledEventsToo: true);
    }

    /// <summary>
    /// Al entrar en la ventana, se apunta para saber cuándo pasa a segundo plano.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ventana = TopLevel.GetTopLevel(this) as Window;
        if (_ventana != null) _ventana.Deactivated += OnVentanaDesactivada;
    }

    /// <summary>Al salir de la ventana, se desapunta.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_ventana != null) _ventana.Deactivated -= OnVentanaDesactivada;
        _ventana = null;
        base.OnDetachedFromVisualTree(e);
    }

    // Con la ventana en segundo plano, la tecla puede soltarse sin que llegue aquí: se cierra la
    // serie para que la próxima pulsación vuelva a dejar su paso.
    private void OnVentanaDesactivada(object? sender, System.EventArgs e) => _teclaEnSerie = null;

    /// <summary>
    /// Un clic en cualquier parte del panel. Si cae dentro de un control con la clase FotoAlPulsar
    /// (las casillas, los deslizadores y los desplegables), el historial hace su foto antes del
    /// cambio. Los botones de color también llevan la clase, pero van por su Click
    /// (<see cref="OnPanelClick"/>): si no, cada clic dejaría dos fotos. Los clics en el resto del
    /// panel (etiquetas, huecos, la barra de desplazamiento...) no hacen nada.
    /// </summary>
    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _teclaEnSerie = null;   // un clic cierra cualquier serie de teclas

        var control = ControlConFoto(e.Source);
        if (control == null || EsBotonDeColor(control)) return;
        HacerFoto();
    }

    /// <summary>
    /// El Click de un botón del panel. Si es un botón de color (FotoAlPulsar), el historial hace su
    /// foto antes de que se abra el selector, se haya pulsado con el ratón o con el teclado. Todo
    /// lo que se elija en el selector hasta cerrarlo es un solo paso de Ctrl+Z.
    /// </summary>
    private void OnPanelClick(object? sender, RoutedEventArgs e)
    {
        var control = ControlConFoto(e.Source);
        if (control != null && EsBotonDeColor(control)) HacerFoto();
    }

    /// <summary>
    /// Una tecla que baja hacia un control del panel. Si el control es de los que llevan
    /// FotoAlPulsar, se prepara la foto (la primera vez de cada serie, ver _teclaEnSerie).
    /// </summary>
    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (ControlConFoto(e.Source) == null || ConModificador(e)) return;
        if (_teclaEnSerie == e.Key) return;   // la misma tecla, mantenida: es la misma serie

        _teclaEnSerie = e.Key;
        Vm?.PrepararFoto();
    }

    /// <summary>
    /// Una tecla que se suelta sobre un control del panel: se prepara la foto, porque hay
    /// controles que cambian al soltar (la casilla, con el Espacio).
    /// </summary>
    private void OnPanelKeyUp(object? sender, KeyEventArgs e)
    {
        if (ControlConFoto(e.Source) != null && !ConModificador(e)) Vm?.PrepararFoto();
    }

    /// <summary>
    /// Si la tecla va con Ctrl o Alt: es un atajo, no un cambio del control, y no prepara foto.
    /// Sobre todo Ctrl+S, que funciona con el foco en cualquier sitio: guardar convierte las
    /// fuentes del sistema en fuentes propias (MainWindowViewModel.IncrustarFuentesDelSistema), y
    /// con una foto preparada eso habría quedado como un paso del historial.
    /// </summary>
    private static bool ConModificador(KeyEventArgs e) =>
        (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;

    /// <summary>Al volver a subir la tecla pulsada: se descarta la foto si no se usó.</summary>
    private void OnPanelKeyDownTerminado(object? sender, KeyEventArgs e) => DescartarFoto();

    /// <summary>Al volver a subir el texto escrito: se descarta la foto si no se usó.</summary>
    private void OnPanelTextInputTerminado(object? sender, TextInputEventArgs e) => DescartarFoto();

    /// <summary>
    /// Al volver a subir la tecla soltada: se descarta la foto si no se usó, y se acaba su serie.
    /// </summary>
    private void OnPanelKeyUpTerminado(object? sender, KeyEventArgs e)
    {
        DescartarFoto();
        if (_teclaEnSerie == e.Key) _teclaEnSerie = null;
    }

    /// <summary>
    /// Texto escrito sobre un control del panel (un desplegable salta a la opción que empieza por
    /// esa letra): se prepara la foto, como con las teclas.
    /// </summary>
    private void OnPanelTextInput(object? sender, TextInputEventArgs e)
    {
        if (ControlConFoto(e.Source) != null) Vm?.PrepararFoto();
    }

    /// <summary>
    /// El control con la clase FotoAlPulsar en el que está lo que ha recibido el evento, o null.
    /// El evento llega desde la pieza concreta (el texto de una casilla, el tirador de un
    /// deslizador...), así que se sube por el árbol hasta dar con el control marcado, sin salir
    /// del panel.
    /// </summary>
    private Visual? ControlConFoto(object? origen)
    {
        for (var v = origen as Visual; v != null && v != this; v = v.GetVisualParent())
        {
            if (v.Classes.Contains("FotoAlPulsar")) return v;
        }
        return null;
    }

    /// <summary>
    /// Si es uno de los botones de color: un Button normal, no una casilla (CheckBox también es un
    /// botón, pero cambia ANTES de lanzar su Click).
    /// </summary>
    private static bool EsBotonDeColor(Visual control) =>
        control is Button && control is not ToggleButton;

    /// <summary>
    /// Va en el GotFocus de los controles que se usan con el foco dentro: los campos numéricos y el
    /// cuadro de texto. Al entrar en uno, el historial hace su foto, y lo que se escriba hasta
    /// salir es un solo paso de Ctrl+Z.
    /// </summary>
    private void OnInteractionStarted(object? sender, RoutedEventArgs e)
    {
        HacerFoto();
    }

    /// <summary>El ViewModel de la ventana, que es quien lleva el historial.</summary>
    private ViewModels.MainWindowViewModel? Vm =>
        (TopLevel.GetTopLevel(this) as Window)?.DataContext as ViewModels.MainWindowViewModel;

    /// <summary>
    /// Le pide al ViewModel que haga su foto (SaveState). Así un Ctrl+Z vuelve a como estaba el
    /// objeto antes del cambio.
    /// </summary>
    private void HacerFoto()
    {
        Vm?.SaveState();
    }

    /// <summary>Tira la foto preparada por una tecla que no cambió nada.</summary>
    private void DescartarFoto()
    {
        Vm?.DescartarFoto();
    }
}
