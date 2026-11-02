using Avalonia.Controls;

namespace SheepGDRequest.Views;

/// <summary>
/// Panel de Ajustes. MainWindow lo muestra con SettingsViewModel como DataContext; la lógica está
/// en ese ViewModel y aquí solo se carga el XAML.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
