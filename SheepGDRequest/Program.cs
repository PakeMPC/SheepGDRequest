using Avalonia;
using System;
using System.Linq;

namespace SheepGDRequest;

/// <summary>
/// Punto de entrada. Prepara la carpeta de datos, atiende las opciones de instalación de
/// Chromium y arranca la interfaz de Avalonia.
/// </summary>
sealed class Program
{
    // Código de inicialización (nota de la plantilla de Avalonia): antes de que arranque Avalonia
    // no se deben usar sus API, las de terceros ni nada que dependa de SynchronizationContext,
    // porque todavía no están inicializadas.
    [STAThread]
    public static void Main(string[] args)
    {
        // Antes que nada se prepara y se migra la carpeta de datos: varios servicios calculan sus
        // rutas en el constructor, así que esto tiene que ejecutarse antes que Avalonia.
        Services.AppPaths.Initialize();

        // === OPCIONES DE LÍNEA DE COMANDOS PARA PLAYWRIGHT ===
        // "instalar-chromium" descarga el Chromium de Playwright, e "instalar-dependencias" instala
        // las librerías del sistema que necesita en Linux (ejecuta apt-get, así que requiere
        // permisos de administrador). En los dos casos el programa termina sin abrir la interfaz.
        if (args.Contains("instalar-chromium"))
        {
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            return; // No se abre la interfaz
        }
        if (args.Contains("instalar-dependencias"))
        {
            Microsoft.Playwright.Program.Main(new[] { "install-deps", "chromium" });
            return; // No se abre la interfaz
        }
        // ==============================================

        // Sin esas opciones, arranca la aplicación.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Configuración de Avalonia (de la plantilla). No se debe quitar: también la usa el diseñador.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
