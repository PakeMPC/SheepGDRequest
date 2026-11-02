using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SheepGDRequest.Models;
using Microsoft.Playwright;

namespace SheepGDRequest.Services;

/// <summary>
/// Resultado de una búsqueda en Geometry Dash. Distingue "el nivel no existe" (el espectador
/// pidió algo que no hay) de los fallos de los servidores, que no son culpa suya.
/// </summary>
public enum ResultadoBusqueda { Encontrado, NoExiste, ErrorServidor, TiempoAgotado }

/// <summary>
/// Lo que devuelve <see cref="GeometryDashService.BuscarNivelAsync"/>: el resultado y, si se
/// encontró, el nivel.
/// </summary>
public sealed record BusquedaNivel(ResultadoBusqueda Resultado, LevelRequestModel? Nivel);

/// <summary>
/// Servicio encargado de comunicarse con los servidores oficiales de Geometry Dash (Boomlings)
/// y con las APIs de la comunidad (GDDL, AREDL) para obtener información detallada de los
/// niveles.
///
/// La búsqueda en GD (<see cref="BuscarNivelAsync"/>) y las listas de la comunidad
/// (<see cref="CompletarListasAsync"/>) van por separado: GenericListener añade el nivel en cuanto
/// responde GD, y las listas se piden después, con su propio plazo, sin retrasar ni hacer fallar
/// la petición.
/// </summary>
public class GeometryDashService
{
    /// <summary>
    /// Cliente HTTP para la API de RobTop. Va sin User-Agent ni cabeceras de navegador, igual que
    /// las peticiones del propio juego: los servidores de GD rechazan las que no lo parecen. Cada
    /// intento lleva además su propio plazo (el CancellationToken de quien llama); el Timeout es
    /// solo un tope por si se llama sin plazo.
    /// </summary>
    private static readonly HttpClient RobTopClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Cliente HTTP para las APIs de la comunidad (GDDL y AREDL). Lleva cabeceras de navegador
    /// (User-Agent, Accept, Origin, Referer) para que Cloudflare no rechace las peticiones.
    /// </summary>
    private static readonly HttpClient CommunityClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    static GeometryDashService()
    {
        // 1. User-Agent de Chrome en Windows. Son peticiones HTTP sin navegador, así que no hay
        // nada con lo que deba coincidir: basta con que parezca un navegador para Cloudflare.
        CommunityClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // 2. Cabeceras adicionales para evitar el error 403 de Cloudflare.
        CommunityClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        CommunityClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,es;q=0.8");
        // Origin y Referer de aredl.net también se envían a GDDL, porque el cliente es compartido;
        // no afectan a sus respuestas.
        CommunityClient.DefaultRequestHeaders.Add("Origin", "https://aredl.net");
        CommunityClient.DefaultRequestHeaders.Add("Referer", "https://aredl.net/");
    }

    private const string BoomlingsUrl = "http://www.boomlings.com/database/getGJLevels21.php";
    private const string GddlApiUrl = "https://gdladder.com/api/levels/";
    private const string AredlApiUrl = "https://api.aredl.net/v2/api/aredl/levels/";

    /// <summary>
    /// Busca un nivel en Geometry Dash. Si la búsqueda es por texto en lugar de por ID, se queda
    /// con el primer resultado que devuelve GD. No pide las listas de la comunidad: para eso está
    /// <see cref="CompletarListasAsync"/>.
    ///
    /// No lanza excepciones: el resultado dice si el nivel se encontró, si no existe, si falló el
    /// servidor o si venció el plazo de <paramref name="ct"/>.
    /// </summary>
    /// <param name="query">La ID del nivel o el texto de búsqueda ingresado por el usuario.</param>
    /// <param name="ct">Plazo del intento: al cancelarse, la petición a GD se corta.</param>
    public async Task<BusquedaNivel> BuscarNivelAsync(string query, CancellationToken ct = default)
    {
        string rawData;
        try
        {
            // Parámetros requeridos por la API de Boomlings. El "secret" es la clave pública que
            // envían todos los clientes de GD, no un secreto real. type 0 = búsqueda normal.
            var parameters = new Dictionary<string, string>
            {
                { "secret", "Wmfd2893gb7" },
                { "type", "0" },
                { "str", query }
            };

            var content = new FormUrlEncodedContent(parameters);

            // RobTopClient, sin cabeceras de navegador, para que Boomlings no rechace la petición.
            using var response = await RobTopClient.PostAsync(BoomlingsUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_GDHttpError"), (int)response.StatusCode));
                return new BusquedaNivel(ResultadoBusqueda.ErrorServidor, null);
            }

            rawData = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BusquedaNivel(ResultadoBusqueda.TiempoAgotado, null);
        }
        catch (Exception ex)
        {
            // Sin red, DNS, conexión cortada...: fallo del servidor o de la red, no del nivel.
            Console.WriteLine(string.Format(I18n.Tr("Log_GDInternalError"), ex.Message));
            return new BusquedaNivel(ResultadoBusqueda.ErrorServidor, null);
        }

        // Si RobTop responde con -1, el nivel no existe o se borró.
        if (rawData.Trim() == "-1")
        {
            Console.WriteLine(I18n.Tr("Log_GDLevelNotFound"));
            return new BusquedaNivel(ResultadoBusqueda.NoExiste, null);
        }
        if (string.IsNullOrWhiteSpace(rawData) || rawData.StartsWith("error"))
        {
            Console.WriteLine(I18n.Tr("Log_GDServerError"));
            return new BusquedaNivel(ResultadoBusqueda.ErrorServidor, null);
        }

        try
        {
            return new BusquedaNivel(ResultadoBusqueda.Encontrado, LeerRespuesta(rawData, query));
        }
        catch (Exception ex)
        {
            // Una respuesta con un formato inesperado.
            Console.WriteLine(string.Format(I18n.Tr("Log_GDInternalError"), ex.Message));
            return new BusquedaNivel(ResultadoBusqueda.ErrorServidor, null);
        }
    }

    /// <summary>
    /// Convierte la respuesta de Boomlings en un LevelRequestModel. Es pública para poder probarla
    /// con respuestas guardadas.
    /// </summary>
    public static LevelRequestModel LeerRespuesta(string rawData, string query)
    {
        // Desempaquetado de la respuesta de RobTop (formato de pares clave:valor). "#" separa
        // secciones: la 0 son los niveles (separados por "|", cada uno en pares
        // "clave:valor:clave:valor") y la 1 los creadores ("idUsuario:nombre:idCuenta").
        var sections = rawData.Split('#');
        var levelsArray = sections[0].Split('|');

        string? selectedLevelData = null;
        // Con un ID se busca entre los resultados el nivel que tiene exactamente ese ID. Si no
        // está, o la búsqueda era por texto, se toma el primero.
        bool isNumericSearch = long.TryParse(query, out _);

        if (isNumericSearch)
        {
            foreach (var lvl in levelsArray)
            {
                var tempDict = lvl.Split(':');
                for (int i = 0; i < tempDict.Length - 1; i += 2)
                {
                    if (tempDict[i] == "1" && tempDict[i + 1] == query)
                    {
                        selectedLevelData = lvl;
                        break;
                    }
                }
                if (selectedLevelData != null) break;
            }
        }

        selectedLevelData ??= levelsArray[0];

        // Diccionario de creadores (sección 1).
        var creatorsDict = new Dictionary<string, string>();
        if (sections.Length > 1)
        {
            var creatorsArray = sections[1].Split('|');
            foreach (var creator in creatorsArray)
            {
                var cData = creator.Split(':');
                if (cData.Length >= 2)
                    creatorsDict[cData[0]] = cData[1];
            }
        }

        var levelDict = selectedLevelData.Split(':');
        var model = new LevelRequestModel();

        int difficultyKey = 0, isDemon = 0, isAuto = 0, demonType = 0;
        int featuredScore = 0, epicType = 0;
        string authorId = "";
        int lengthKey = 0;

        // Correspondencia entre las claves de RobTop y el modelo. Claves: 1 ID, 2 nombre, 6 ID
        // del creador, 9 dificultad (10 a 50), 10 descargas, 14 likes, 15 duración, 17 demon,
        // 18 estrellas, 19 featured, 25 auto, 35 canción, 37 monedas, 42 epic, 43 tipo de
        // demon.
        for (int i = 0; i < levelDict.Length - 1; i += 2)
        {
            string key = levelDict[i];
            string val = levelDict[i + 1];

            if (key == "1") model.LevelId = val;
            else if (key == "2") model.Name = val;
            else if (key == "6") authorId = val;
            else if (key == "9") int.TryParse(val, out difficultyKey);
            else if (key == "10") { if (long.TryParse(val, out long downloadsRaw)) model.Downloads = CompactNumber(downloadsRaw); }
            else if (key == "19") int.TryParse(val, out featuredScore);
            else if (key == "42") int.TryParse(val, out epicType);
            else if (key == "14")
            {
                if (long.TryParse(val, out long likesRaw))
                {
                    // El signo se guarda en IsDislike, así que el número se guarda siempre en
                    // positivo.
                    model.IsDislike = likesRaw < 0;
                    model.Likes = CompactNumber(Math.Abs(likesRaw));
                }
            }
            else if (key == "15") int.TryParse(val, out lengthKey);
            else if (key == "17") int.TryParse(val, out isDemon);
            else if (key == "18") { if (int.TryParse(val, out int stars)) model.Stars = stars; }
            else if (key == "25") int.TryParse(val, out isAuto);
            else if (key == "37") { if (int.TryParse(val, out int coins)) model.Coins = coins; }
            else if (key == "43") int.TryParse(val, out demonType);
            else if (key == "35") model.CustomSongId = val;
        }

        model.Author = creatorsDict.ContainsKey(authorId) ? creatorsDict[authorId] : I18n.Tr("Default_UnknownAuthor");
        // Nota sobre el valor 5: en GD 2.2 la clave 15 vale 5 para los niveles de plataforma,
        // que antes caían en el "_ => XL". El texto "Plat." no debe cambiarse: el mod lo
        // reconoce en sheep::isPlatformer, que busca la subcadena "PLAT", para elegir entre
        // estrellas y lunas, y StarOrMoonConverter lo usa para lo mismo en la ventana.
        model.Length = lengthKey switch { 0 => "Tiny", 1 => "Short", 2 => "Medium", 3 => "Long", 4 => "XL", 5 => "Plat.", _ => "XL" };
        // Orbes según las estrellas. Los platformers usan la misma tabla con las lunas
        // (comprobado en el juego: un platformer de 10 lunas da 500 orbes).
        model.Orbs = model.Stars switch { 1=>50, 2=>50, 3=>75, 4=>125, 5=>175, 6=>225, 7=>275, 8=>350, 9=>425, 10=>500, _=>0 };

        // Cara de dificultad oficial. DifficultyName va en inglés y en mayúsculas a propósito:
        // GenericListener lo convierte después al enum de los filtros a partir de estos textos
        // exactos. No cambiarlos sin revisar allí.
        if (model.Stars == 0)
        {
            model.DifficultyFacePath = "/Assets/GDassets/faces/unrated.png";
            model.DifficultyName = "UNRATED";
        }
        else if (isAuto == 1)
        {
            model.DifficultyFacePath = "/Assets/GDassets/faces/auto.png";
            model.DifficultyName = "AUTO";
        }
        else if (isDemon == 1)
        {
            model.DifficultyFacePath = demonType switch
            {
                3 => "/Assets/GDassets/faces/demon-easy.png",
                4 => "/Assets/GDassets/faces/demon-medium.png",
                // 0 es el valor por defecto de GD para un demon, que el juego pinta como Hard.
                0 => "/Assets/GDassets/faces/demon-hard.png",
                5 => "/Assets/GDassets/faces/demon-insane.png",
                6 => "/Assets/GDassets/faces/demon-extreme.png",
                _ => "/Assets/GDassets/faces/demon.png"
            };

            model.DifficultyName = demonType switch
            {
                3 => "EASY DEMON",
                4 => "MEDIUM DEMON",
                0 => "HARD DEMON",
                5 => "INSANE DEMON",
                6 => "EXTREME DEMON",
                _ => "HARD DEMON"
            };
        }
        else
        {
            model.DifficultyFacePath = difficultyKey switch
            {
                10 => "/Assets/GDassets/faces/easy.png",
                20 => "/Assets/GDassets/faces/normal.png",
                30 => "/Assets/GDassets/faces/hard.png",
                40 => "/Assets/GDassets/faces/harder.png",
                50 => "/Assets/GDassets/faces/insane.png",
                _ => "/Assets/GDassets/faces/unrated.png"
            };

            model.DifficultyName = difficultyKey switch
            {
                10 => "EASY",
                20 => "NORMAL",
                30 => "HARD",
                40 => "HARDER",
                50 => "INSANE",
                _ => "UNRATED"
            };
        }

        // Borde de Featured, Epic, Legendary o Mythic.
        if (epicType == 3) model.FeatureGlowPath = "/Assets/GDassets/faces/mythic.png";
        else if (epicType == 2) model.FeatureGlowPath = "/Assets/GDassets/faces/legendary.png";
        else if (epicType == 1) model.FeatureGlowPath = "/Assets/GDassets/faces/epic.png";
        else if (featuredScore > 0) model.FeatureGlowPath = "/Assets/GDassets/faces/featured.png";
        else model.FeatureGlowPath = null;

        return model;
    }

    /// <summary>
    /// Completa el nivel con su tier de GDDL y su posición en AREDL. Solo se consulta para los
    /// demons: las dos listas son de demons, y para el resto GDDL responde {} y AREDL 404. Las
    /// dos peticiones van en paralelo y cada una cubre sus propios fallos, que solo se anotan en
    /// la consola.
    /// </summary>
    /// <returns>true si se rellenó algún dato.</returns>
    public async Task<bool> CompletarListasAsync(LevelRequestModel model, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(model.LevelId)) return false;
        if (!model.DifficultyName.Contains("DEMON", StringComparison.OrdinalIgnoreCase)) return false;

        var gddl = PedirGddlAsync(model, ct);
        var aredl = PedirAredlAsync(model, ct);
        bool[] resultados = await Task.WhenAll(gddl, aredl);
        return resultados[0] || resultados[1];
    }

    private static async Task<bool> PedirGddlAsync(LevelRequestModel model, CancellationToken ct)
    {
        try
        {
            using var gddlResponse = await CommunityClient.GetAsync($"{GddlApiUrl}{model.LevelId}", ct);
            if (!gddlResponse.IsSuccessStatusCode)
            {
                // Las respuestas 404 o 403 de GDDL no son excepciones: sin esta línea no quedaría
                // constancia de ellas.
                Console.WriteLine(string.Format(I18n.Tr("Log_GDDLHttpError"), (int)gddlResponse.StatusCode, model.LevelId));
                return false;
            }

            using var gddlDoc = JsonDocument.Parse(await gddlResponse.Content.ReadAsStringAsync(ct));
            if (gddlDoc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!(gddlDoc.RootElement.TryGetProperty("Rating", out var tier) ||
                  gddlDoc.RootElement.TryGetProperty("Tier", out tier) ||
                  gddlDoc.RootElement.TryGetProperty("rating", out tier)))
                return false;
            if (tier.ValueKind == JsonValueKind.Null) return false;

            string rawTier = tier.ToString();
            if (double.TryParse(rawTier, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double tierDouble))
            {
                model.GddlTier = tierDouble.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

                // Color según el tier de GDDL.
                if (tierDouble >= 33) model.GddlTierColor = "#4A001E";
                else if (tierDouble >= 28) model.GddlTierColor = "#6E0B33";
                else if (tierDouble >= 23) model.GddlTierColor = "#9E2A59";
                else if (tierDouble >= 18) model.GddlTierColor = "#C25C8F";
                else model.GddlTierColor = "#D4A1D8";
            }
            else
            {
                model.GddlTier = rawTier;
            }
            return true;
        }
        catch
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_GDDLFailed"), model.LevelId));
            return false;
        }
    }

    private static async Task<bool> PedirAredlAsync(LevelRequestModel model, CancellationToken ct)
    {
        try
        {
            // CommunityClient, porque lleva las cabeceras de navegador (User-Agent de Chrome,
            // Accept, Origin, Referer).
            using var aredlResponse = await CommunityClient.GetAsync($"{AredlApiUrl}{model.LevelId}", ct);

            if (!aredlResponse.IsSuccessStatusCode)
            {
                // 404 es lo normal para un demon que no está en la lista.
                if (aredlResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
                    Console.WriteLine(string.Format(I18n.Tr("Log_AREDLHttpError"), (int)aredlResponse.StatusCode));
                return false;
            }

            var responseText = await aredlResponse.Content.ReadAsStringAsync(ct);

            // Comprueba que la respuesta sea JSON y no una página de bloqueo de Cloudflare.
            if (string.IsNullOrWhiteSpace(responseText) || !responseText.TrimStart().StartsWith("{"))
            {
                Console.WriteLine(I18n.Tr("Log_AREDLCloudflare"));
                return false;
            }

            using var aredlDoc = JsonDocument.Parse(responseText);
            var raiz = aredlDoc.RootElement;

            // La URL levels/{n} acepta tanto una ID de nivel como una posición de la lista. Con
            // una ID pequeña (los niveles más antiguos de GD) devolvería el nivel de ese puesto,
            // así que solo se acepta la respuesta si su level_id es el pedido.
            if (!raiz.TryGetProperty("level_id", out var levelId) ||
                levelId.ToString() != model.LevelId)
                return false;

            if (raiz.TryGetProperty("position", out var pos) ||
                raiz.TryGetProperty("placement", out pos) ||
                raiz.TryGetProperty("rank", out pos))
            {
                model.AredlRank = $"#{pos}";
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_AREDLCritical"), ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Compacta una cantidad a formato corto: 648 → "648", 79718951 → "79.7M", 1645779 → "1.6M".
    /// Es necesario porque los números completos no caben en la tarjeta "Now Playing" del mod de
    /// Geode ni en los widgets de OBS.
    /// </summary>
    private static string CompactNumber(long value)
    {
        bool isNegative = value < 0;
        long abs = Math.Abs(value);

        string text;
        if (abs >= 1_000_000) text = ScaleAndFormat(abs / 1_000_000d, "M");
        else if (abs >= 1_000) text = ScaleAndFormat(abs / 1_000d, "K");
        else text = abs.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // La rama negativa no se usa actualmente: los likes llegan ya en positivo (el signo está en
        // IsDislike).
        return isNegative ? "-" + text : text;
    }

    /// <summary>
    /// Deja un decimal y añade la unidad. Dos decisiones deliberadas:
    /// 1) Trunca en lugar de redondear, para que 999.999 no se convierta en "1000K".
    /// 2) Usa InvariantCulture, porque el valor viaja dentro de queue_data.json y lo leen el mod
    ///    (C++) y los widgets. Con la cultura del sistema en español saldría "79,7M".
    /// </summary>
    private static string ScaleAndFormat(double scaled, string unit)
    {
        double truncated = Math.Floor(scaled * 10) / 10;
        // El formato "0.#" omite el decimal cuando es cero: 2.0 → "2M", no "2.0M".
        return truncated.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + unit;
    }
}
