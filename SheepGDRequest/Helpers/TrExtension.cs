using System;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using SheepGDRequest.Services;

namespace SheepGDRequest.Helpers;

/// <summary>
/// Extensión de marcado para traducir textos desde XAML, con la forma {i18n:Tr UI_Clave}.
/// Devuelve un Binding a I18nNotifier.LanguageTrigger cuyo convertidor devuelve I18n.Tr(Key).
/// Cada vez que cambia el idioma, I18nNotifier avisa del cambio de esa propiedad, el binding se
/// vuelve a evaluar y el texto sale traducido al idioma nuevo, sin reiniciar la aplicación.
/// </summary>
public class TrExtension : MarkupExtension
{
    public string Key { get; set; }

    public TrExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // El código de idioma enlazado solo sirve de aviso; el texto lo pone el convertidor.
        return new Binding(nameof(I18nNotifier.LanguageTrigger))
        {
            Source = I18nNotifier.Instance,
            Mode = BindingMode.OneWay,
            Converter = new TranslatorConverter(Key)
        };
    }

    // ==========================================
    // CONVERTIDOR INTERNO
    // ==========================================
    /// <summary>
    /// Traduce siempre la misma clave. El valor que recibe, el código de idioma, no se usa.
    /// </summary>
    private class TranslatorConverter : IValueConverter
    {
        private readonly string _key;

        public TranslatorConverter(string key)
        {
            _key = key;
        }

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            // Se ejecuta al crear el binding y cada vez que cambia el idioma.
            return I18n.Tr(_key);
        }

        // Solo se usa en un sentido (de las traducciones a la vista).
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
