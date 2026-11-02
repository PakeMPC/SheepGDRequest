using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using SheepGDEditor.ViewModels;
using System;
using System.Threading.Tasks;
using System.Linq;
namespace SheepGDEditor.Views;
using SheepGDEditor.Services;

/// <summary>
/// El código de la ventana principal: todo lo que el lienzo hace con el ratón y el teclado, y
/// que no se puede expresar con enlaces. Seleccionar (clic, Ctrl+clic y recuadro), arrastrar,
/// cambiar el tamaño, recortar y girar con los tiradores, desplazar el lienzo con el botón
/// central, el zoom con Ctrl+rueda, los atajos de teclado y el aviso de cambios sin guardar al
/// cerrar.
///
/// Las posiciones del ratón se leen sobre WorkspaceContainer, el Panel que lleva el zoom y el
/// desplazamiento, así que ya son coordenadas del tema y no de la pantalla. La excepción es el
/// desplazamiento del lienzo, que mide sobre la ventana (ver OnBackgroundPointerPressed).
/// </summary>
public partial class MainWindow : Window
{
    // Arrastre de objetos: el último punto del ratón y si se está arrastrando. Para alternar los
    // tiradores al soltar, también si el objeto ya estaba seleccionado antes del clic y si el
    // ratón se ha llegado a mover.
    private Point _lastDragPoint;
    private bool _isDraggingWidget = false;
    private bool _wasAlreadySelected = false;
    private bool _hasDragged = false;

    // Giro: si se está girando y el ángulo del ratón respecto al centro al empezar. Lo de cada
    // objeto va en _groupRotationStates.
    private bool _isRotating = false;
    private double _initialMouseAngle;

    // Giro de todo lo seleccionado a la vez: cada objeto gira sobre sí mismo y, además, su centro
    // da la vuelta alrededor del centro del grupo. Se apunta cómo estaba cada uno al empezar, y
    // todo se calcula desde ahí.
    private class GroupRotState {
        public ViewModels.WidgetItemViewModel Widget { get; set; } = null!;
        public double InitialX { get; set; }
        public double InitialY { get; set; }
        public double InitialRotation { get; set; }
    }
    private System.Collections.Generic.List<GroupRotState> _groupRotationStates = new();
    private Point _groupRotationCenter;

    // El recuadro de selección: dónde empezó, si se está dibujando y el Rectangle que lo pinta
    // (SelectionBox, en el .axaml).
    private Point _selectionStartPoint;
    private bool _isSelecting = false;
    private Avalonia.Controls.Shapes.Rectangle? _selectionBox;

    // Desplazamiento del lienzo con el botón central.
    private bool _isPanning = false;
    private Point _lastPanPoint;

    // Recorte con los tiradores rosas: qué lado (el Name del tirador), dónde empezó el ratón y
    // cuánto recorte había entonces. El recorte se calcula siempre desde ese punto de partida, y
    // no sumando movimientos, para que no se desincronice.
    private bool _isCropping = false;
    private string _cropDirection = "";
    private Point _cropStartPoint;
    private double _initialCropValue;
    private ViewModels.WidgetItemViewModel? _croppingWidget;

    // Mover con las flechas: si ya se ha hecho la foto del historial en esta serie de
    // pulsaciones. Una flecha mantenida repite la pulsación muchas veces por segundo, y no puede
    // ser un paso del historial cada una: la foto se hace en la primera, y la serie se acaba al
    // soltar la flecha (OnWindowKeyUp) o al pasar la ventana a segundo plano, por si la tecla se
    // suelta con otra ventana delante y aquí no llega.
    private bool _fotoDeFlechasHecha = false;

    /// <summary>
    /// Carga la ventana desde MainWindow.axaml y engancha lo que cierra una serie de pulsaciones
    /// de las flechas (ver _fotoDeFlechasHecha).
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // Con handledEventsToo: soltar la tecla tiene que cerrar la serie aunque algún control la
        // haya marcado como atendida por el camino.
        AddHandler(KeyUpEvent, OnWindowKeyUp, handledEventsToo: true);
        Deactivated += (_, _) => _fotoDeFlechasHecha = false;
    }

    // ==========================================
    // CLIC Y ARRASTRE DE OBJETOS
    // ==========================================

    /// <summary>
    /// Clic sobre un objeto. Sin Ctrl, si no estaba seleccionado, pasa a ser el único
    /// seleccionado; con Ctrl, entra o sale de la selección. Si es de un grupo vinculado, el grupo
    /// entero le sigue: entra con él o sale con él. Además guarda un paso en el historial (aunque
    /// luego no se mueva nada) y empieza el arrastre.
    /// </summary>
    private void OnWidgetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control control && control.DataContext is ViewModels.WidgetItemViewModel widget)
        {
            var vm = DataContext as ViewModels.MainWindowViewModel;
            if (vm == null) return;

            // El lienzo se queda con el foco: es lo que hace que las flechas y los atajos sean
            // SUYOS y no del último control del panel que se tocó. Ver ElFocoEstaEnElLienzo().
            this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer")?.Focus();

            // Si el objeto ya estaba seleccionado ANTES del clic. Al soltar sin arrastrar, eso
            // alterna sus tiradores entre tamaño y giro, como en Inkscape.
            _wasAlreadySelected = widget.IsSelected;
            _hasDragged = false;

            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                widget.IsSelected = !widget.IsSelected;
            }
            else
            {
                if (!widget.IsSelected)
                {
                    foreach (var w in vm.CurrentCanvasObjects) w.IsSelected = false;
                    widget.IsSelected = true;
                }
            }

            // Un objeto vinculado se lleva a todo su grupo: si ha quedado seleccionado, entra el
            // grupo entero; si ha salido (Ctrl+clic sobre uno que ya lo estaba), sale el grupo
            // entero. (Antes el grupo se seleccionaba siempre, así que Ctrl+clic sacaba al objeto
            // y el grupo lo volvía a meter en el mismo clic.)
            if (!string.IsNullOrEmpty(widget.LinkedGroupId))
            {
                bool seleccionado = widget.IsSelected;
                foreach (var w in vm.CurrentCanvasObjects.Where(x => x.LinkedGroupId == widget.LinkedGroupId))
                {
                    w.IsSelected = seleccionado;
                }
            }

            // El panel de propiedades sigue a la selección, y el historial apunta cómo estaba todo
            // antes del posible arrastre.
            vm.UpdateSelection();
            vm.SaveState();

            // El punto de partida del arrastre, en coordenadas del lienzo.
            _lastDragPoint = e.GetPosition(this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer"));
            _isDraggingWidget = true;

            e.Handled = true;
        }
    }

    /// <summary>
    /// Doble clic sobre un objeto: abre el panel de propiedades, como el botón del lápiz.
    /// </summary>
    private void OnWidgetDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            if (vm.ShowPropertiesCommand.CanExecute(null))
            {
                vm.ShowPropertiesCommand.Execute(null);
            }
        }

        // Que no llegue también al fondo del lienzo (OnBackgroundDoubleTapped).
        e.Handled = true;
    }

    /// <summary>
    /// Doble clic en el fondo del lienzo: si cae sobre un fantasma del lienzo unificado, salta a
    /// la pestaña de ese objeto y abre sus propiedades.
    ///
    /// La capa de fantasmas tiene IsHitTestVisible="False", así que el evento llega aquí, al
    /// fondo. Y como WorkspaceContainer es el Panel que lleva el zoom y el desplazamiento,
    /// e.GetPosition sobre él ya da coordenadas del lienzo: no hay que deshacer nada.
    /// </summary>
    private void OnBackgroundDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;

        var panel = this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer");
        if (panel == null) return;

        var p = e.GetPosition(panel);
        if (!vm.JumpToGhostAt(p.X, p.Y)) return;   // no había fantasma ahí: que siga su curso

        // Mismo gesto que el doble clic sobre un objeto real: abrir Propiedades.
        if (vm.ShowPropertiesCommand.CanExecute(null)) vm.ShowPropertiesCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Arrastre: mueve todos los objetos seleccionados lo mismo que el ratón. En cuanto llega un
    /// movimiento sin el botón izquierdo pulsado, el arrastre se acaba.
    /// </summary>
    private void OnWidgetPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingWidget && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var currentPoint = e.GetPosition(this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer"));

            // Más de 2 píxeles cuenta como arrastre. Menos es un clic con algo de pulso, y no debe
            // impedir que el clic alterne los tiradores.
            if (Math.Abs(currentPoint.X - _lastDragPoint.X) > 2 || Math.Abs(currentPoint.Y - _lastDragPoint.Y) > 2)
            {
                _hasDragged = true;
            }

            double deltaX = currentPoint.X - _lastDragPoint.X;
            double deltaY = currentPoint.Y - _lastDragPoint.Y;

            var vm = DataContext as ViewModels.MainWindowViewModel;
            if (vm != null)
            {
                foreach (var w in vm.CurrentCanvasObjects.Where(x => x.IsSelected))
                {
                    w.X += deltaX;
                    w.Y += deltaY;
                }
            }

            _lastDragPoint = currentPoint;
            e.Handled = true;
        }
        else
        {
            _isDraggingWidget = false;
        }
    }

    /// <summary>
    /// Al soltar: si el objeto ya estaba seleccionado y no se ha arrastrado, alterna sus tiradores
    /// entre los de tamaño y los de giro, como en Inkscape.
    /// </summary>
    private void OnWidgetPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control control && control.DataContext is ViewModels.WidgetItemViewModel widget)
        {
            if (_wasAlreadySelected && !_hasDragged)
            {
                widget.IsRotationMode = !widget.IsRotationMode;
            }
        }

        _isDraggingWidget = false;
        e.Handled = true;
    }

    // ==========================================
    // GIRO
    // ==========================================

    /// <summary>
    /// Clic en un tirador de giro: guarda un paso en el historial, calcula el centro del grupo
    /// seleccionado (el de la caja que los envuelve, sin contar el giro que ya tengan) y apunta
    /// cómo estaba cada objeto. El ratón queda capturado hasta soltarlo.
    /// </summary>
    private void OnRotatePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Border border && border.DataContext is ViewModels.WidgetItemViewModel widget)
        {
            var vm = DataContext as ViewModels.MainWindowViewModel;
            if (vm == null) return;
            vm.SaveState();

            _isRotating = true;

            // 1. Todos los seleccionados.
            var selected = vm.CurrentCanvasObjects.Where(w => w.IsSelected).ToList();

            // 2. El centro del grupo: el de la caja que los envuelve.
            double minX = selected.Min(w => w.X);
            double minY = selected.Min(w => w.Y);
            double maxX = selected.Max(w => w.X + w.Width);
            double maxY = selected.Max(w => w.Y + w.Height);
            double centerX = minX + (maxX - minX) / 2.0;
            double centerY = minY + (maxY - minY) / 2.0;
            _groupRotationCenter = new Point(centerX, centerY);

            // 3. Cómo estaba cada objeto al empezar.
            _groupRotationStates.Clear();
            foreach (var w in selected)
            {
                _groupRotationStates.Add(new GroupRotState {
                    Widget = w, InitialX = w.X, InitialY = w.Y, InitialRotation = w.RotationAngle
                });
            }

            var container = this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer");
            var mouse = e.GetPosition(container);

            // Ángulo inicial del ratón respecto al centro del GRUPO
            _initialMouseAngle = Math.Atan2(mouse.Y - centerY, mouse.X - centerX) * (180 / Math.PI);

            e.Pointer.Capture(border);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Al mover: gira cada objeto el mismo ángulo que ha girado el ratón alrededor del centro del
    /// grupo, y lleva su centro alrededor de ese mismo punto.
    /// </summary>
    private void OnRotatePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isRotating && _groupRotationStates.Count > 0)
        {
            var container = this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer");
            var mouse = e.GetPosition(container);

            // Cuántos grados ha girado el ratón alrededor del centro desde el clic.
            double currentMouseAngle = Math.Atan2(mouse.Y - _groupRotationCenter.Y, mouse.X - _groupRotationCenter.X) * (180 / Math.PI);
            double angleDelta = currentMouseAngle - _initialMouseAngle;

            double rad = angleDelta * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            // Se aplica a cada objeto, siempre a partir de cómo estaba al empezar:
            foreach (var state in _groupRotationStates)
            {
                // 1. Gira sobre su propio centro.
                state.Widget.RotationAngle = state.InitialRotation + angleDelta;

                // 2. Su centro da la vuelta alrededor del centro del grupo.
                double objCenterX = state.InitialX + (state.Widget.Width / 2.0);
                double objCenterY = state.InitialY + (state.Widget.Height / 2.0);

                double dx = objCenterX - _groupRotationCenter.X;
                double dy = objCenterY - _groupRotationCenter.Y;

                double rotatedCenterX = _groupRotationCenter.X + (dx * cos - dy * sin);
                double rotatedCenterY = _groupRotationCenter.Y + (dx * sin + dy * cos);

                // 3. Y se recoloca la esquina a partir del centro nuevo.
                state.Widget.X = rotatedCenterX - (state.Widget.Width / 2.0);
                state.Widget.Y = rotatedCenterY - (state.Widget.Height / 2.0);
            }
            e.Handled = true;
        }
    }

    /// <summary>Al soltar un tirador de giro: se acaba el giro y se libera el ratón.</summary>
    private void OnRotatePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isRotating)
        {
            _isRotating = false;
            _groupRotationStates.Clear();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    // ==========================================
    // FONDO DEL LIENZO: RECUADRO DE SELECCIÓN Y DESPLAZAMIENTO
    // ==========================================

    /// <summary>
    /// Clic en el fondo del lienzo. Con el botón central, empieza a desplazar el lienzo. Con
    /// cualquier otro, empieza el recuadro de selección; sin Ctrl, antes se deselecciona todo.
    /// </summary>
    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var vm = DataContext as ViewModels.MainWindowViewModel;
        if (vm == null) return;

        // Igual que en OnWidgetPointerPressed: tocar el lienzo es reclamar el teclado.
        this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer")?.Focus();

        var point = e.GetCurrentPoint(this);

        // Botón central: desplazar el lienzo.
        if (point.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            // La posición se mide sobre la ventana y no sobre el lienzo: el lienzo se mueve con el
            // propio desplazamiento, y medir sobre él haría temblar el movimiento.
            _lastPanPoint = e.GetPosition(this);
            e.Pointer.Capture(sender as Avalonia.Controls.Control);
            e.Handled = true;
            return; // sin recuadro de selección
        }

        // 1. Sin Ctrl, se empieza deseleccionando todo.
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            foreach (var widget in vm.CurrentCanvasObjects) widget.IsSelected = false;
            vm.UpdateSelection();
        }

        // 2. Empieza el recuadro, de tamaño cero, donde está el ratón.
        _isSelecting = true;
        _selectionStartPoint = e.GetPosition(this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer"));

        if (_selectionBox == null)
            _selectionBox = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("SelectionBox");

        if (_selectionBox != null)
        {
            _selectionBox.IsVisible = true;
            Avalonia.Controls.Canvas.SetLeft(_selectionBox, _selectionStartPoint.X);
            Avalonia.Controls.Canvas.SetTop(_selectionBox, _selectionStartPoint.Y);
            _selectionBox.Width = 0;
            _selectionBox.Height = 0;
        }
    }

    /// <summary>
    /// Al mover sobre el fondo: desplaza el lienzo, o estira el recuadro de selección y
    /// selecciona en vivo lo que queda dentro.
    /// </summary>
    private void OnBackgroundPointerMoved(object? sender, PointerEventArgs e)
    {
        var vm = DataContext as ViewModels.MainWindowViewModel;
        if (vm == null) return;

        // Desplazamiento del lienzo.
        if (_isPanning)
        {
            var panPoint = e.GetPosition(this);
            double deltaX = panPoint.X - _lastPanPoint.X;
            double deltaY = panPoint.Y - _lastPanPoint.Y;

            // Sin dividir por el zoom, que lo dejaba demasiado lento.
            vm.CanvasOffsetX += deltaX;
            vm.CanvasOffsetY += deltaY;

            _lastPanPoint = panPoint;
            e.Handled = true;
            return;
        }

        // Si no se está dibujando el recuadro, no hay nada que hacer.
        if (!_isSelecting || _selectionBox == null) return;


        var currentPoint = e.GetPosition(this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer"));

        // El recuadro va de la esquina de partida al ratón, en cualquier dirección.
        var x = Math.Min(currentPoint.X, _selectionStartPoint.X);
        var y = Math.Min(currentPoint.Y, _selectionStartPoint.Y);
        var width = Math.Abs(currentPoint.X - _selectionStartPoint.X);
        var height = Math.Abs(currentPoint.Y - _selectionStartPoint.Y);

        Avalonia.Controls.Canvas.SetLeft(_selectionBox, x);
        Avalonia.Controls.Canvas.SetTop(_selectionBox, y);
        _selectionBox.Width = width;
        _selectionBox.Height = height;

        // Selección en vivo: queda seleccionado lo que cabe ENTERO en el recuadro. Se mira la parte
        // VISIBLE de cada objeto, la que queda tras el recorte: es lo que se ve y lo que se puede
        // pinchar. (Antes se miraba la caja entera, y un objeto muy recortado no entraba aunque
        // todo lo que se veía de él estuviera dentro.) El giro no se tiene en cuenta, y los grupos
        // vinculados no se seleccionan enteros, a diferencia del clic.
        //
        // Un objeto recortado del todo no tiene parte visible: se queda en una línea (o un punto)
        // en el borde de su recorte, y si queda dentro, se coge. Es la única forma de recuperarlo
        // con el ratón, porque no le queda nada que pinchar.
        var selectionRect = new Rect(x, y, width, height);

        foreach (var widget in vm.CurrentCanvasObjects)
        {
            var widgetRect = new Rect(widget.X + widget.CropLeft, widget.Y + widget.CropTop,
                                      widget.CroppedWidth, widget.CroppedHeight);

            if (selectionRect.Contains(widgetRect))
            {
                widget.IsSelected = true;
            }
            else if ((e.KeyModifiers & KeyModifiers.Control) == 0)
            {
                // Lo que se sale, fuera, salvo con Ctrl, que conserva lo que ya estaba.
                widget.IsSelected = false;
            }
        }
    }

    /// <summary>
    /// Al soltar: se acaba el desplazamiento o el recuadro. Con el recuadro, el panel de
    /// propiedades se pone al día con la selección.
    /// </summary>
    private void OnBackgroundPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Fin del desplazamiento.
        if (_isPanning && e.InitialPressMouseButton == Avalonia.Input.MouseButton.Middle)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_isSelecting)
        {
            _isSelecting = false;
            if (_selectionBox != null) _selectionBox.IsVisible = false;

            // El panel de propiedades no sigue a la selección en vivo: se actualiza al soltar.
            var vm = DataContext as ViewModels.MainWindowViewModel;
            if (vm != null) vm.UpdateSelection();
        }
    }

    // ==========================================
    // TAMAÑO CON LOS TIRADORES AZULES
    // ==========================================

    /// <summary>
    /// Al empezar a arrastrar un tirador de esquina, el historial hace su foto: el arrastre entero
    /// es un solo paso de Ctrl+Z. (Antes no se hacía, y como era también lo que marcaba el tema
    /// como modificado, cambiar el tamaño con los tiradores después de guardar se perdía al cerrar
    /// sin ningún aviso.)
    /// </summary>
    private void OnResizeDragStarted(object? sender, VectorEventArgs e)
    {
        (DataContext as ViewModels.MainWindowViewModel)?.SaveState();
    }

    /// <summary>
    /// Arrastre de un tirador de esquina: escala todo lo seleccionado como un bloque, desde la
    /// esquina contraria, que se queda quieta. Cada objeto cambia de tamaño y de posición en la
    /// misma proporción que la caja que los envuelve. El bloque nunca baja de 20 px. La foto del
    /// historial ya la hizo OnResizeDragStarted, al empezar.
    /// </summary>
    private void OnResizeDragDelta(object? sender, VectorEventArgs e)
    {
        if (sender is Thumb thumb && thumb.DataContext is ViewModels.WidgetItemViewModel dragWidget)
        {
            var vm = DataContext as ViewModels.MainWindowViewModel;
            if (vm == null) return;

            var selected = vm.CurrentCanvasObjects.Where(w => w.IsSelected).ToList();
            if (selected.Count == 0) return;

            // 1. La caja que envuelve a todos, tal como está ahora.
            double minX = selected.Min(w => w.X);
            double minY = selected.Min(w => w.Y);
            double maxX = selected.Max(w => w.X + w.Width);
            double maxY = selected.Max(w => w.Y + w.Height);

            double oldGroupWidth = maxX - minX;
            double oldGroupHeight = maxY - minY;

            // Una caja sin ancho o sin alto no se puede escalar.
            if (oldGroupWidth <= 0 || oldGroupHeight <= 0) return;

            // 2. El movimiento del ratón agranda o encoge la caja por el lado del tirador.
            double deltaX = e.Vector.X;
            double deltaY = e.Vector.Y;
            double newGroupWidth = oldGroupWidth;
            double newGroupHeight = oldGroupHeight;

            // El punto fijo es la esquina contraria a la del tirador.
            double pivotX = minX;
            double pivotY = minY;

            // Los cuatro tiradores de las esquinas (su Name, en MainWindow.axaml). Los de los lados
            // se quitaron del .axaml hace tiempo, y sus casos de aquí también.
            switch (thumb.Name)
            {
                case "ResizeBottomRight":
                    newGroupWidth += deltaX; newGroupHeight += deltaY; pivotX = minX; pivotY = minY; break;
                case "ResizeBottomLeft":
                    newGroupWidth -= deltaX; newGroupHeight += deltaY; pivotX = maxX; pivotY = minY; break;
                case "ResizeTopRight":
                    newGroupWidth += deltaX; newGroupHeight -= deltaY; pivotX = minX; pivotY = maxY; break;
                case "ResizeTopLeft":
                    newGroupWidth -= deltaX; newGroupHeight -= deltaY; pivotX = maxX; pivotY = maxY; break;
            }

            // El bloque nunca baja de 20 px de ancho ni de alto.
            if (newGroupWidth < 20) newGroupWidth = 20;
            if (newGroupHeight < 20) newGroupHeight = 20;

            // 3. Cuánto ha crecido o encogido, en cada eje.
            double scaleX = newGroupWidth / oldGroupWidth;
            double scaleY = newGroupHeight / oldGroupHeight;

            // 4. Cada objeto crece igual, y se aleja o se acerca al punto fijo en la misma
            //    proporción.
            foreach (var w in selected)
            {
                w.Width *= scaleX;
                w.Height *= scaleY;

                w.X = pivotX + (w.X - pivotX) * scaleX;
                w.Y = pivotY + (w.Y - pivotY) * scaleY;
            }
        }
    }

    // ==========================================
    // TECLADO
    // ==========================================

    /// <summary>
    /// Si las teclas son del lienzo: si el foco está en el lienzo o dentro de él, o no lo tiene
    /// nadie.
    ///
    /// La pregunta se hace en POSITIVO ("¿está el foco DENTRO del lienzo?") y no al revés, a
    /// propósito. Preguntando "¿está FUERA del panel de propiedades?", todo lo desconocido
    /// pasaría a ser del lienzo: la caja de herramientas, los TextBox del nombre y el autor del
    /// tema, el editor de código y, sobre todo, cualquier cosa dentro de un popup. Los
    /// desplegables, los menús contextuales y el flyout del ColorView se dibujan en su propio
    /// PopupRoot, que NO cuelga del árbol visual de la ventana (es lo mismo que obliga al
    /// selector :is(TopLevel) de App.axaml), así que contarían como "fuera del panel" y el lienzo
    /// les robaría las flechas. En positivo, lo desconocido se queda fuera, que es el lado
    /// seguro, y no se estropea el día que aparezca un cuarto panel.
    /// </summary>
    private bool ElFocoEstaEnElLienzo()
    {
        var lienzo = this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer");
        if (lienzo == null) return false;

        var enfocado = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

        // Nadie ha reclamado el teclado todavía (nada enfocado, o la propia ventana): las teclas
        // son del lienzo. Sin esta red, los atajos no funcionarían hasta el primer clic en el
        // lienzo; o sea, al abrir la aplicación Ctrl+Z no haría nada.
        if (enfocado == null || ReferenceEquals(enfocado, this)) return true;

        return enfocado is Visual v
               && (ReferenceEquals(v, lienzo) || v.GetVisualAncestors().Contains(lienzo));
    }
    /// <summary>
    /// Los atajos de teclado de la ventana. Ctrl+S funciona siempre. El resto, solo si las teclas
    /// son del lienzo (ElFocoEstaEnElLienzo) y el editor de código está cerrado: Ctrl+C, Ctrl+V y
    /// Ctrl+D copian, pegan y duplican; Ctrl+Alt+V pega las propiedades; Ctrl+L vincula y
    /// Ctrl+Alt+L desvincula; Ctrl+Z y Ctrl+Y deshacen y rehacen; Ctrl+A selecciona todo;
    /// Ctrl+Flecha arriba o abajo sube o baja de capa. Las flechas mueven lo seleccionado 1 px
    /// (10 con Mayús) o, si no hay nada seleccionado, desplazan el lienzo. Supr y Retroceso
    /// borran.
    ///
    /// Mover con las flechas deja UN paso en el historial por cada serie de pulsaciones, no uno
    /// por pulsación: ver _fotoDeFlechasHecha.
    /// </summary>
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;

        bool ctrl  = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool alt   = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        // Ctrl+S es GLOBAL y va antes de la pregunta del foco: guardar mientras se escribe el
        // nombre del tema es justo cuando más falta hace, y ahí el foco está en una caja de texto.
        if (ctrl && !alt && e.Key == Key.S)
        {
            vm.SaveThemeCommand.Execute(this);
            e.Handled = true;
            return;
        }

        // De aquí abajo TODO es del lienzo. Si el foco está en el panel de propiedades, en la
        // barra superior, en un desplegable o en el editor de código, la tecla no es nuestra y se
        // deja pasar para que el control haga lo suyo.
        if (!ElFocoEstaEnElLienzo()) return;

        // El editor de código ocupa el sitio del lienzo: con él abierto no hay nada que mover ni
        // que borrar, y los objetos fantasma que se ven detrás no son el objetivo de la tecla.
        if (vm.IsCodeEditorVisible) return;

        // --- ATAJOS CON CTRL ---
        // Antes vivían en <Window.KeyBindings>, que al ser de la VENTANA se disparaban sin mirar
        // el foco y no pasaban nunca por aquí.
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.C: vm.CopyObjectCommand.Execute(null); break;
                // Ctrl+Alt+V es "pegar propiedades"; Ctrl+V pelado pega el objeto.
                case Key.V:
                    if (alt) vm.PastePropertiesCommand.Execute(null);
                    else     vm.PasteObjectCommand.Execute(null);
                    break;
                case Key.D: vm.DuplicateObjectCommand.Execute(null); break;
                case Key.L:
                    if (alt) vm.UnlinkSelectedObjectsCommand.Execute(null);
                    else     vm.LinkSelectedObjectsCommand.Execute(null);
                    break;
                case Key.Z: vm.UndoCommand.Execute(null); break;
                case Key.Y: vm.RedoCommand.Execute(null); break;
                case Key.A: vm.SelectAllObjectsCommand.Execute(null); break;
                case Key.Up:   vm.MoveLayerUpCommand.Execute(null); break;
                case Key.Down: vm.MoveLayerDownCommand.Execute(null); break;
                default: return;   // cualquier otro Ctrl+algo no es nuestro
            }
            e.Handled = true;
            return;
        }

        // --- FLECHAS SIN CTRL ---
        // El "sin Ctrl" no es cosmético: antes esta rama no miraba el modificador, así que
        // Ctrl+Flecha hacía DOS cosas (subía la capa por el KeyBinding y además movía el objeto
        // 1 px). No se notaba porque 1 px no se ve. El return del bloque de arriba es lo que lo
        // cierra.
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            double moveSpeed = shift ? 10.0 : 1.0; // Mayús = 10 px en vez de 1
            const double panSpeed = 20.0;

            // Con algo seleccionado, se mueve; sin nada, se desplaza el lienzo.
            if (vm.CurrentCanvasObjects.Any(w => w.IsSelected))
            {
                // La foto del historial, solo en la primera pulsación de la serie. Desplazar el
                // lienzo no la necesita: no cambia el tema.
                if (!_fotoDeFlechasHecha)
                {
                    vm.SaveState();
                    _fotoDeFlechasHecha = true;
                }

                foreach (var w in vm.CurrentCanvasObjects.Where(x => x.IsSelected))
                {
                    if (e.Key == Key.Up)    w.Y -= moveSpeed;
                    if (e.Key == Key.Down)  w.Y += moveSpeed;
                    if (e.Key == Key.Left)  w.X -= moveSpeed;
                    if (e.Key == Key.Right) w.X += moveSpeed;
                }
            }
            else
            {
                if (e.Key == Key.Up)    vm.CanvasOffsetY += panSpeed;
                if (e.Key == Key.Down)  vm.CanvasOffsetY -= panSpeed;
                if (e.Key == Key.Left)  vm.CanvasOffsetX += panSpeed;
                if (e.Key == Key.Right) vm.CanvasOffsetX -= panSpeed;
            }
            e.Handled = true;
            return;
        }

        // --- BORRAR: Supr o Retroceso ---
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            vm.DeleteSelectedObjectCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Al soltar una flecha se acaba la serie de pulsaciones: la siguiente vuelve a dejar un paso
    /// en el historial.
    /// </summary>
    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right) _fotoDeFlechasHecha = false;
    }

    // ==========================================
    // RECORTE CON LOS TIRADORES ROSAS
    // ==========================================

    /// <summary>
    /// Clic en un tirador de recorte: guarda un paso en el historial y apunta qué lado se recorta
    /// (el Name del tirador), dónde está el ratón y cuánto recorte había. El ratón queda capturado
    /// hasta soltarlo.
    /// </summary>
    private void OnCropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Border border && border.DataContext is ViewModels.WidgetItemViewModel widget)
        {
            var vm = DataContext as ViewModels.MainWindowViewModel;
            if (vm != null) vm.SaveState();

            _isCropping = true;
            _croppingWidget = widget;
            _cropDirection = border.Name ?? "";

            // El punto de partida, en coordenadas del lienzo: el recorte se calcula siempre desde
            // aquí.
            _cropStartPoint = e.GetPosition(this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer"));

            switch (_cropDirection)
            {
                case "CropTop": _initialCropValue = widget.CropTop; break;
                case "CropBottom": _initialCropValue = widget.CropBottom; break;
                case "CropLeft": _initialCropValue = widget.CropLeft; break;
                case "CropRight": _initialCropValue = widget.CropRight; break;
            }

            e.Pointer.Capture(border);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Al mover: el recorte es el de partida más lo que se ha movido el ratón hacia dentro.
    /// </summary>
    private void OnCropPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isCropping && _croppingWidget != null)
        {
            var current = e.GetPosition(this.FindControl<Avalonia.Controls.Panel>("WorkspaceContainer"));
            double deltaX = current.X - _cropStartPoint.X;
            double deltaY = current.Y - _cropStartPoint.Y;

            // Entre 0 y lo que deja al menos 1 px sin recortar entre este lado y el contrario.
            switch (_cropDirection)
            {
                case "CropTop":
                    _croppingWidget.CropTop = Math.Clamp(_initialCropValue + deltaY, 0, _croppingWidget.Height - _croppingWidget.CropBottom - 1);
                    break;
                case "CropBottom":
                    _croppingWidget.CropBottom = Math.Clamp(_initialCropValue - deltaY, 0, _croppingWidget.Height - _croppingWidget.CropTop - 1);
                    break;
                case "CropLeft":
                    _croppingWidget.CropLeft = Math.Clamp(_initialCropValue + deltaX, 0, _croppingWidget.Width - _croppingWidget.CropRight - 1);
                    break;
                case "CropRight":
                    _croppingWidget.CropRight = Math.Clamp(_initialCropValue - deltaX, 0, _croppingWidget.Width - _croppingWidget.CropLeft - 1);
                    break;
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Ctrl+rueda sobre el lienzo: zoom, de 10 en 10 %, entre el 20 % y el 300 %. Los botones de
    /// zoom de la barra (ZoomIn y ZoomOut del ViewModel) tienen los mismos límites.
    /// </summary>
    private void OnCanvasPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && DataContext is ViewModels.MainWindowViewModel vm)
        {
            // Limita el zoom entre 20% y 300%
            vm.CanvasZoom = Math.Clamp(vm.CanvasZoom + (e.Delta.Y > 0 ? 0.1 : -0.1), 0.2, 3.0);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Al soltar un tirador de recorte: se acaba el recorte y se libera el ratón.
    /// </summary>
    private void OnCropPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isCropping)
        {
            _isCropping = false;
            _croppingWidget = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    // ==========================================
    // AL CERRAR
    // ==========================================

    /// <summary>
    /// Al cerrarse la ventana: apaga el servidor de la vista previa, porque el HttpListener tiene
    /// el puerto cogido mientras viva el proceso, y borra las carpetas temporales de esta sesión
    /// (la de la vista previa, con el tema entero dentro, y las de los temas abiertos).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainWindowViewModel)?.StopBrowserPreview();
        (DataContext as MainWindowViewModel)?.LimpiarCarpetasDeCarga();
        base.OnClosed(e);
    }

    // ==========================================
    // AVISO DE CAMBIOS SIN GUARDAR
    // ==========================================
    // Lo que se contesta en el aviso: guardar y seguir, seguir sin guardar, o quedarse como
    // estaba. "Seguir" es cerrar la ventana o abrir otro tema, según quién pregunte.
    private enum RespuestaAviso { Guardar, SeguirSinGuardar, Cancelar }

    // Cuando el usuario ya ha decidido, hay que dejar pasar el cierre sin volver a preguntar.
    private bool _cierreConfirmado;

    /// <summary>
    /// Al intentar cerrar con cambios sin guardar, pregunta antes (PuedeDescartarLosCambiosAsync).
    /// Guardar guarda, y si al final no llega a guardarse, no cierra; Salir sin guardar cierra, y
    /// Cancelar no cierra.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_cierreConfirmado || DataContext is not ViewModels.MainWindowViewModel vm || !vm.IsDirty)
        {
            base.OnClosing(e);
            return;
        }

        // Paramos el cierre mientras preguntamos. No se puede esperar dentro de OnClosing:
        // hay que cancelar, preguntar, y volver a cerrar a mano si procede.
        e.Cancel = true;
        base.OnClosing(e);

        if (!await PuedeDescartarLosCambiosAsync(EditorI18n.Tr("UI_UnsavedMessage"), EditorI18n.Tr("UI_BtnExitWithoutSaving")))
            return;

        _cierreConfirmado = true;
        Close();
    }

    /// <summary>
    /// La pregunta de los cambios sin guardar, antes de algo que los perdería: cerrar la ventana
    /// (OnClosing) o abrir otro tema (MainWindowViewModel.LoadTheme). Si no hay cambios, no
    /// pregunta nada.
    /// </summary>
    /// <param name="mensaje">El texto del aviso, que dice qué se va a hacer.</param>
    /// <param name="textoSeguir">El botón de seguir sin guardar ("Salir sin guardar", "Abrir sin
    /// guardar").</param>
    /// <returns>true si se puede seguir: no había cambios, se han guardado, o se eligió seguir sin
    /// guardar. false si se canceló, o si se eligió guardar y al final no se guardó.</returns>
    public async Task<bool> PuedeDescartarLosCambiosAsync(string mensaje, string textoSeguir)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm || !vm.IsDirty) return true;

        var respuesta = await PreguntarGuardarAsync(mensaje, textoSeguir);

        if (respuesta == RespuestaAviso.Cancelar) return false;

        if (respuesta == RespuestaAviso.Guardar)
        {
            await vm.SaveThemeCommand.ExecuteAsync(this);

            // Si sigue sucio es que no llegó a guardar (canceló el diálogo de "Guardar como").
            // Seguir aquí sería exactamente lo que este aviso existe para evitar.
            return !vm.IsDirty;
        }

        return true; // seguir sin guardar
    }

    /// <summary>
    /// Avalonia no trae MessageBox, así que el diálogo se monta a mano. Son tres botones:
    /// no compensa un .axaml nuevo para esto.
    /// </summary>
    /// <param name="mensaje">El texto del aviso.</param>
    /// <param name="textoSeguir">El texto del botón de seguir sin guardar.</param>
    private async Task<RespuestaAviso> PreguntarGuardarAsync(string mensaje, string textoSeguir)
    {
        // Cancelar es el valor por defecto a propósito: si el usuario cierra este diálogo con
        // la X, lo que quiere es NO perder nada.
        var resultado = RespuestaAviso.Cancelar;

        var dialogo = new Window
        {
            Title = EditorI18n.Tr("UI_UnsavedTitle"),
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brush.Parse("#1E1E2E")
        };

        // Un botón del diálogo: al pulsarlo apunta su respuesta y cierra el diálogo.
        Button Boton(string texto, RespuestaAviso valor, string fondo, string texto_color)
        {
            var b = new Button
            {
                Content = texto,
                Padding = new Thickness(14, 8),
                Background = Avalonia.Media.Brush.Parse(fondo),
                Foreground = Avalonia.Media.Brush.Parse(texto_color),
                CornerRadius = new CornerRadius(4)
            };
            b.Click += (_, _) => { resultado = valor; dialogo.Close(); };
            return b;
        }

        var botones = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        botones.Children.Add(Boton(EditorI18n.Tr("UI_BtnCancel"), RespuestaAviso.Cancelar, "#313244", "#CDD6F4"));
        botones.Children.Add(Boton(textoSeguir, RespuestaAviso.SeguirSinGuardar, "#313244", "#F38BA8"));
        botones.Children.Add(Boton(EditorI18n.Tr("UI_BtnSave"), RespuestaAviso.Guardar, "#89B4FA", "#11111B"));

        var contenido = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        contenido.Children.Add(new TextBlock
        {
            Text = mensaje,
            Foreground = Avalonia.Media.Brush.Parse("#CDD6F4"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        contenido.Children.Add(botones);

        dialogo.Content = contenido;

        await dialogo.ShowDialog(this);
        return resultado;
    }
}
