using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SheepGDRequest.Services;

namespace SheepGDRequest.Helpers;

/// <summary>
/// Respuesta del diálogo. Cancelar vale 0 a propósito: si el usuario cierra la ventana con la
/// X, ShowDialog devuelve default(T), y eso debe significar "no hacer nada".
/// </summary>
public enum RespuestaCarpeta { Cancelar = 0, Aceptar, ElegirOtra }

/// <summary>
/// Ventana de confirmación construida en código, igual que la del inicio de sesión: Avalonia no
/// incluye cuadros de diálogo de mensaje.
///
/// Existe porque en Linux el selector de carpetas lo muestra el portal XDG del escritorio, que
/// ignora SuggestedStartLocation: aunque el programa sepa dónde está Geometry Dash, el diálogo
/// se abre en otra carpeta. Confirmar una ruta conocida es más rápido y más fiable que obligar
/// al usuario a navegar hasta ella. Lo usa SettingsViewModel.InstallGeodeMod.
/// </summary>
public static class ConfirmFolderDialog
{
    /// <summary>
    /// Muestra la ruta encontrada y espera a que el usuario acepte, elija otra carpeta o cancele.
    /// </summary>
    /// <param name="duenio">Ventana sobre la que se centra el diálogo.</param>
    /// <param name="ruta">Carpeta de Geometry Dash que se propone.</param>
    public static async Task<RespuestaCarpeta> MostrarAsync(Window duenio, string ruta)
    {
        var ventana = new Window
        {
            Title = I18n.Tr("UI_ConfirmGdTitle"),
            Width = 560, SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1e1e2e"))
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14 };

        panel.Children.Add(new TextBlock
        {
            Text = I18n.Tr("UI_ConfirmGdMsg"),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#313244")),
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(10, 8),
            Child = new TextBlock
            {
                Text = ruta,
                Foreground = new SolidColorBrush(Color.Parse("#A6E3A1")),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("monospace")
            }
        });

        var botones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        // Crea un botón que cierra el diálogo con la respuesta indicada; el destacado es el verde.
        Button Crear(string llave, RespuestaCarpeta respuesta, bool destacado)
        {
            var b = new Button
            {
                Content = I18n.Tr(llave),
                Padding = new Avalonia.Thickness(16, 8),
                CornerRadius = new Avalonia.CornerRadius(5),
                Background = new SolidColorBrush(Color.Parse(destacado ? "#A6E3A1" : "#313244")),
                Foreground = destacado ? Brushes.Black : Brushes.White,
                FontWeight = destacado ? FontWeight.Bold : FontWeight.Normal
            };
            b.Click += (_, _) => ventana.Close(respuesta);
            return b;
        }

        botones.Children.Add(Crear("UI_BtnCancelDialog", RespuestaCarpeta.Cancelar, false));
        botones.Children.Add(Crear("UI_BtnChooseAnother", RespuestaCarpeta.ElegirOtra, false));
        botones.Children.Add(Crear("UI_BtnYesInstallHere", RespuestaCarpeta.Aceptar, true));

        panel.Children.Add(botones);
        ventana.Content = panel;

        return await ventana.ShowDialog<RespuestaCarpeta>(duenio);
    }
}
