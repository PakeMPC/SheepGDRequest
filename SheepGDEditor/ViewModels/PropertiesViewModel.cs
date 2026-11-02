using CommunityToolkit.Mvvm.ComponentModel;
using SheepGDEditor.Services;

namespace SheepGDEditor.ViewModels;

/// <summary>
/// Lo que enseña el panel de propiedades (PropertiesView): el objeto seleccionado en el lienzo, el
/// título de la cabecera y qué secciones se ven.
///
/// No tiene lógica propia. Lo rellena MainWindowViewModel.UpdateSelection cada vez que cambia la
/// selección, y los controles del panel editan directamente el objeto seleccionado
/// (SelectedWidget.FontSize, SelectedWidget.TextPadding...). Hay una sola instancia, la de
/// MainWindowViewModel.PropertiesVM.
/// </summary>
public partial class PropertiesViewModel : ObservableObject
{
    /// <summary>
    /// El objeto que edita el panel, o null si no hay nada seleccionado. Con varios objetos
    /// seleccionados es el primero, y lo que se cambie en él se copia a los demás
    /// (MainWindowViewModel.OnTrackedWidgetPropertyChanged).
    /// </summary>
    [ObservableProperty]
    private WidgetItemViewModel? _selectedWidget;

    /// <summary>
    /// El título de la cabecera del panel, en el idioma del Editor: el nombre del objeto,
    /// "Selección Múltiple (N)" o "Sin Selección", que es con lo que empieza.
    ///
    /// Lo escribe MainWindowViewModel: UpdateSelection al cambiar la selección, y
    /// OnTrackedWidgetPropertyChanged cuando cambia el nombre del objeto seleccionado (al escribir
    /// el contenido de un texto, o al elegir el archivo de un vídeo, que le pone su nombre).
    /// </summary>
    [ObservableProperty]
    private string _selectionTitle = EditorI18n.Tr("UI_NoSelection");

    /// <summary>
    /// Si se ve la sección de texto del panel (contenido, fuente, alineación, margen...). Solo
    /// cuando TODOS los objetos seleccionados son textos.
    /// </summary>
    [ObservableProperty] private bool _showTextProperties;

    /// <summary>
    /// Si se ve la sección de imágenes y vídeos del panel. Solo cuando TODOS los objetos
    /// seleccionados son imágenes o vídeos: con una selección que mezcla textos e imágenes no se ve
    /// ninguna de las dos secciones.
    /// </summary>
    [ObservableProperty] private bool _showMediaProperties;
}
