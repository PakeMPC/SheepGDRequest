using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SheepGDRequest.Models;

namespace SheepGDRequest.Services;

/// <summary>
/// Lista los temas de widgets instalados (los .zip de la carpeta de temas) para los
/// desplegables de Ajustes. Lee solo el theme.json de cada zip, sin descomprimirlo; quien
/// sirve los archivos del tema es WidgetServerService.
/// </summary>
public class WidgetThemeManager
{
    private readonly string _themesDirectory = AppPaths.WidgetThemesDir;

    public ObservableCollection<ThemeItem> NotificationThemes { get; } = new();
    public ObservableCollection<ThemeItem> PlayingThemes { get; } = new();
    public ObservableCollection<ThemeItem> QueueThemes { get; } = new();
    public ObservableCollection<ThemeItem> UnifiedThemes { get; } = new();

    public WidgetThemeManager()
    {
        ScanThemes();
    }

    /// <summary>
    /// Vuelve a llenar las cuatro listas desde cero. Se llama dos veces al arrancar: desde el
    /// constructor (SettingsViewModel lo crea en un inicializador de propiedad, todavía con el
    /// idioma por defecto) y después de cargar settings.json, ya en el idioma del usuario.
    /// </summary>
    public void ScanThemes()
    {
        ClearAndAddDefault(NotificationThemes);
        ClearAndAddDefault(PlayingThemes);
        ClearAndAddDefault(QueueThemes);
        ClearAndAddDefault(UnifiedThemes);

        if (!Directory.Exists(_themesDirectory))
        {
            Directory.CreateDirectory(_themesDirectory);
            return;
        }

        var zipFiles = Directory.GetFiles(_themesDirectory, "*.zip");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var zipPath in zipFiles)
        {
            // ClearAndAddDefault ya añade "Default" como entrada fija. Si además se leyera
            // default.zip, el tema aparecería dos veces en los desplegables.
            if (string.Equals(Path.GetFileName(zipPath), "default.zip", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                // Se abre el zip en modo lectura, sin descomprimirlo.
                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry("theme.json");

                if (entry != null)
                {
                    using var stream = entry.Open();
                    var manifest = JsonSerializer.Deserialize<WidgetThemeManifest>(stream, options);

                    if (manifest != null)
                    {
                        string fileName = Path.GetFileName(zipPath);
                        var themeItem = new ThemeItem
                        {
                            // Nombre visible: el que genera WidgetThemeManifest.ToString() en el
                            // idioma activo.
                            DisplayName = manifest.ToString(),
                            FileName = fileName
                        };
                        // Un "type" que no sea uno de estos cuatro deja el tema fuera de todas las
                        // listas, sin avisar.
                        string type = manifest.Type.ToLower();

                        // Distribución: el unificado aparece en todas las listas; los demás, solo
                        // en la suya.
                        if (type == "unified")
                        {
                            UnifiedThemes.Add(themeItem);
                            NotificationThemes.Add(themeItem);
                            PlayingThemes.Add(themeItem);
                            QueueThemes.Add(themeItem);
                        }
                        else if (type == "notification") NotificationThemes.Add(themeItem);
                        else if (type == "playing") PlayingThemes.Add(themeItem);
                        else if (type == "queue") QueueThemes.Add(themeItem);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_ThemeReadError"), zipPath, ex.Message));
            }
        }
    }

    /// <summary>
    /// Vacía una lista y añade la entrada del tema por defecto.
    /// </summary>
    private void ClearAndAddDefault(ObservableCollection<ThemeItem> collection)
    {
        collection.Clear();

        // DisplayName se traduce; FileName no. FileName es la clave con la que settings.json guarda
        // el tema elegido y con la que SettingsViewModel lo vuelve a seleccionar al cargar:
        // traducirla rompería la selección guardada al cambiar de idioma.
        collection.Add(new ThemeItem { DisplayName = I18n.Tr("UI_ThemeDefault"), FileName = "default" });
    }
}
