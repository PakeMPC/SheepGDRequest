using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SheepGDRequest.Services;

namespace SheepGDRequest.Helpers;

/// <summary>
/// Ventanas de la actualización del programa, construidas en código con el mismo aspecto que
/// UpdateModDialog: el aviso de que hay una versión nueva (con sus novedades) y la ventana de
/// progreso de la descarga. Las usa SettingsViewModel.ComprobarActualizacionDelProgramaAsync.
/// </summary>
public static class UpdateAppDialog
{
    private static readonly IBrush Fondo = new SolidColorBrush(Color.Parse("#1e1e2e"));
    private static readonly IBrush Secundario = new SolidColorBrush(Color.Parse("#A6ADC8"));

    /// <summary>
    /// Aviso de versión nueva, con los botones "Más tarde" y "Actualizar".
    /// </summary>
    /// <param name="duenio">Ventana sobre la que se centra el aviso.</param>
    /// <param name="nueva">Versión publicada, tal como se muestra (por ejemplo, "v1.2.0").</param>
    /// <param name="actual">Versión de esta copia, tal como se muestra.</param>
    /// <param name="notas">
    /// Novedades de la release (su descripción en GitHub); puede estar vacía.
    /// </param>
    /// <param name="automatica">Si esta copia se actualiza sola. Si no, el texto explica que se
    /// abrirá la página de descarga.</param>
    public static async Task<RespuestaActualizacion> PreguntarAsync(Window duenio, string nueva, string actual,
        string notas, bool automatica)
    {
        var ventana = new Window
        {
            Title = I18n.Tr("UI_AppUpdateTitle"),
            Width = 560, SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Fondo
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14 };

        panel.Children.Add(new TextBlock
        {
            Text = string.Format(I18n.Tr("UI_AppUpdateMsg"), nueva, actual),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        });

        // Las novedades son el texto de la release tal cual (Markdown sin interpretar). Se
        // limitan en alto para que una release larga no saque los botones de la pantalla.
        if (!string.IsNullOrWhiteSpace(notas))
        {
            panel.Children.Add(new TextBlock { Text = I18n.Tr("UI_AppUpdateNotes"), Foreground = Secundario });
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#181825")),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(12),
                Child = new ScrollViewer
                {
                    MaxHeight = 220,
                    Content = new SelectableTextBlock
                    {
                        Text = notas,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = I18n.Tr(automatica ? "UI_AppUpdateHintAuto" : "UI_AppUpdateHintManual"),
            Foreground = Secundario,
            TextWrapping = TextWrapping.Wrap
        });

        var botones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

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
        botones.Children.Add(Crear(automatica ? "UI_BtnUpdate" : "UI_BtnOpenDownloadPage",
            RespuestaActualizacion.Actualizar, true));

        panel.Children.Add(botones);
        ventana.Content = panel;

        return await ventana.ShowDialog<RespuestaActualizacion>(duenio);
    }

    /// <summary>
    /// Ventana de progreso de la descarga, sin botones: no se puede cerrar hasta que el programa
    /// la cierra con <see cref="Progreso.Cerrar"/>.
    /// </summary>
    public static Progreso MostrarProgreso(Window duenio)
    {
        var texto = new TextBlock
        {
            Text = string.Format(I18n.Tr("UI_AppUpdateDownloading"), 0),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        };
        var barra = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8 };

        var ventana = new Window
        {
            Title = I18n.Tr("UI_AppUpdateTitle"),
            Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Fondo,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 14,
                Children = { texto, barra }
            }
        };

        var progreso = new Progreso(ventana, texto, barra);
        ventana.Closing += (_, e) => { if (!progreso.PuedeCerrarse) e.Cancel = true; };
        ventana.Show(duenio);
        return progreso;
    }

    /// <summary>
    /// Control de la ventana de progreso. Implementa IProgress (0 a 1) y se puede usar desde
    /// cualquier hilo: los cambios se pasan al hilo de la interfaz.
    /// </summary>
    public sealed class Progreso : IProgress<double>
    {
        private readonly Window _ventana;
        private readonly TextBlock _texto;
        private readonly ProgressBar _barra;
        private int _ultimo = -1;

        internal bool PuedeCerrarse { get; private set; }

        internal Progreso(Window ventana, TextBlock texto, ProgressBar barra)
        {
            _ventana = ventana;
            _texto = texto;
            _barra = barra;
        }

        public void Report(double valor)
        {
            int porcentaje = (int)Math.Round(Math.Clamp(valor, 0, 1) * 100);
            if (porcentaje == _ultimo) return;
            _ultimo = porcentaje;

            Dispatcher.UIThread.Post(() =>
            {
                _barra.Value = porcentaje;
                _texto.Text = string.Format(I18n.Tr("UI_AppUpdateDownloading"), porcentaje);
            });
        }

        /// <summary>Cambia el texto a "preparando" (al terminar la descarga).</summary>
        public void Preparando() => Dispatcher.UIThread.Post(() =>
        {
            _barra.IsIndeterminate = true;
            _texto.Text = I18n.Tr("UI_AppUpdatePreparing");
        });

        /// <summary>
        /// Cierra la ventana. Desde el hilo de la interfaz se cierra en el acto, para que no quede
        /// abierta (y bloqueando el cierre) si justo después se cierra la ventana principal.
        /// </summary>
        public void Cerrar()
        {
            void Hacer()
            {
                PuedeCerrarse = true;
                _ventana.Close();
            }

            if (Dispatcher.UIThread.CheckAccess()) Hacer();
            else Dispatcher.UIThread.Post(Hacer);
        }
    }
}
