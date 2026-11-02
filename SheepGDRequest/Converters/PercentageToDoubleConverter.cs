using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SheepGDRequest.Converters;

/// <summary>
/// Convierte un porcentaje entero (0 a 100) en la opacidad de Avalonia (0.0 a 1.0). Lo usa la
/// imagen de fondo de la ventana principal, enlazada al ajuste ThumbnailOpacity.
/// </summary>
public class PercentageToDoubleConverter : IValueConverter
{
    // Instancia única para usar desde XAML con x:Static.
    public static readonly PercentageToDoubleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Por ejemplo, el 70 del control deslizante pasa a 0.7.
        if (value is int intValue) return intValue / 100.0;
        return 1.0; // Valor inesperado: opacidad completa
    }

    // Solo se usa en un sentido (del ajuste a la vista).
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
