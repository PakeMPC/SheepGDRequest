using Avalonia;
using System;

namespace SheepGDEditor;

/// <summary>
/// El punto de entrada del Editor: arranca Avalonia, que llama a App (App.axaml.cs) para montar
/// la red de excepciones y abrir la ventana principal.
/// </summary>
sealed class Program
{
    // OJO: aquí no se puede usar nada de Avalonia ni de otras librerías, ni nada que dependa de
    // un SynchronizationContext, hasta que se llame a AppMain: todavía no está inicializado y
    // puede fallar. (Es el aviso de la plantilla de Avalonia, y por eso la red de excepciones se
    // monta en App.axaml.cs y no aquí.)
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// La configuración de Avalonia. No se puede quitar: el visor de diseño del IDE también la
    /// usa para enseñar los .axaml.
    ///
    /// UsePlatformDetect elige solo el sistema de ventanas (Windows, X11...). Las herramientas de
    /// desarrollo (F12) solo van en Debug, y su paquete, AvaloniaUI.DiagnosticsSupport, ni
    /// siquiera se incluye en las otras configuraciones. WithInterFont registra la fuente Inter
    /// que trae Avalonia, que es con la que nace todo texto nuevo del lienzo. LogToTrace manda
    /// los avisos de Avalonia a System.Diagnostics.Trace.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
