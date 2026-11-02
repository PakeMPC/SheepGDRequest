using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SheepGDRequest.Converters;

/// <summary>
/// Elige el icono de estrella o de luna según la duración del nivel: los platformers dan
/// lunas, y desde GD 2.2 tienen su propio icono.
///
/// Usa Length, y no un campo nuevo, a propósito y igual que el mod: sheep::isPlatformer busca la
/// subcadena "PLAT" en ese mismo valor. Así los dos programas dependen del mismo dato.
///
/// Nota: devuelve un Bitmap, no la ruta. Un binding a Image.Source no convierte texto en imagen
/// en tiempo de ejecución; eso solo lo hace el compilador de XAML con valores literales. Es el
/// mismo motivo por el que existe StringToBitmapConverter.
/// </summary>
public class StarOrMoonConverter : IValueConverter
{
    // Instancia única para usar desde XAML con x:Static.
    public static readonly StarOrMoonConverter Instance = new();

    // Son solo dos imágenes y no cambian: se cargan una vez y se reutilizan.
    private static Bitmap? _estrella;
    private static Bitmap? _luna;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string longitud = (value as string ?? "").ToUpperInvariant();

        return longitud.Contains("PLAT")
            ? (_luna ??= Cargar("moon.png"))
            : (_estrella ??= Cargar("star.png"));
    }

    /// <summary>
    /// Carga un icono de Assets/GDassets/icons. Devuelve null si no se puede cargar.
    /// </summary>
    private static Bitmap? Cargar(string archivo)
    {
        try
        {
            var uri = new Uri($"avares://SheepGDRequest/Assets/GDassets/icons/{archivo}");
            return new Bitmap(AssetLoader.Open(uri));
        }
        catch
        {
            return null; // Si falta el recurso, no se muestra icono, en lugar de fallar
        }
    }

    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
