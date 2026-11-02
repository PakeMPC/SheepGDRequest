using Avalonia.Controls;

namespace SheepGDEditor.Views;

/// <summary>
/// El código del panel lateral del editor de código (CodeEditorSideView.axaml). Solo carga el
/// XAML: los botones funcionan con enlaces al comando de MainWindowViewModel, sin código aquí.
/// </summary>
public partial class CodeEditorSideView : UserControl
{
    public CodeEditorSideView()
    {
        InitializeComponent();
    }
}
