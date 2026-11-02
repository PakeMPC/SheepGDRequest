using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SheepGDRequest.Converters;

/// <summary>
/// Convierte la ruta de un recurso de la aplicación en un Bitmap para Image.Source. Hace falta
/// porque un binding a Image.Source no convierte texto en imagen en tiempo de ejecución; eso
/// solo lo hace el compilador de XAML con valores literales.
///
/// Acepta las rutas "/Assets/..." que guarda GeometryDashService (caras de dificultad y bordes)
/// y las "avares://..." completas de los iconos de plataforma. Se usa en el nivel actual y en
/// la lista de la cola de la ventana principal. Crea un Bitmap nuevo en cada conversión, sin
/// caché; con imágenes pequeñas, el coste es bajo.
/// </summary>
public class StringToBitmapConverter : IValueConverter
{
    // Instancia única para usar desde XAML con x:Static.
    public static readonly StringToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string uriString && !string.IsNullOrWhiteSpace(uriString))
        {
            try
            {
                // Las rutas de GeometryDashService empiezan por "/Assets/...": se les añade el
                // prefijo "avares://" que necesita AssetLoader.
                if (uriString.StartsWith("/Assets"))
                {
                    uriString = $"avares://SheepGDRequest{uriString}";
                }

                // Carga el recurso como Bitmap.
                return new Bitmap(AssetLoader.Open(new Uri(uriString)));
            }
            catch
            {
                // Si el recurso no existe, no se muestra imagen, en lugar de fallar.
                return null;
            }
        }
        return null;
    }

    // Solo se usa en un sentido (del modelo a la vista).
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
