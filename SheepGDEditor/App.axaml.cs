using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SheepGDEditor.ViewModels;
using SheepGDEditor.Views;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SheepGDEditor;

/// <summary>
/// La aplicación del Editor: carga App.axaml, monta la red de excepciones y abre la ventana
/// principal.
/// </summary>
public partial class App : Application
{
    /// <summary>Carga App.axaml: el tema, los estilos y las fuentes.</summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Con Avalonia ya en pie: monta la red de excepciones y abre la ventana principal con su
    /// ViewModel.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        MontarRedDeExcepciones();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ==========================================
    // RED DE EXCEPCIONES
    // ==========================================
    // Va AQUÍ y no en Program.cs, aunque el plan escrito en el CLAUDE.md dijera "antes de
    // StartWithClassicDesktopLifetime": el propio comentario de la plantilla avisa de no tocar
    // APIs de Avalonia antes de AppMain, y Dispatcher.UIThread es justo eso. Aquí Avalonia ya
    // está en pie y todavía no ha corrido nada del usuario.
    //
    // Engancha los tres sitios por los que puede escaparse una excepción. Cada una se apunta en
    // rescate/errores.log, en la carpeta de datos, y la del hilo de la interfaz además vuelca el
    // tema abierto a rescate/<fecha y hora>, para no perder el trabajo.
    private static void MontarRedDeExcepciones()
    {
        // 1. Lo que llega por el bucle de la interfaz. Es el caso que tumbaba la aplicación: un
        //    [RelayCommand] async Task que lanza acaba en AwaitAndThrowIfFailed, que relanza
        //    aquí. Y es el único de los tres que corre SIEMPRE en el hilo de la interfaz, así que
        //    es el único desde el que se puede tocar el ViewModel para volcar el tema.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Atender("interfaz", e.Exception, volcar: true);

            // La aplicación sigue viva A PROPÓSITO: con la copia de rescate ya en disco, la
            // catástrofe (perder el trabajo) ya no puede pasar, y morir aquí solo obligaría a
            // recuperar desde la carpeta de rescate en vez de poder dar a Ctrl+S y salir limpio.
            //
            // OJO: el setter de Handled IGNORA el false, solo acepta true. Para dejarla morir no
            // se pone "Handled = false": se deja esta línea SIN ESCRIBIR.
            e.Handled = true;
        };

        // 2. Una Task que falla y a la que nadie hace await: se marca como vista y se apunta.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Atender("tarea sin observar", e.Exception, volcar: false);
        };

        // 3. Lo que se escapa de los dos anteriores. Aquí ya NO se puede evitar la muerte: solo
        //    da tiempo a apuntarlo.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Atender("dominio", e.ExceptionObject as Exception, volcar: false);
    }

    /// <summary>
    /// Apunta una excepción en rescate/errores.log (y en la consola) y, si volcar es true, antes
    /// vuelca el tema abierto con MainWindowViewModel.VolcarRescate. Nada de esto puede fallar
    /// hacia fuera: si el volcado falla, se apunta igual, y si ni eso se puede, se deja estar.
    ///
    /// volcar=false en los dos últimos enganches es deliberado: pueden saltar en CUALQUIER hilo
    /// (UnobservedTaskException lo hace en el del recolector), y tocar desde ahí las
    /// ObservableCollection del ViewModel sería empeorar las cosas justo cuando peor están. Esos
    /// dos solo apuntan el error. La red NO es uniforme, y más vale tenerlo escrito.
    /// </summary>
    /// <param name="origen">De dónde viene: "interfaz", "tarea sin observar" o "dominio".</param>
    /// <param name="ex">La excepción, si la hay.</param>
    /// <param name="volcar">Si hay que volcar antes el tema (solo desde el hilo de la
    /// interfaz).</param>
    private static void Atender(string origen, Exception? ex, bool volcar)
    {
        string? rescate = null;

        if (volcar)
        {
            try
            {
                var vm = (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                         ?.MainWindow?.DataContext as MainWindowViewModel;
                rescate = vm?.VolcarRescate(origen);
            }
            catch { /* que falle el volcado no puede impedir que se registre el error */ }
        }

        try
        {
            string carpeta = Path.Combine(SheepGDRequest.Services.AppPaths.DataDir, "rescate");
            Directory.CreateDirectory(carpeta);

            string texto =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] origen: {origen}" + Environment.NewLine +
                (ex?.ToString() ?? "(sin excepcion)") + Environment.NewLine +
                (rescate != null ? $"Tema volcado en: {rescate}" : "Sin volcado del tema.") +
                Environment.NewLine + new string('-', 70) + Environment.NewLine;

            File.AppendAllText(Path.Combine(carpeta, "errores.log"), texto);
            Console.WriteLine(texto);
        }
        catch { /* si no se puede ni registrar, no queda nada que hacer */ }
    }
}
