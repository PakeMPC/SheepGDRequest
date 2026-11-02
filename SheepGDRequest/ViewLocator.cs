using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SheepGDRequest.ViewModels;

namespace SheepGDRequest;

/// <summary>
/// Localizador de vistas que trae la plantilla de proyecto de Avalonia: dado un ViewModel, crea
/// la vista cuyo nombre completo resulta de cambiar "ViewModel" por "View" (por ejemplo,
/// SheepGDRequest.ViewModels.SettingsViewModel → SheepGDRequest.Views.SettingsView). Se registra
/// en App.axaml y se aplica a cualquier ViewModelBase que se muestre como contenido.
///
/// Nota: en esta aplicación las vistas se colocan a mano en el XAML (SettingsView dentro de
/// MainWindow), así que en la práctica no se usa. Su texto "Not Found" solo aparecería si
/// faltara una vista, y está fijo, fuera de I18n.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;
        
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        
        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
