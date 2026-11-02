using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SheepGDRequest.Models;
using SheepGDRequest.ViewModels;

namespace SheepGDRequest.Services;

/// <summary>
/// Tramo común a todas las fuentes de chat: lo que ocurre entre "alguien escribió un comando" y
/// "el nivel está en la cola". Cada listener (Discord, TikTok, StreamElements, Social Stream y la
/// fuente "Otro") gestiona su conexión y extrae el texto; a partir de ahí, todos llaman a esta
/// clase.
///
/// Tiene cuatro piezas independientes:
/// <list type="bullet">
///   <item><c>TryParseCommand</c>: decide si el texto de un mensaje es una petición. Lo usan las
///   fuentes que traen el autor aparte (SheepChat, Ninja, StreamElements, TikTok).</item>
///   <item><c>TryParseGenericMessage</c>: extrae el usuario y el nivel de una línea sin
///   procesar (Discord y "Otro").</item>
///   <item><c>GetObserverScript</c>: el JavaScript que observa una web de chat cualquiera.</item>
///   <item><c>ProcessLevelRequestAsync</c>: antispam, filtros, búsqueda en GD y alta en la
///   cola.</item>
/// </list>
/// </summary>
public class GenericListener
{
    /// <summary>
    /// Parser genérico: extrae el usuario y el nivel de una línea de chat sin procesar, suponiendo
    /// el formato habitual "Usuario: Mensaje". Prueba los comandos configurados en orden y se queda
    /// con el primero que aparezca en el texto seguido de algo.
    ///
    /// <c>takeFullText</c> decide qué se toma como nombre de nivel cuando no hay comillas:
    /// <list type="bullet">
    ///   <item>true (Discord): toda la frase tras el comando. El mensaje llega limpio y
    ///   acotado.</item>
    ///   <item>false ("Otro"): solo la primera palabra. Aquí el texto sale del DOM de una web
    ///   desconocida, y detrás del nivel suele venir texto de otros elementos.</item>
    /// </list>
    /// Si el nivel va entre comillas, en los dos casos se toma lo que hay entre ellas.
    ///
    /// El comando tiene que ir como palabra: precedido del principio del texto, un espacio o
    /// ":", y seguido de un espacio o del final. Así un comando corto como "!r" no coincide con
    /// "!rank".
    ///
    /// El nombre es una suposición: el texto que va justo antes del comando, sin la hora. Con ":"
    /// delante del comando ("Juan Pérez: !add 128") se toman hasta tres palabras antes de los dos
    /// puntos; sin ":", la última palabra. Discord no usa este nombre: allí el autor se sabe con
    /// certeza y lo pone el propio listener.
    /// </summary>
    /// <param name="rawText">La línea tal como llega de la fuente.</param>
    /// <param name="commands">Los comandos de petición configurados en Ajustes (tres como
    /// mucho).</param>
    /// <param name="username">El nombre encontrado; si no hay, el genérico de
    /// <c>Default_Viewer</c>.</param>
    /// <param name="query">Lo que se buscará en GD: un ID o un nombre de nivel.</param>
    /// <param name="takeFullText">La frase entera (Discord) o solo la primera palabra
    /// ("Otro").</param>
    /// <returns>true si encontró un comando seguido de algo que buscar.</returns>
    public static bool TryParseGenericMessage(string rawText, ObservableCollection<RequestCommandModel> commands, out string username, out string query, bool takeFullText = false)
    {
        username = "";
        query = "";

        if (string.IsNullOrWhiteSpace(rawText)) return false;

        rawText = LimpiarTexto(rawText);

        // Comillas rectas y tipográficas (estas últimas las insertan los teclados de móvil).
        char[] quotes = { '"', '“', '”' };

        foreach (var cmd in commands)
        {
            if (string.IsNullOrWhiteSpace(cmd.CommandText)) continue;

            string commandStr = cmd.CommandText.Trim();
            var coincidencia = Regex.Match(rawText, @"(?<![^\s:])" + Regex.Escape(commandStr) + @"(?=\s|$)", RegexOptions.IgnoreCase);
            if (!coincidencia.Success) continue;
            int cmdIndex = coincidencia.Index;

            // === EXTRAER USUARIO ===
            username = ExtraerNombre(rawText.Substring(0, cmdIndex));
            if (string.IsNullOrWhiteSpace(username)) username = I18n.Tr("Default_Viewer");

            // === EXTRAER NIVEL ===
            string textAfterCmd = rawText.Substring(cmdIndex + commandStr.Length).Trim();

            if (!string.IsNullOrWhiteSpace(textAfterCmd))
            {
                if (quotes.Contains(textAfterCmd[0]))
                {
                    // Entre comillas el nombre puede llevar espacios: !add "dream flower". Sin
                    // comilla de cierre, se toma hasta el final de la línea.
                    string inner = textAfterCmd.Substring(1);
                    int closing = inner.IndexOfAny(quotes);
                    query = (closing != -1 ? inner.Substring(0, closing) : inner).Trim();
                }
                else if (takeFullText)
                {
                    query = textAfterCmd.Trim();
                }
                else
                {
                    var afterWords = textAfterCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (afterWords.Length > 0) query = afterWords[0].Trim();
                }
            }

            // Un comando sin nada detrás no cuenta: se siguen probando los demás.
            if (!string.IsNullOrWhiteSpace(query)) return true;
        }

        return false;
    }

    /// <summary>
    /// Supone el nombre de quien escribe a partir del texto que va antes del comando: se quita la
    /// hora ("12:30", "9:05 PM"); si el texto termina en ":", se toman hasta tres palabras antes
    /// de los dos puntos (los nombres con espacios son habituales en YouTube); si no, la última
    /// palabra. Se quitan "@" y "=".
    /// </summary>
    private static string ExtraerNombre(string textoAntes)
    {
        string texto = Regex.Replace(textoAntes, @"\b\d{1,2}:\d{2}(:\d{2})?(\s*[AaPp]\.?\s?[Mm]\.?)?(?=\s|$)", " ");
        texto = Regex.Replace(texto, @"\s+", " ").Trim();
        if (texto.Length == 0) return "";

        string[] palabras;
        if (texto.EndsWith(':'))
        {
            string antes = texto.Substring(0, texto.Length - 1);
            int dosPuntos = antes.LastIndexOf(':');
            if (dosPuntos >= 0) antes = antes.Substring(dosPuntos + 1);
            palabras = antes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            palabras = palabras.Skip(Math.Max(0, palabras.Length - 3)).ToArray();
        }
        else
        {
            palabras = new[] { texto.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last() };
        }

        return string.Join(" ", palabras).Replace(":", "").Replace("@", "").Replace("=", "").Trim();
    }

    /// <summary>
    /// Limpieza común a todas las fuentes: quita los caracteres invisibles que insertan algunas
    /// webs (espacios de ancho cero, guion suave, BOM, unión de palabras, marcas de dirección),
    /// que pegados a un ID harían que "128" dejara de leerse como número, y reduce cualquier
    /// secuencia de espacios (incluidos los duros), saltos de línea o tabuladores a un espacio.
    /// </summary>
    public static string LimpiarTexto(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return "";
        texto = Regex.Replace(texto, @"[\u200B-\u200D\uFEFF\u00AD\u2060\u200E\u200F]", "");
        return Regex.Replace(texto, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Qué es un mensaje para <see cref="TryParseCommand"/>.
    /// </summary>
    public enum TipoMensaje { Nada, Comando, IdDetectado }

    /// <summary>
    /// Decide si el texto de un mensaje (sin el autor) es una petición, con los comandos y la
    /// detección automática de IDs de los ajustes actuales.
    /// </summary>
    public static TipoMensaje TryParseCommand(string chatText, out string query)
    {
        var config = MainWindowViewModel.Instance.SettingsVM.Settings;
        return TryParseCommand(chatText, config.RequestCommands, config.AutoDetectIds, out query);
    }

    /// <summary>
    /// Decide si el texto de un mensaje (sin el autor) es una petición. Es el único parser de las
    /// fuentes que traen el autor aparte:
    /// <list type="bullet">
    ///   <item>Comando: el mensaje empieza por un comando seguido de un espacio. Todo lo que va
    ///   detrás es el nivel, sin una comilla inicial ni final, para aceptar !req "Nombre con
    ///   espacios". Un comando sin nada detrás no es una petición.</item>
    ///   <item>ID detectado: si el mensaje no empieza por un comando y la detección automática
    ///   está activa, un número suelto de 5 a 10 cifras.</item>
    /// </list>
    /// </summary>
    /// <param name="query">El nivel que se buscará; vacío si el mensaje no es una petición.</param>
    public static TipoMensaje TryParseCommand(string chatText, IEnumerable<RequestCommandModel> commands, bool autoDetectIds, out string query)
    {
        query = "";
        chatText = LimpiarTexto(chatText);
        if (chatText.Length == 0) return TipoMensaje.Nada;

        foreach (var cmd in commands)
        {
            if (string.IsNullOrWhiteSpace(cmd.CommandText)) continue;
            string commandStr = cmd.CommandText.Trim();

            if (!chatText.StartsWith(commandStr + " ", StringComparison.OrdinalIgnoreCase) &&
                !chatText.Equals(commandStr, StringComparison.OrdinalIgnoreCase))
                continue;

            // Es un comando: aunque no traiga nivel, ya no se busca un ID suelto en el mensaje.
            string resto = chatText.Substring(commandStr.Length).Trim();
            resto = resto.TrimStart('"', '“', '”').TrimEnd('"', '“', '”').Trim();
            query = resto;
            return query.Length > 0 ? TipoMensaje.Comando : TipoMensaje.Nada;
        }

        if (autoDetectIds)
        {
            var match = Regex.Match(chatText, @"\b\d{5,10}\b");
            if (match.Success)
            {
                query = match.Value;
                return TipoMensaje.IdDetectado;
            }
        }

        return TipoMensaje.Nada;
    }

    // ================================================================
    // JAVASCRIPT: el observador de la fuente "Otro"
    // ================================================================
    /// <summary>
    /// Observador de la fuente "Otro": un MutationObserver que Playwright inyecta en una web de
    /// chat cualquiera. Cada elemento que la web añade a la página se envía completo, como texto, a
    /// <c>window.sendToCSharp</c>, la función que expone <c>SheepChatListener</c> y que termina en
    /// <c>TryParseGenericMessage</c>.
    ///
    /// Como no conoce la web, no sabe qué es un mensaje: envía el <c>textContent</c> del elemento
    /// añadido con todo lo que contenga (nombre, insignias, hora y mensaje). Por eso el parser se
    /// queda solo con la primera palabra tras el comando. Solo observa elementos: un texto suelto
    /// añadido sin elemento contenedor no se detecta. Si la web pinta un mensaje en varios pasos,
    /// puede llegar más de una vez; el antispam de 3 segundos descarta las repeticiones.
    ///
    /// La explicación va aquí y no dentro del script porque un comentario dentro de la cadena
    /// cambiaría el JavaScript que se inyecta.
    /// </summary>
    /// <returns>El cuerpo del script. SheepChatListener lo envuelve para inyectarlo como init
    /// script, que se vuelve a aplicar si la web se recarga.</returns>
    public static string GetObserverScript()
    {
        return @"
            const observer = new MutationObserver((mutations) => {
                for (let mutation of mutations) {
                    for (let node of mutation.addedNodes) {
                        if (node.nodeType === 1 && node.textContent) {
                            let cleanText = node.textContent.replace(/\n/g, ' ').trim();
                            if (cleanText.length > 0) {
                                window.sendToCSharp(cleanText);
                            }
                        }
                    }
                }
            });
            observer.observe(document.body, { childList: true, subtree: true });
        ";
    }

    // ================================================================
    // ANTISPAM: REGISTRO DE PETICIONES Y BLOQUEO ENTRE HILOS
    // ================================================================
    /// <summary>
    /// Momento de la última petición de cada usuario (nombre en minúsculas), para el antispam de
    /// 3 segundos, en milisegundos de <see cref="Environment.TickCount64"/>: un reloj monótono, que
    /// no retrocede si el sistema cambia la hora (horario de invierno, ajuste manual). Su objetivo
    /// principal son los duplicados: el mismo mensaje puede llegar por dos fuentes a la vez (el
    /// chat de Twitch y el widget de SheepChat) o dos veces por el observador de "Otro".
    ///
    /// Solo se usa dentro de <c>CooldownLock</c>. Las entradas vencidas se borran cuando el
    /// diccionario crece, para que no acumule un nombre por cada espectador de la sesión.
    /// </summary>
    private static readonly Dictionary<string, long> UserCooldowns = new();

    private const long AntispamMs = 3000;

    /// <summary>
    /// Bloqueo del antispam: comprobar y registrar deben ser un solo paso. Sin él, dos hilos con el
    /// mismo mensaje (dos fuentes a la vez) podrían pasar la comprobación antes de que ninguno
    /// registrara la hora.
    /// </summary>
    private static readonly object CooldownLock = new object();

    // ================================================================
    // PROCESADOR CENTRAL
    // ================================================================
    /// <summary>
    /// Procesador central: decide si una petición entra en la cola y, si es así, la añade. Las
    /// comprobaciones baratas van primero y la búsqueda en GD (lenta y con reintentos) lo más
    /// tarde posible: antispam, peticiones activadas, límite global, solo IDs, límite por usuario,
    /// búsqueda en GD, repetidos, lista negra, rate, dificultad y alta en la cola.
    ///
    /// Casi todos los rechazos dejan una línea en la consola y, si el ajuste de avisos
    /// correspondiente está activo, una notificación. No lanza excepciones: cualquier error
    /// inesperado se escribe en la consola.
    /// </summary>
    /// <param name="query">Lo que pidió el espectador: un ID o un nombre de nivel.</param>
    /// <param name="requester">Quién lo pidió. Es la clave del antispam y del límite por
    /// usuario.</param>
    /// <param name="platform">La fuente configurada por la que llegó.</param>
    /// <param name="platformIndicator">De dónde indica venir el mensaje ("youtube", "tiktok"...),
    /// para elegir el icono. En la fuente "Otro" es la URL del chat. Vacío si no se
    /// sabe.</param>
    /// <param name="platformMessageId">Solo Discord: el ID del mensaje. Se guarda en el nivel para
    /// no añadirlo dos veces si lo aprueban varios moderadores y para quitarlo si se borra el
    /// mensaje.</param>
    public static async Task ProcessLevelRequestAsync(string query, string requester, ChatPlatform platform, string platformIndicator = "", string platformMessageId = "")
    {
        try
        {
            // === LIMPIEZA ===
            // La misma que hacen los parsers, por si alguna fuente llama aquí directamente.
            query = LimpiarTexto(query);

            if (string.IsNullOrWhiteSpace(query)) return;

            // === ANTISPAM (CON BLOQUEO ENTRE HILOS) ===
            // La hora se registra antes de los filtros: una petición rechazada también cuenta para
            // los 3 segundos.
            requester = string.IsNullOrWhiteSpace(requester) ? I18n.Tr("Default_Viewer") : requester;
            string userKey = requester.ToLowerInvariant();

            lock (CooldownLock)
            {
                long ahora = Environment.TickCount64;
                if (UserCooldowns.TryGetValue(userKey, out long ultima) && ahora - ultima < AntispamMs)
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_AntiSpam"), requester, platform));
                    return; // Mensaje repetido: se descarta.
                }

                // Limpieza de las entradas vencidas, que ya no bloquean a nadie.
                if (UserCooldowns.Count > 256)
                {
                    foreach (var vencida in UserCooldowns.Where(p => ahora - p.Value >= AntispamMs).Select(p => p.Key).ToList())
                        UserCooldowns.Remove(vencida);
                }

                // El momento se registra antes de liberar el bloqueo.
                UserCooldowns[userKey] = ahora;
            }
            // ======================================

            var vm = MainWindowViewModel.Instance;
            var config = vm.SettingsVM.Settings;

            // 0. ¿Las solicitudes están activadas?
            if (!config.IsRequestsEnabled)
            {
                // Se sale sin escribir en la consola, para no llenarla mientras las peticiones
                // están desactivadas.
                return;
            }

            // Una lectura de la cola para los filtros baratos, antes de la búsqueda en GD. Como la
            // búsqueda puede tardar, los límites y los repetidos se vuelven a comprobar al añadir
            // el nivel, dentro del cerrojo de la cola (paso 10).
            var queueState = await vm.QueueService.ReadQueueStateSafeAsync();

            // 0.5 Límite Global de la Cola
            // Cuenta solo la cola, no el nivel que se está jugando.
            if (SuperaLimiteGlobal(queueState, config))
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_FilterGlobalLimit"), requester));
                await InyectarNotificacionAsync(config.NotifyFilterBlocks, "Warning", "Notif_FilterGlobalLimit", requester, config.GlobalMaxRequests.ToString());
                return;
            }

            // 1. Solo IDs
            if (config.OnlyIdRequests && !Regex.IsMatch(query, @"^\d+$"))
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_FilterOnlyIds"), requester, query));
                // Se usa query porque todavía no hay datos del nivel.
                await InyectarNotificacionAsync(config.NotifyFilterBlocks, "Warning", "Notif_FilterOnlyIds", requester, query);
                return;
            }

            // 2. Límite por usuario
            if (SuperaLimiteUsuario(queueState, config, requester))
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_FilterUserLimit"), requester));
                await InyectarNotificacionAsync(config.NotifyFilterBlocks, "Warning", "Notif_FilterUserLimit", requester, query);
                return;
            }

            // 3. Búsqueda en Geometry Dash
            // Cada intento tiene 5 segundos; al vencer, la petición a GD se cancela (no se queda
            // corriendo en segundo plano). Se reintenta, con un segundo de pausa, cuando GD no
            // contesta a tiempo (hasta diez intentos) o responde con un error (hasta tres): ninguna
            // de las dos cosas es culpa del espectador. "El nivel no existe" es definitivo.
            const int MaxSearchAttempts = 10;
            const int MaxErroresServidor = 3;

            LevelRequestModel? levelModel = null;
            bool noExiste = false;
            int intento = 1;
            int erroresServidor = 0;

            // Fuera del bucle: el servicio no guarda estado (sus HttpClient son estáticos),
            // así que no tiene sentido construir uno nuevo en cada reintento.
            var gdService = new GeometryDashService();

            while (true)
            {
                // Solo se informa en los intentos 1, 2, 3, 5 y 10, para no llenar la consola con
                // diez líneas por petición durante el directo.
                if (intento <= 3 || intento == 5 || intento == MaxSearchAttempts)
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_SearchAttempt"), platform, requester, query, intento));
                }

                BusquedaNivel busqueda;
                using (var plazo = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    busqueda = await gdService.BuscarNivelAsync(query, plazo.Token).ConfigureAwait(false);
                }

                if (busqueda.Resultado == ResultadoBusqueda.Encontrado) { levelModel = busqueda.Nivel; break; }
                if (busqueda.Resultado == ResultadoBusqueda.NoExiste) { noExiste = true; break; }

                if (busqueda.Resultado == ResultadoBusqueda.ErrorServidor) erroresServidor++;
                if (intento >= MaxSearchAttempts || erroresServidor >= MaxErroresServidor) break;

                intento++;
                await Task.Delay(1000);
            }

            // 3.5 GD no respondió o respondió con errores. Es distinto de "el nivel no existe": el
            // espectador no hizo nada mal, son los servidores de GD los que fallaron.
            if (levelModel == null && !noExiste)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_SearchGaveUp"), intento, query));
                await InyectarNotificacionAsync(config.NotifySystemEvents, "Error", "Notif_GDSearchTimeout", requester, query);
                return;
            }

            if (levelModel == null)
            {
                await InyectarNotificacionAsync(config.NotifyFilterBlocks, "Warning", "Notif_GDLevelNotFound", requester, query);
                return;
            }

            // 4. Funciones internas

            // Traduce el DifficultyName que arma GeometryDashService ("MEDIUM DEMON", "INSANE"...,
            // siempre en inglés y en mayúsculas) al enum de los filtros. El orden de los if
            // importa: "insane demon" antes que "insane", y "harder" antes que "hard". Un "demon"
            // a secas es el Hard Demon, como en el juego.
            DifficultyRating ParseLevelDifficulty(string diffName)
            {
                if (string.IsNullOrEmpty(diffName)) return DifficultyRating.None;
                string lower = diffName.ToLower();
                if (lower.Contains("extreme")) return DifficultyRating.ExtremeDemon;
                if (lower.Contains("insane demon")) return DifficultyRating.InsaneDemon;
                if (lower.Contains("hard demon")) return DifficultyRating.HardDemon;
                if (lower.Contains("medium")) return DifficultyRating.MediumDemon;
                if (lower.Contains("easy demon")) return DifficultyRating.EasyDemon;
                if (lower.Contains("demon")) return DifficultyRating.HardDemon;
                if (lower.Contains("insane")) return DifficultyRating.Insane;
                if (lower.Contains("harder")) return DifficultyRating.Harder;
                if (lower.Contains("hard")) return DifficultyRating.Hard;
                if (lower.Contains("normal")) return DifficultyRating.Normal;
                if (lower.Contains("easy")) return DifficultyRating.Easy;
                if (lower.Contains("auto")) return DifficultyRating.Auto;
                return DifficultyRating.None;
            }

            // El escalón de cada dificultad, para "Hasta" y "A partir de". "Cualquier Demon"
            // (DemonTodos) es el escalón 8, entre Insane y Easy Demon: "A partir de: Cualquier
            // Demon" deja pasar los cinco demons. En "Hasta" se trata aparte (paso 8).
            int GetDifficultyWeight(DifficultyRating rating)
            {
                if (rating == DifficultyRating.None) return 1;
                if (rating == DifficultyRating.Auto) return 2;
                if (rating == DifficultyRating.Easy) return 3;
                if (rating == DifficultyRating.Normal) return 4;
                if (rating == DifficultyRating.Hard) return 5;
                if (rating == DifficultyRating.Harder) return 6;
                if (rating == DifficultyRating.Insane) return 7;
                if (rating == DifficultyRating.DemonTodos) return 8;
                if (rating == DifficultyRating.EasyDemon) return 9;
                if (rating == DifficultyRating.MediumDemon) return 10;
                if (rating == DifficultyRating.HardDemon) return 11;
                if (rating == DifficultyRating.InsaneDemon) return 12;
                if (rating == DifficultyRating.ExtremeDemon) return 13;
                return 0;
            }

            // El veredicto se guarda como clave (FilterReason_...): la notificación la traduce por
            // su cuenta, y la consola la traduce al escribirla.
            bool levelRejected = false;
            string rejectReasonKey = "";

            // 5. Filtro de niveles repetidos
            // Mira el nivel en juego y la cola, no el historial: un nivel ya jugado se puede volver
            // a pedir.
            if (EsRepetido(queueState, config, levelModel.LevelId))
            {
                levelRejected = true; rejectReasonKey = "FilterReason_Repeated";
            }

            // 6. Lista negra
            if (!levelRejected && config.IsBlacklistEnabled)
            {
                if (config.BlacklistedIds.Any(badId => !string.IsNullOrWhiteSpace(badId.LevelIdText) && badId.LevelIdText.Trim() == levelModel.LevelId))
                {
                    levelRejected = true; rejectReasonKey = "FilterReason_Blacklisted";
                }
            }

            // 7. Filtro de rate
            if (!levelRejected && config.OnlyRatedLevels && levelModel.Stars <= 0)
            {
                levelRejected = true; rejectReasonKey = "FilterReason_Unrated";
            }

            // 8. Filtro de dificultad
            // isDemon comprueba el rango del enum: "Cualquier Demon" coincide con los cinco demons.
            if (!levelRejected && config.TargetDifficulty != DifficultyRating.Todos)
            {
                DifficultyRating lvlDiff = ParseLevelDifficulty(levelModel.DifficultyName);
                bool isDemon = lvlDiff >= DifficultyRating.EasyDemon && lvlDiff <= DifficultyRating.ExtremeDemon;
                bool matchesExact = config.TargetDifficulty == DifficultyRating.DemonTodos ? isDemon : lvlDiff == config.TargetDifficulty;
                int lvlWeight = GetDifficultyWeight(lvlDiff);

                // "Hasta: Cualquier Demon" significa "hasta el demon más difícil", o sea, todo:
                // el tope es el escalón del Extreme Demon y no el 8.
                int targetWeight = config.DifficultyCondition == DifficultyFilterCondition.Hasta && config.TargetDifficulty == DifficultyRating.DemonTodos
                    ? GetDifficultyWeight(DifficultyRating.ExtremeDemon)
                    : GetDifficultyWeight(config.TargetDifficulty);

                switch (config.DifficultyCondition)
                {
                    case DifficultyFilterCondition.Solo: if (!matchesExact) { levelRejected = true; rejectReasonKey = "FilterReason_OnlyType"; } break;
                    case DifficultyFilterCondition.TodosExcepto: if (matchesExact) { levelRejected = true; rejectReasonKey = "FilterReason_BlockedDiff"; } break;
                    case DifficultyFilterCondition.Hasta: if (lvlWeight > targetWeight) { levelRejected = true; rejectReasonKey = "FilterReason_MaxDiff"; } break;
                    case DifficultyFilterCondition.APartirDe: if (lvlWeight < targetWeight) { levelRejected = true; rejectReasonKey = "FilterReason_MinDiff"; } break;
                }
            }

            // 9. Veredicto final
            if (levelRejected)
            {
                await RechazarAsync(rejectReasonKey);
                return;
            }

            // El nivel pasó todos los filtros: se completan los datos que GD no proporciona (quién
            // lo pidió y desde qué plataforma).
            levelModel.RequesterName = requester;
            string iconPath = ElegirIcono(platformIndicator, platform);
            levelModel.PlatformIconPath = iconPath;
            levelModel.DiscordMessageId = platformMessageId; // Ver el parámetro platformMessageId

            // 10. Alta en la cola. Los límites y los repetidos se vuelven a comprobar con la cola
            // de este momento, dentro de su cerrojo: mientras se buscaba en GD pudieron entrar
            // otros niveles (del mismo usuario, o el mismo nivel pedido por otro).
            string? rechazoFinal = null;
            var guardado = await vm.QueueService.ModificarAsync(state =>
            {
                if (SuperaLimiteGlobal(state, config)) { rechazoFinal = "Notif_FilterGlobalLimit"; return false; }
                if (SuperaLimiteUsuario(state, config, requester)) { rechazoFinal = "Notif_FilterUserLimit"; return false; }
                if (EsRepetido(state, config, levelModel.LevelId)) { rechazoFinal = "FilterReason_Repeated"; return false; }

                // En reposo, el nivel pasa directamente a ser el actual.
                if (state.CurrentLevel != null && state.CurrentLevel.Name == "WAITING...")
                    state.CurrentLevel = levelModel;
                else
                    state.Queue.Add(levelModel);
                return true;
            });

            if (rechazoFinal == "Notif_FilterGlobalLimit")
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_FilterGlobalLimit"), requester));
                await InyectarNotificacionAsync(config.NotifyFilterBlocks, "Warning", "Notif_FilterGlobalLimit", requester, config.GlobalMaxRequests.ToString());
                return;
            }
            if (rechazoFinal == "Notif_FilterUserLimit")
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_FilterUserLimit"), requester));
                await InyectarNotificacionAsync(config.NotifyFilterBlocks, "Warning", "Notif_FilterUserLimit", requester, query);
                return;
            }
            if (rechazoFinal != null)
            {
                await RechazarAsync(rechazoFinal);
                return;
            }

            // La escritura falló (ya se dijo en la consola): no se anuncia como añadido.
            if (guardado == null) return;

            Console.WriteLine(string.Format(I18n.Tr("Log_LevelAdded"), iconPath.Split('/').Last().Replace(".png", "")));
            await InyectarNotificacionAsync(config.NotifySuccessEvents, "Success", "Notif_LevelAdded", requester, levelModel.Name);

            // 11. Listas de la comunidad (GDDL y AREDL), después del alta: si van lentas o fallan,
            // no retrasan ni estropean la petición. Si llegan, se guardan en el nivel.
            _ = CompletarListasAsync(gdService, levelModel);

            // Escribe el rechazo en la consola y, si está activado, en el panel. La razón va como
            // clave (y, en los filtros de dificultad, "Clave|Enum_..."): I18n.Tr la traduce,
            // también el parámetro, así que el panel la puede volver a traducir al cambiar de
            // idioma.
            async Task RechazarAsync(string reasonKey)
            {
                string finalReasonKey = reasonKey;
                if (reasonKey == "FilterReason_OnlyType" || reasonKey == "FilterReason_MaxDiff" || reasonKey == "FilterReason_MinDiff")
                    finalReasonKey = $"{reasonKey}|Enum_DifficultyRating_{config.TargetDifficulty}";

                Console.WriteLine(string.Format(I18n.Tr("Log_LevelBlocked"), platform, requester, I18n.Tr(finalReasonKey)));

                // Nombre que se muestra en el aviso: el del nivel o, si GD no devolvió ninguno, lo
                // que se buscó.
                string displayLevel = !string.IsNullOrEmpty(levelModel?.Name) ? levelModel.Name : query;
                await InyectarNotificacionAsync(config.NotifyFilterBlocks, "Error", "Notif_LevelBlocked", requester, displayLevel, finalReasonKey);
            }
        }
        catch (Exception ex)
        {
            // Ningún error de este método debe interrumpir al listener que lo llamó.
            Console.WriteLine(string.Format(I18n.Tr("Log_ProcessorError"), ex.Message));
        }
    }

    /// <summary>
    /// Límite global: cuenta solo la cola, no el nivel que se está jugando.
    /// </summary>
    private static bool SuperaLimiteGlobal(QueueState state, SettingsModel config)
        => config.IsGlobalLimitEnabled && state.Queue.Count >= config.GlobalMaxRequests;

    /// <summary>
    /// Límite por usuario: cuenta el nivel en juego y los de la cola, sin distinguir mayúsculas.
    /// El nivel en juego no cuenta si es el marcador de espera ("WAITING...", ver
    /// QueueFileService.CreateDefaultState).
    /// </summary>
    private static bool SuperaLimiteUsuario(QueueState state, SettingsModel config, string requester)
    {
        if (!config.IsUserLimitEnabled) return false;

        int count = state.Queue.Count(l => string.Equals(l.RequesterName, requester, StringComparison.OrdinalIgnoreCase));
        if (state.CurrentLevel != null && state.CurrentLevel.Name != "WAITING..." &&
            string.Equals(state.CurrentLevel.RequesterName, requester, StringComparison.OrdinalIgnoreCase))
            count++;

        return count >= config.UserMaxRequests;
    }

    /// <summary>
    /// Repetido: el nivel ya está en juego o en la cola (salvo que se permitan los repetidos).
    /// </summary>
    private static bool EsRepetido(QueueState state, SettingsModel config, string levelId)
    {
        if (config.AllowRepeatedLevels) return false;
        return (state.CurrentLevel != null && state.CurrentLevel.LevelId == levelId)
               || state.Queue.Any(l => l.LevelId == levelId);
    }

    /// <summary>
    /// Icono de la plataforma según el indicador que envía cada fuente; si no se reconoce, el
    /// globo. Si el indicador es una URL (la fuente "Otro"), se mira solo el dominio, para que un
    /// canal con "yt" en el nombre o un "fbclid" en la URL no cambien el icono. Si es una palabra
    /// ("youtube", "twitch"...), se compara palabra por palabra.
    /// </summary>
    public static string ElegirIcono(string? platformIndicator, ChatPlatform platform)
    {
        const string Base = "avares://SheepGDRequest/Assets/GDassets/icons/";

        if (string.IsNullOrWhiteSpace(platformIndicator))
        {
            // Sin indicador, StreamElements se trata como Twitch.
            return Base + (platform == ChatPlatform.StreamElements ? "twitch.png" : "global.png");
        }

        string indicador = platformIndicator.Trim().ToLowerInvariant();

        if (Uri.TryCreate(indicador, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            string host = uri.Host;
            bool Dominio(string d) => host == d || host.EndsWith("." + d);

            if (Dominio("youtube.com") || Dominio("youtu.be")) return Base + "youtube.png";
            if (Dominio("twitch.tv")) return Base + "twitch.png";
            if (Dominio("facebook.com") || Dominio("fb.gg") || Dominio("fb.com")) return Base + "facebook.png";
            if (Dominio("kick.com")) return Base + "kick.png";
            if (Dominio("steampowered.com") || Dominio("steamcommunity.com")) return Base + "steam.png";
            if (Dominio("tiktok.com")) return Base + "tiktok.png";
            if (Dominio("discord.com") || Dominio("discord.gg")) return Base + "discord.png";
            return Base + "global.png";
        }

        var palabras = Regex.Split(indicador, @"[^a-z0-9]+").Where(p => p.Length > 0).ToHashSet();
        if (palabras.Contains("youtube") || palabras.Contains("yt")) return Base + "youtube.png";
        if (palabras.Contains("twitch")) return Base + "twitch.png";
        if (palabras.Contains("facebook") || palabras.Contains("fb")) return Base + "facebook.png";
        if (palabras.Contains("kick")) return Base + "kick.png";
        if (palabras.Contains("steam")) return Base + "steam.png";
        if (palabras.Contains("tiktok")) return Base + "tiktok.png";
        if (palabras.Contains("discord")) return Base + "discord.png";
        return Base + "global.png";
    }

    /// <summary>
    /// Pide GDDL y AREDL para un nivel ya añadido y, si llega algún dato, lo guarda en ese nivel
    /// (buscándolo por RequestId en el nivel actual, la cola y el historial). Tiene su propio plazo
    /// de 8 segundos. No lanza excepciones.
    /// </summary>
    private static async Task CompletarListasAsync(GeometryDashService gdService, LevelRequestModel nivel)
    {
        try
        {
            var copia = new LevelRequestModel { LevelId = nivel.LevelId, DifficultyName = nivel.DifficultyName };
            using var plazo = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            if (!await gdService.CompletarListasAsync(copia, plazo.Token)) return;

            await MainWindowViewModel.Instance.QueueService.ModificarAsync(state =>
            {
                var destino = new[] { state.CurrentLevel }.Concat(state.Queue).Concat(state.History)
                    .FirstOrDefault(l => l != null && l.RequestId == nivel.RequestId);
                if (destino == null) return false;

                destino.GddlTier = copia.GddlTier;
                destino.GddlTierColor = copia.GddlTierColor;
                destino.AredlRank = copia.AredlRank;
                return true;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_ProcessorError"), ex.Message));
        }
    }

    /// <summary>
    /// Envía una notificación solo si está activo el ajuste de avisos correspondiente (filtros,
    /// sistema o éxitos). Recibe claves y no textos: NotificationService traduce al crearla, y la
    /// lista de notificaciones vuelve a traducir al mostrarla.
    /// </summary>
    /// <param name="debeNotificar">El ajuste de avisos que corresponde a este caso.</param>
    /// <param name="type">"Success", "Warning" o "Error": determina el color del aviso.</param>
    /// <param name="transKey">La clave del mensaje (Notif_...).</param>
    /// <param name="user">Quién pidió el nivel. Rellena el {0}.</param>
    /// <param name="levelData">El nivel o lo que se buscó. Rellena el {1}.</param>
    /// <param name="reasonKey">La razón del rechazo, si la hay, como "Clave" o
    /// "Clave|parámetro". Rellena el {2}.</param>
    private static async Task InyectarNotificacionAsync(bool debeNotificar, string type, string transKey, string user, string levelData, string reasonKey = "")
    {
        if (debeNotificar)
        {
            await ViewModels.MainWindowViewModel.Instance.NotificationService.AddNotificationAsync(
                type, transKey, user, levelData, reasonKey);
        }
    }
}
