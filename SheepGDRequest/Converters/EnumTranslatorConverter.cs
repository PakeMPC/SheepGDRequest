using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using SheepGDRequest.Services;

namespace SheepGDRequest.Converters;

/// <summary>
/// Convierte un valor de enum (por ejemplo, DifficultyRating.EasyDemon) en su texto traducido
/// para la interfaz, sin cambiar el valor que se guarda en el JSON. Se usa en los desplegables
/// de Ajustes: plataforma, condición y dificultad del filtro, y unidad de tiempo.
///
/// Los desplegables lo usan en un MultiBinding con el valor y con
/// <c>I18nNotifier.LanguageTrigger</c>: el segundo valor no se usa, pero hace que el texto se
/// vuelva a traducir al cambiar de idioma, sin recrear la vista.
/// </summary>
public class EnumTranslatorConverter : IValueConverter, IMultiValueConverter
{
    // Instancia única para usar desde XAML con x:Static.
    public static readonly EnumTranslatorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return null;

        // La clave combina el tipo de enum y el valor, por ejemplo
        // "Enum_DifficultyRating_EasyDemon". Los cuatro enums de los desplegables tienen todas sus
        // claves en los tres idiomas.
        string enumType = value.GetType().Name;
        string enumValue = value.ToString() ?? "";
        string key = $"Enum_{enumType}_{enumValue}";

        return I18n.Tr(key);
    }

    /// <summary>
    /// Versión para MultiBinding: traduce el primer valor; el resto (el idioma) solo provoca que
    /// se vuelva a evaluar.
    /// </summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        return values.Count > 0 ? Convert(values[0], targetType, parameter, culture) : null;
    }

    // Solo se usa en un sentido: los desplegables enlazan el valor del enum directamente.
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("La conversión inversa no es necesaria para los menús desplegables de Avalonia.");
    }
}
