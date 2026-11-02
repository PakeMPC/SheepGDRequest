using SheepGDRequest.Services;

namespace SheepGDRequest.Models;

/// <summary>
/// Una petición de nivel: los datos del nivel que obtiene GeometryDashService y quién lo pidió.
/// Se guarda en queue_data.json, que lee el mod de Geode, y se envía a los widgets por WebSocket,
/// así que cambiar el nombre de una propiedad rompe a esos consumidores.
/// </summary>
public class LevelRequestModel
{
    // Identifica la petición, no el nivel: distingue dos peticiones del mismo nivel.
    public string RequestId { get; set; } = System.Guid.NewGuid().ToString();
    public string LevelId { get; set; } = "";

    // Valores por defecto: los de texto se traducen al crear el objeto y GeometryDashService los
    // sustituye por los datos del nivel. DifficultyName va siempre en inglés y en mayúsculas
    // (GenericListener lo usa para filtrar), y Default_Unrated vale "UNRATED" en todos los idiomas.
    public string Name { get; set; } = I18n.Tr("Default_UnknownName");
    public string Author { get; set; } = "-"; // GeometryDashService pone el nombre del creador
    public string DifficultyName { get; set; } = I18n.Tr("Default_Unrated");

    // Estadísticas. Descargas y likes llegan ya compactados como texto ("79.7M", "648.6K").
    public int Stars { get; set; } = 0;
    public string Downloads { get; set; } = "0";
    public string Likes { get; set; } = "0";
    // Likes va siempre en positivo; si el valor que da GD es negativo, IsDislike vale true. La
    // ventana principal lo usa para elegir el icono de like o de dislike.
    public bool IsDislike { get; set; } = false;
    // ID del mensaje de Discord que originó la petición; vacío en otras plataformas. Evita añadirla
    // dos veces si la aprueban varios moderadores y permite quitarla si se borra el mensaje.
    public string DiscordMessageId { get; set; } = "";

    // Duración: "Tiny", "Short", "Medium", "Long", "XL" o, en los platformers, "Plat."; "NA" por
    // defecto. StarOrMoonConverter y el mod reconocen los platformers por la subcadena "PLAT".
    public string Length { get; set; } = I18n.Tr("Default_NA");

    public int Orbs { get; set; } = 0;
    public int Coins { get; set; } = 0;
    public string CustomSongId { get; set; } = "";

    // Muestran u ocultan cada moneda de la ventana principal según el número de monedas.
    public bool HasCoin1 => Coins >= 1;
    public bool HasCoin2 => Coins >= 2;
    public bool HasCoin3 => Coins >= 3;

    // Listas de la comunidad (GDDL y AREDL). Vacío significa que el nivel no está en esa lista. No
    // se debe escribir "-" aquí: el guion solo lo pone EmptyToDashConverter al mostrarlo.
    public string GddlTier { get; set; } = ""; // Tier de GDDL, con un decimal como máximo
    // Color de fondo del botón de GDDL: más oscuro cuanto más alto es el tier.
    public string GddlTierColor { get; set; } = "#D4A1D8";
    // Posición en AREDL con el "#" ya puesto ("#1341"). La ventana principal no usa HasAredl; viaja
    // en el JSON que reciben el mod y los widgets.
    public string AredlRank { get; set; } = "";
    public bool HasAredl => !string.IsNullOrEmpty(AredlRank);

    // Rutas de imágenes dentro de Assets. StringToBitmapConverter las convierte en Bitmap.
    public string DifficultyFacePath { get; set; } = "/Assets/GDassets/faces/unrated.png";
    // Brillo alrededor de la cara de dificultad: featured, epic, legendary o mythic.
    public string? FeatureGlowPath { get; set; } = null; // null si el nivel no tiene ninguno
    public bool HasFeatureGlow => !string.IsNullOrEmpty(FeatureGlowPath);
    // Miniatura del nivel en levelthumbs.prevter.me, un servicio externo de la comunidad. La
    // ventana principal la usa como fondo desenfocado.
    public string ThumbnailUrl => $"https://levelthumbs.prevter.me/thumbnail/{LevelId}";

    // Quién pidió el nivel y desde qué plataforma. Los rellena GenericListener para todas ellas.
    public string RequesterName { get; set; } = "";
    public string PlatformIconPath { get; set; } = "/Assets/GDassets/icons/twitch.png";

    // Estado global que viaja con cada nivel para los widgets. Son getters que leen
    // MainWindowViewModel.Instance: se incluyen al serializar y se ignoran al leer el JSON.
    // TimeRemainingText es el tiempo restante del auto-skip en el momento de serializar.
    public string TimeRemainingText => ViewModels.MainWindowViewModel.Instance?.TimeRemainingText ?? "";

    /// <summary>
    /// Instante absoluto en que salta el auto-skip, en segundos Unix (UTC). 0 significa que el
    /// nivel no tiene auto-skip, o que está desactivado, y no se dibuja contador.
    ///
    /// A diferencia de TimeRemainingText, que es un getter calculado, esta propiedad tiene set
    /// porque se guarda en queue_data.json. Se escribe una vez por nivel y cada consumidor calcula
    /// "fin − ahora" por su cuenta, así que no hace falta reescribir el JSON cada segundo.
    /// </summary>
    public long AutoSkipEndsAtUnix { get; set; } = 0;

    // Si las peticiones están abiertas. También es estado global para los widgets.
    public bool IsRequestsEnabled => ViewModels.MainWindowViewModel.Instance?.SettingsVM.Settings.IsRequestsEnabled ?? true;
}
