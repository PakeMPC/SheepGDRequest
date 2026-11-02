using Avalonia.Controls;

namespace SheepGDEditor.Views;

/// <summary>
/// El código de la caja de herramientas (ToolboxView.axaml). Solo carga el XAML: los botones
/// funcionan con enlaces al comando de MainWindowViewModel.
/// </summary>
public partial class ToolboxView : UserControl
{
    /// <summary>Carga el panel desde ToolboxView.axaml.</summary>
    public ToolboxView()
    {
        InitializeComponent();
    }
}
