using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SheepGDRequest.Converters;

/// <summary>
/// Devuelve el color del borde de cada notificación en el panel de la ventana según su tipo.
/// </summary>
public class NotificationColorConverter : IValueConverter
{
    public static readonly NotificationColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string type)
        {
            return type switch
            {
                "Error" => Brushes.IndianRed,
                "Warning" => Brushes.Orange,
                "Success" => Brushes.MediumSeaGreen,
                _ => Brushes.LightSkyBlue // Info y cualquier otro tipo
            };
        }
        return Brushes.White;
    }
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
