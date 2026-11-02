using System.Text.Json.Serialization;
using SheepGDRequest.Services;

namespace SheepGDRequest.Models;

/// <summary>
/// Contenido de theme.json, el manifiesto de cada tema de widgets (un .zip creado con el Editor).
/// WidgetThemeManager lo lee al escanear los temas. Esta aplicación solo usa name, type y author:
/// el nombre que se muestra en los desplegables de Ajustes y la lista en la que va cada tema.
/// </summary>
public class WidgetThemeManifest
{
    // Vacío por defecto, y no "Desconocido": el texto de un tema sin nombre lo pone ToString(), en
    // el idioma activo, y así aquí se conserva el dato tal como viene.
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    // "unified", "notification", "playing" o "queue": decide en qué desplegables aparece el tema.
    // Un tema unificado aparece en los cuatro.
    [JsonPropertyName("type")]
    public string Type { get; set; } = "unified";

    // Vacío por defecto por la misma razón, y además porque ToString() muestra solo el nombre
    // cuando no hay autor. Con "Desconocido" como valor por defecto, esa comprobación no se cumplía
    // nunca y un tema sin autor salía como "Mi tema - por Desconocido".
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    // Lo que sigue lo escribe el Editor; esta aplicación no lo usa.
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("resolution")]
    public WidgetResolution Resolution { get; set; } = new();

    [JsonPropertyName("positions")]
    public WidgetPositions Positions { get; set; } = new();

    [JsonPropertyName("assets")]
    public WidgetAssets Assets { get; set; } = new();

    /// <summary>
    /// Nombre que se muestra en los desplegables: "nombre - por autor", o solo el nombre si no
    /// hay autor.
    /// </summary>
    public override string ToString()
    {
        // IsNullOrWhiteSpace y no "??": un theme.json puede traer "name": "" además de null.
        string nombre = string.IsNullOrWhiteSpace(Name) ? I18n.Tr("Default_UnknownName") : Name;

        // Sin autor, solo el nombre.
        if (string.IsNullOrWhiteSpace(Author))
            return nombre;

        // Plantilla con huecos y no concatenación: el orden de las palabras depende del idioma.
        return string.Format(I18n.Tr("UI_ThemeByAuthor"), nombre, Author);
    }
}

// Partes de theme.json que escribe el Editor y que esta aplicación no usa.
public class WidgetResolution
{
    [JsonPropertyName("width")]
    public int Width { get; set; } = 1920;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 1080;
}

public class WidgetPositions
{
    [JsonPropertyName("notification")]
    public WidgetPosition Notification { get; set; } = new();

    [JsonPropertyName("queue")]
    public WidgetPosition Queue { get; set; } = new();

    [JsonPropertyName("playing")]
    public WidgetPosition Playing { get; set; } = new();
}

public class WidgetPosition
{
    [JsonPropertyName("x")]
    public int X { get; set; } = 0;

    [JsonPropertyName("y")]
    public int Y { get; set; } = 0;
}

public class WidgetAssets
{
    [JsonPropertyName("customFont")]
    public string? CustomFont { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }
}

/// <summary>
/// Una entrada de los desplegables de temas de Ajustes. DisplayName se calcula al escanear los
/// temas, en el idioma de ese momento; al cambiar de idioma, SettingsViewModel vuelve a escanear
/// y a emparejar los temas elegidos por FileName.
/// </summary>
public class ThemeItem
{
    public string DisplayName { get; set; } = "";
    // Nombre del .zip ("default" en el tema por defecto). Es lo que guarda settings.json para
    // recordar el tema elegido.
    public string FileName { get; set; } = "";

    // El ComboBox muestra el texto de ToString().
    public override string ToString() => DisplayName;
}
