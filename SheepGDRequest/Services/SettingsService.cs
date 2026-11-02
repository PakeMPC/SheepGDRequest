using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SheepGDRequest.Models;

namespace SheepGDRequest.Services;

/// <summary>
/// Guarda y carga la configuración del usuario (settings.json) de forma asíncrona.
///
/// El guardado es atómico (archivo temporal y renombrado), así que un corte a mitad de escritura
/// no deja el archivo vacío. Si settings.json existe pero no se puede leer, se aparta con otro
/// nombre antes de seguir con los valores por defecto, y <see cref="CopiaDelArchivoIlegible"/>
/// dice dónde quedó para poder avisar al usuario.
/// </summary>
public class SettingsService
{
    private readonly string _settingsFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Ruta a la que se movió settings.json en la última carga, si no se pudo leer; null si se
    /// leyó bien.
    /// </summary>
    public string? CopiaDelArchivoIlegible { get; private set; }

    /// <summary>
    /// Configura la ruta del archivo y las opciones del serializador JSON.
    /// </summary>
    public SettingsService()
    {
        _settingsFilePath = AppPaths.SettingsFile;

        // JSON con sangría, legible a mano, y enums guardados por nombre. Un nombre que ya no
        // existe (un valor renombrado o quitado en una versión nueva) se lee como el valor por
        // defecto de ese enum, en lugar de impedir la lectura de todo el archivo.
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new EnumToleranteFactory() }
        };
    }

    /// <summary>
    /// Carga la configuración del usuario desde el disco.
    /// Si el archivo no existe, crea uno con los valores por defecto. Si existe pero no se puede
    /// leer, lo renombra a "settings.ilegible-fecha.json", lo anota en
    /// <see cref="CopiaDelArchivoIlegible"/> y devuelve los valores por defecto.
    ///
    /// También aplica el idioma guardado, de modo que quien la llama ya trabaja en ese idioma.
    /// </summary>
    /// <returns>La configuración cargada.</returns>
    public async Task<SettingsModel> LoadSettingsAsync()
    {
        CopiaDelArchivoIlegible = null;

        if (!File.Exists(_settingsFilePath))
        {
            var defaultSettings = new SettingsModel();
            await SaveSettingsAsync(defaultSettings);
            Console.WriteLine(I18n.Tr("Log_SettingsCreated"));
            return defaultSettings;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<SettingsModel>(json, _jsonOptions)
                           ?? throw new JsonException("settings.json vacío");

            // Si hay un idioma guardado, se aplica de inmediato. Si está vacío no se aplica: el
            // valor vacío indica que el usuario todavía no ha elegido, y MainWindow abre el
            // selector de idioma.
            if (!string.IsNullOrEmpty(settings.AppLanguage))
            {
                I18n.ChangeLanguage(settings.AppLanguage);
            }

            return settings;
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_SettingsLoadError"), ex.Message));

            // Se aparta el archivo para que el siguiente guardado no lo sobrescriba.
            try
            {
                string carpeta = Path.GetDirectoryName(_settingsFilePath) ?? "";
                string copia = Path.Combine(carpeta, $"settings.ilegible-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Move(_settingsFilePath, copia, overwrite: true);
                CopiaDelArchivoIlegible = copia;
                Console.WriteLine(string.Format(I18n.Tr("Log_SettingsMovedAside"), copia));
            }
            catch (Exception exCopia)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_SettingsLoadError"), exCopia.Message));
            }

            return new SettingsModel();
        }
    }

    /// <summary>
    /// Guarda la configuración en settings.json: escribe un archivo temporal y lo renombra encima
    /// del anterior, así que quien lo lea ve la versión anterior o la nueva, nunca una a medias.
    /// </summary>
    /// <param name="settings">La configuración que se guarda.</param>
    public async Task SaveSettingsAsync(SettingsModel settings)
    {
        string temporal = _settingsFilePath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(temporal, json);
            File.Move(temporal, _settingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_SettingsSaveError"), ex.Message));
        }
    }

    /// <summary>
    /// Crea, para cada enum, un conversor que guarda el valor por nombre y que, al leer un nombre
    /// que no existe, usa el valor por defecto del enum en lugar de lanzar una excepción.
    /// </summary>
    private sealed class EnumToleranteFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(EnumTolerante<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class EnumTolerante<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var valor)
                && Enum.IsDefined(valor))
                return valor;

            // Compatibilidad con un número (como lo guardaría System.Text.Json sin conversor).
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n)
                && Enum.IsDefined(typeof(T), n))
                return (T)Enum.ToObject(typeof(T), n);

            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
