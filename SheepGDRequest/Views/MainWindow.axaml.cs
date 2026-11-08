using Avalonia;
using Avalonia.Controls;
using System;
using System.Linq;
using System.Threading.Tasks;
using SheepGDRequest.Services;
using SheepGDRequest.ViewModels;

namespace SheepGDRequest.Views;

/// <summary>
/// Ventana principal. Al abrirse aplica lo que depende de settings.json y al cerrarse guarda su
/// posición y su tamaño.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = PrepararVentanaAsync();
    }

    /// <summary>
    /// Aplica lo que depende de settings.json (el idioma del primer arranque y la posición y el
    /// tamaño de la ventana) después de esperar a que termine de cargarse.
    ///
    /// Se espera a SettingsViewModel.Cargado porque la carga es asíncrona: si se leyeran los
    /// ajustes directamente en OnOpened, a veces llegarían todavía los valores por defecto y la
    /// ventana no recuperaría su posición.
    /// </summary>
    private async Task PrepararVentanaAsync()
    {
        var vm = MainWindowViewModel.Instance.SettingsVM;

        try
        {
            await vm.Cargado;
        }
        catch (Exception ex)
        {
            // Si la carga falla, se sigue con la configuración en blanco: sale el selector de
            // idioma y la ventana no recupera su posición. (Si lo que falla es leer
            // settings.json, SettingsService ya lo ha apartado con otro nombre, así que el
            // siguiente guardado no lo sobrescribe.)
            Console.WriteLine(string.Format(I18n.Tr("Log_SettingsLoadError"), ex.Message));
        }

        var settings = vm.Settings;

        // 1. Primer arranque: AppLanguage vacío significa que el usuario aún no ha elegido idioma.
        if (string.IsNullOrEmpty(settings.AppLanguage))
        {
            string? elegido = await new LanguageWindow().ShowDialog<string?>(this);

            // Asignar SelectedLanguage aplica el idioma y lo guarda: su setter llama a
            // I18n.ChangeLanguage y guarda settings.json, así que no hay que repetirlo aquí.
            vm.SelectedLanguage = vm.AvailableLanguages.FirstOrDefault(l => l.Code == elegido)
                                  ?? vm.AvailableLanguages[0];
        }

        // 2. Si hay una posición guardada, se restauran la posición y el tamaño.
        if (settings.WindowX != -1 && settings.WindowY != -1)
        {
            Position = new PixelPoint(settings.WindowX, settings.WindowY);
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }

        // 3. Restos de una actualización anterior (la carpeta de preparación, las versiones
        // apartadas). Con unos segundos de margen: el lanzador que la aplicó puede seguir abierto.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            ActualizacionService.LimpiarRestos();
        });

        // 4. Con la ventana ya en su sitio, se mira si hay una versión nueva del programa y luego
        // del mod de Geode. Van al final para que los avisos no se crucen con el selector de
        // idioma del primer arranque. Si se está aplicando una actualización del programa, la
        // ventana se está cerrando y lo del mod se deja para el siguiente arranque.
        if (await vm.ComprobarActualizacionDelProgramaAsync(this)) return;
        await vm.ComprobarActualizacionDelModAsync(this);
    }

    // true cuando ya se guardó settings.json y la ventana puede cerrarse de verdad.
    private bool _ajustesGuardadosAlCerrar;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // El primer intento de cierre se cancela para poder esperar al guardado, que es
        // asíncrono: al cerrarse la última ventana la aplicación termina, y un guardado sin
        // esperar podría quedarse a medias. Cuando termina, GuardarYCerrarAsync vuelve a cerrar.
        if (!_ajustesGuardadosAlCerrar)
        {
            e.Cancel = true;
            _ = GuardarYCerrarAsync();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Guarda la posición y el tamaño de la ventana en settings.json y cierra la ventana. Guarda
    /// sin reconectar los enlaces de chat, que no tiene sentido hacer al salir.
    /// </summary>
    private async Task GuardarYCerrarAsync()
    {
        var vm = MainWindowViewModel.Instance.SettingsVM;
        var settings = vm.Settings;

        // Posición y tamaño actuales de la ventana.
        settings.WindowX = Position.X;
        settings.WindowY = Position.Y;
        settings.WindowWidth = (int)Width;
        settings.WindowHeight = (int)Height;

        try
        {
            await vm.GuardarSinReconectarAsync();
        }
        finally
        {
            _ajustesGuardadosAlCerrar = true;
            Close();
        }
    }
}
