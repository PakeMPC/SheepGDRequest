using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using SheepGDRequest.ViewModels;
using SheepGDRequest.Views;

namespace SheepGDRequest;

/// <summary>
/// Arranque de la interfaz: crea la ventana principal con su ViewModel y lanza en segundo plano
/// la preparación del navegador y la conexión de los enlaces de chat guardados.
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            // La preparación del navegador (instalar Chromium para Playwright si falta) empieza
            // cuanto antes y sin bloquear el arranque. StartListeningForWidget espera a esta misma
            // tarea, así que ninguna conexión intenta abrir Chromium antes de que esté listo.
            _ = Services.SheepChatListener.EnsureBrowsersAsync();

            // Conexión de los enlaces de chat guardados, en segundo plano. StartListeningAsync
            // espera por su cuenta a que se cargue settings.json y escribe sus errores en la
            // consola.
            _ = System.Threading.Tasks.Task.Run(() => new Services.SheepChatListener().StartListeningAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
