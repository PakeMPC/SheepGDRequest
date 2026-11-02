using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SheepGDRequest.Converters;

/// <summary>
/// Muestra "-" cuando el texto está vacío. Se usa en las insignias de AREDL y GDDL de la
/// ventana, que se dibujan siempre aunque el nivel no esté en esas listas.
///
/// Es solo presentación. AredlRank y GddlTier viajan dentro de queue_data.json, y tanto el mod
/// de Geode como los widgets de OBS interpretan un valor vacío como "este nivel no está en la
/// lista, no se dibuja la insignia". Si el guion se guardara en el modelo, el mod mostraría una
/// insignia con un guion dentro. Por eso se resuelve aquí y no en el modelo.
/// </summary>
public class EmptyToDashConverter : IValueConverter
{
    // Instancia única para usar desde XAML con x:Static.
    public static readonly EmptyToDashConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string texto = value as string ?? "";
        return string.IsNullOrWhiteSpace(texto) ? "-" : texto;
    }

    // Solo se usa en un sentido (del modelo a la vista).
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
