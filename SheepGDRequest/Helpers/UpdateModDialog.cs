using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SheepGDRequest.Services;

namespace SheepGDRequest.Helpers;

/// <summary>
/// Respuesta del aviso de actualización. MasTarde vale 0 a propósito: si el usuario cierra la
/// ventana con la X, ShowDialog devuelve default(T), y eso debe significar "no actualizar".
/// </summary>
public enum RespuestaActualizacion { MasTarde = 0, Actualizar }

/// <summary>
/// Aviso de que hay una versión nueva del mod de Geode, con los botones "Más tarde" y
/// "Actualizar". Construido en código, con el mismo aspecto que ConfirmFolderDialog. Lo muestra
/// SettingsViewModel.ComprobarActualizacionDelModAsync al arrancar.
/// </summary>
public static class UpdateModDialog
{
    /// <param name="duenio">Ventana sobre la que se centra el aviso.</param>
    /// <param name="nueva">Versión publicada, tal como se muestra (por ejemplo, "v1.1.0").</param>
    /// <param name="instalada">Versión instalada, tal como se muestra.</param>
    public static async Task<RespuestaActualizacion> MostrarAsync(Window duenio, string nueva, string instalada)
    {
        var ventana = new Window
        {
            Title = I18n.Tr("UI_ModUpdateTitle"),
            Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1e1e2e"))
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14 };

        panel.Children.Add(new TextBlock
        {
            Text = string.Format(I18n.Tr("UI_ModUpdateMsg"), nueva, instalada),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });

        // Reemplazar el archivo con el juego abierto puede fallar en Windows, y aunque no falle,
        // la versión nueva no carga hasta reiniciar el juego.
        panel.Children.Add(new TextBlock
        {
            Text = I18n.Tr("UI_ModUpdateHint"),
            Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")),
            TextWrapping = TextWrapping.Wrap
        });

        var botones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        // Crea un botón que cierra el aviso con la respuesta indicada; el destacado es el verde.
        Button Crear(string llave, RespuestaActualizacion respuesta, bool destacado)
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

        botones.Children.Add(Crear("UI_BtnLater", RespuestaActualizacion.MasTarde, false));
        botones.Children.Add(Crear("UI_BtnUpdate", RespuestaActualizacion.Actualizar, true));

        panel.Children.Add(botones);
        ventana.Content = panel;

        return await ventana.ShowDialog<RespuestaActualizacion>(duenio);
    }
}
