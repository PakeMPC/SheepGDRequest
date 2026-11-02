using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SheepGDRequest.Views;

/// <summary>
/// Selector de idioma del primer arranque. Lo abre MainWindow con ShowDialog cuando
/// AppLanguage está vacío y devuelve el código elegido ("es", "en" o "pt"). Si se cierra con la
/// X, devuelve null y MainWindow usa el español.
/// </summary>
public partial class LanguageWindow : Window
{
    public LanguageWindow()
    {
        InitializeComponent();
    }

    // Cada botón cierra la ventana y devuelve el código de su idioma.
    private void BtnEs_Click(object? sender, RoutedEventArgs e) => Close("es");
    private void BtnEn_Click(object? sender, RoutedEventArgs e) => Close("en");
    private void BtnPt_Click(object? sender, RoutedEventArgs e) => Close("pt");
}
