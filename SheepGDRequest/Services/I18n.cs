using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace SheepGDRequest.Services;

// =======================================================
// NOTIFICADOR: puente observable para Avalonia
// =======================================================
/// <summary>
/// Puente entre I18n (estático) y los bindings de Avalonia. Los textos de la interfaz se
/// enlazan a <see cref="LanguageTrigger"/> a través de TrExtension, así que basta con avisar de
/// que esta propiedad cambió para que todos se vuelvan a traducir.
/// </summary>
public class I18nNotifier : INotifyPropertyChanged
{
    public static I18nNotifier Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    // Propiedad sin lógica que solo sirve para que Avalonia la vigile.
    public string LanguageTrigger => I18n.CurrentLanguage;

    public void InvokeLanguageChanged()
    {
        // Avisa a Avalonia de que esta propiedad cambió.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageTrigger)));
    }
}

// =======================================================
// CLASE I18N
// =======================================================
/// <summary>
/// Traducciones de la aplicación: tres diccionarios (es, en y pt) y <see cref="Tr"/> para
/// leerlos. El español es la base: lo que falte en otro idioma sale en español, y lo que no esté
/// en ninguno sale como "[clave]". Los tres diccionarios deben tener las mismas claves; en una
/// compilación de depuración, cada clave que cae al español se avisa una vez en la consola.
/// </summary>
public static class I18n
{
    public static string CurrentLanguage { get; private set; } = "es";

    private static Dictionary<string, string> _currentTranslations;

    static I18n()
    {
        _currentTranslations = _es;
    }

    /// <summary>
    /// Cambia el idioma de toda la aplicación. La llaman SettingsService al cargar y el selector
    /// de idioma de Ajustes. Un código que no sea "en" ni "pt" se trata como español.
    /// </summary>
    public static void ChangeLanguage(string langCode)
    {
        CurrentLanguage = langCode.ToLower();
        _currentTranslations = CurrentLanguage switch
        {
            "en" => _en,
            "pt" => _pt,
            _ => _es
        };

        // Avisa a los textos de la interfaz enlazados con {i18n:Tr}.
        I18nNotifier.Instance.InvokeLanguageChanged();

        // Se reenvía el estado a los widgets; "state_updated" lleva el idioma nuevo.
        var vm = SheepGDRequest.ViewModels.MainWindowViewModel.Instance;
        if (vm != null && vm.SettingsVM.Settings.IsWidgetsServerEnabled)
        {
            SheepGDRequest.Services.WidgetServerService.Instance?.BroadcastQueueStateAsync(vm.CurrentLevel, vm.RequestQueue.ToList());
        }
    }

    /// <summary>
    /// Traduce una clave. Admite parámetros detrás de "|": "Clave|a|b" rellena {0} y {1}. Un
    /// parámetro que sea a su vez una clave de los diccionarios (por ejemplo,
    /// "Enum_DifficultyRating_HardDemon") también se traduce; el resto se inserta tal cual. Así,
    /// un texto guardado como "Clave|Enum_..." sale en el idioma activo cada vez que se traduce.
    /// </summary>
    public static string Tr(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";

        // 1. Separa la clave de los parámetros ("FilterReason_OnlyType|Easy Demon").
        string[] parts = key.Split('|');
        string mainKey = parts[0];

        // 2. Se busca en el idioma activo, después en español y, si no está, sale entre corchetes.
        string text = mainKey;
        if (_currentTranslations.TryGetValue(mainKey, out var translation))
        {
            text = translation;
        }
        else if (_es.TryGetValue(mainKey, out var esTranslation))
        {
            text = esTranslation;
            AvisarDeClaveSinTraducir(mainKey);
        }
        else
        {
            text = $"[{mainKey}]"; // La clave no existe en ningún diccionario
        }

        // 3. Si hay parámetros después de "|", rellenan los huecos {0}, {1}, etc.
        if (parts.Length > 1)
        {
            try
            {
                // Todos los textos después del primer "|", en orden.
                return string.Format(text, parts.Skip(1).Select(TraducirSiEsClave).ToArray());
            }
            catch
            {
                return text; // Si el formato falla, se devuelve el texto sin rellenar
            }
        }

        return text;
    }

    /// <summary>
    /// Solo en depuración: avisa en la consola, una vez por clave, de que una clave no está en el
    /// idioma activo y sale en español. El texto es fijo a propósito: es un aviso para quien
    /// desarrolla, no para el usuario, y no puede depender de las traducciones que comprueba.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void AvisarDeClaveSinTraducir(string clave)
    {
        lock (ClavesAvisadas)
        {
            if (ClavesAvisadas.Add($"{CurrentLanguage}:{clave}"))
                Console.WriteLine($"[I18n] Falta la clave {clave} en el idioma '{CurrentLanguage}'.");
        }
    }

    private static readonly HashSet<string> ClavesAvisadas = new();

    private static object TraducirSiEsClave(string parametro)
    {
        if (_currentTranslations.TryGetValue(parametro, out var traducido)) return traducido;
        if (_es.TryGetValue(parametro, out var enEspanol)) return enEspanol;
        return parametro;
    }

    // ==========================================
    // ESPAÑOL (BASE)
    // ==========================================
    private static readonly Dictionary<string, string> _es = new()
    {
        // 1. VALORES POR DEFECTO Y ESTADOS
        { "Default_UnknownName", "Desconocido" },
        { "Default_Unrated", "UNRATED" },
        { "Default_NA", "NA" },
        { "Default_Viewer", "Espectador" },
        { "Default_UnknownAuthor", "Desconocido" },
        { "Default_WaitingRequester", "Esperando peticiones..." },
        { "Default_System", "Sistema" },

        // 2. INTERFAZ PRINCIPAL Y MENÚS
        { "UI_MenuToggle", "Expandir / Contraer" },
        { "UI_MenuCollapse", "Contraer" },
        { "UI_MenuLevels", "Lista de Niveles" },
        { "UI_MenuSettings", "Configuración" },
        { "UI_MenuConsole", "Consola" },
        { "UI_ConsoleTitle", ">_ TERMINAL DE DEPURACIÓN" },
        { "UI_ConsoleBack", "VOLVER A NIVELES" },
        { "UI_By", "BY" },
        { "UI_ThemeDefault", "Predeterminado" },
        { "UI_ThemeByAuthor", "{0} - por {1}" },
        { "UI_SendBy", "Enviado por" },
        { "UI_CopySong", "Copiar Canción" },
        { "UI_CopyId", "Copiar ID" },
        { "UI_Previous", "Anterior" },
        { "UI_Next", "Siguiente" },
        { "UI_Random", "Aleatorio" },
        { "UI_Remove", "Eliminar" },
        { "UI_Priority", "Prioridad" },
        { "UI_LoginRequiredTitle", "Inicio de Sesión Requerido" },
        { "UI_LoginPrompt", "Acceso a {0}\n\nInicia sesión en la ventana del navegador que se abrirá\n\n(si es necesario).\n\nluego haz clic en LISTO." },
        { "UI_LoginReadyBtn", "LISTO" },
        { "UI_TitleClearWarning", "BORRAR" },
        { "UI_BtnClearQueue", "BORRAR TODOS LOS NIVELES ENVIADOS" },
        { "UI_BtnClearNotifications", "BORRAR NOTIFICACIONES" },
        { "UI_RequestsEnabled", "SOLICITUDES ACTIVADAS" },
        { "UI_RequestsDisabled", "SOLICITUDES PAUSADAS" },
        { "UI_DiscordTokenTitle", "Token del Bot de Discord:" },
        { "UI_DiscordTokenWatermark", "Pega el Token de tu bot aquí..." },
        { "UI_DiscordTutorialBtn", "Obtener token" },
        { "UI_SecTools", "HERRAMIENTAS" },
        { "UI_BtnOpenThemesFolder", "Abrir carpeta de temas para widgets" },
        { "UI_BtnOpenThemeEditor", "Abrir editor de temas para widgets" },
        { "UI_BtnInstallGeodeMod", "Instalar Mod de Geode" },
        { "UI_PickGdFolder", "Selecciona la carpeta donde tienes instalado Geometry Dash" },
        { "UI_ConfirmGdTitle", "Instalar Mod de Geode" },
        { "UI_ConfirmGdMsg", "Se instalará el mod en esta carpeta:" },
        { "UI_BtnYesInstallHere", "Sí, instalar aquí" },
        { "UI_BtnChooseAnother", "Elegir otra carpeta" },
        { "UI_BtnCancelDialog", "Cancelar" },
        { "UI_ModUpdateTitle", "Actualización del mod de Geode" },
        { "UI_ModUpdateMsg", "Hay una versión nueva del mod de SheepGD para Geometry Dash: {0}. Tienes instalada la {1}." },
        { "UI_ModUpdateHint", "Cierra Geometry Dash antes de actualizar; la versión nueva carga al volver a abrirlo." },
        { "UI_BtnUpdate", "Actualizar" },
        { "UI_BtnLater", "Más tarde" },


        // 3. PANTALLA DE AJUSTES
        { "UI_SettingsPanelTitle", "CONFIGURACIÓN" },
        { "UI_BtnClearCache", "BORRAR CACHÉ" },
        { "UI_TooltipClearCache", "Borra las sesiones guardadas" },
        { "UI_BtnSaveChanges", "GUARDAR CAMBIOS" },
        { "UI_SecNetworkCmds", "RED Y COMANDOS" },
        { "UI_ChatLinksTitle", "Enlaces de Chat (WebSockets o Popouts)" },
        { "UI_ChatUrlWatermark", "Pega la URL del chat aquí..." },
        { "UI_TooltipReconnect", "Reconectar este enlace" },
        { "UI_TooltipDelete", "Eliminar" },
        { "UI_BtnAddLink", "+ Agregar Enlace" },
        { "UI_CmdRequestsTitle", "Comandos de Request (Ej: !add)" },
        { "UI_BtnAddCmd", "+ Agregar Comando" },
        { "UI_SecQueueRules", "REGLAS DE LA FILA" },
        { "UI_LimitPerUser", "Límite por usuario" },
        { "UI_LimitRequests", "Límite de solicitudes" },
        { "UI_AutoSkip", "Auto-Skip de Niveles" },
        { "UI_OnlyRated", "Permitir SOLO niveles con Rate" },
        { "UI_AllowRepeated", "Permitir Niveles Repetidos" },
        { "UI_StrictId", "Modo estricto: Solo por ID" },
        { "UI_AutoDetectId", "Auto-detectar IDs" },
        { "UI_LimitByDiff", "Limitar por Dificultad:" },
        { "UI_EnableBlacklist", "Activar Lista Negra" },
        { "UI_BlacklistWatermark", "ID del nivel a bloquear..." },
        { "UI_BtnAddBlacklist", "+ Agregar a Lista Negra" },
        { "UI_SecInterfaceStream", "INTERFAZ Y STREAMING" },
        { "UI_AlwaysOnTop", "Mantener ventana Siempre Encima" },
        { "UI_ExportObs", "Exportar textos en vivo para OBS (.txt y widget)" },
        { "UI_ThumbOpacity", "Opacidad de la Miniatura (Fondo)" },
        { "UI_SecNotifications", "NOTIFICACIONES" },
        { "UI_NotifySuccess", "Notificar niveles añadidos con éxito" },
        { "UI_NotifyFilters", "Notificar bloqueos y filtros" },
        { "UI_NotifyAutoSkip", "Notificar eventos de Auto-Skip" },
        { "UI_NotifySystem", "Notificar eventos del sistema" },
        { "UI_TtsVolume", "Volumen TTS:" },
        { "UI_TtsReadSuccess", "TTS: Leer aceptados" },
        { "UI_TtsReadBlocks", "TTS: Leer bloqueos" },
        { "UI_TtsReadSystem", "TTS: Leer eventos del sistema" },
        { "UI_DiscordOnlyChecked", "Listar solo niveles reaccionados con ✅ ☑️ ✔️ ♥️ ❤️ 👍" },
        { "UI_DiscordOnlyCheckedTip", "El nivel entra a la fila cuando alguien reacciona al mensaje, no al enviarlo." },

        // WIDGETS (sección de Ajustes)
        { "UI_SecWidgets", "WIDGETS DE OBS (SERVIDOR LOCAL)" },
        { "UI_EnableWidgets", "Activar Servidor de Widgets" },
        { "UI_WidgetPort", "Puerto del Servidor:" },
        { "UI_WidgetNotification", "Widget de Notificaciones" },
        { "UI_WidgetPlaying", "Widget de Nivel Jugando" },
        { "UI_WidgetQueue", "Widget de Cola de Niveles" },
        { "UI_WidgetUnified", "Widget Unificado (Todo en uno)" },
        { "UI_Theme", "Tema:" },
        { "UI_Copy", "Copiar" },
        { "UI_BtnReLogin", "Sesión" },
        { "UI_TooltipReLogin", "Borra la sesión guardada de este enlace y vuelve a abrir el inicio de sesión" },

        // 4. TEXTOS PARA EXPORTACIÓN A OBS (TXT)
        { "Obs_WaitingLevels", "Esperando niveles..." },
        { "Obs_PlayingFormat", "Jugando: {0} ({1}) | ID: {2} | Pedido por: {3}" },
        { "Obs_NextFormat", "Siguiente: {0} (Pedido por {1})" },
        { "Obs_NextEmpty", "Siguiente: --" },
        { "Obs_QueueItemFormat", "{0}. {1} (Pedido por {2})" },
        { "Obs_QueueEmpty", "La lista está vacía." },

        // 5. ENUMS (TEXTOS DE LOS DESPLEGABLES). La clave es Enum_<Tipo>_<Valor>; la construye
        // EnumTranslatorConverter.
        { "Enum_ChatPlatform_SheepChat", "SheepChat (Dedicado)" },
        { "Enum_ChatPlatform_SocialStreamNinja", "SocialStream.Ninja (Dedicado)" },
        { "Enum_ChatPlatform_StreamElements", "StreamElements (Dedicado)" },
        { "Enum_ChatPlatform_TikTok", "TikTok Live (API)" },
        { "Enum_ChatPlatform_Discord", "Discord (Canal con bot Privado)" },
        { "Enum_ChatPlatform_Otro", "Otros (Universal)" },
        { "Enum_DifficultyFilterCondition_Solo", "Solo" },
        { "Enum_DifficultyFilterCondition_TodosExcepto", "Todas EXCEPTO" },
        { "Enum_DifficultyFilterCondition_Hasta", "Máximo hasta" },
        { "Enum_DifficultyFilterCondition_APartirDe", "Mínimo a partir de" },
        { "Enum_DifficultyRating_Todos", "Cualquier Dificultad" },
        { "Enum_DifficultyRating_None", "N/A (Sin Dificultad)" },
        { "Enum_DifficultyRating_Auto", "Auto" },
        { "Enum_DifficultyRating_Easy", "Easy" },
        { "Enum_DifficultyRating_Normal", "Normal" },
        { "Enum_DifficultyRating_Hard", "Hard" },
        { "Enum_DifficultyRating_Harder", "Harder" },
        { "Enum_DifficultyRating_Insane", "Insane" },
        { "Enum_DifficultyRating_DemonTodos", "Cualquier Demon" },
        { "Enum_DifficultyRating_EasyDemon", "Easy Demon" },
        { "Enum_DifficultyRating_MediumDemon", "Medium Demon" },
        { "Enum_DifficultyRating_HardDemon", "Hard Demon" },
        { "Enum_DifficultyRating_InsaneDemon", "Insane Demon" },
        { "Enum_DifficultyRating_ExtremeDemon", "Extreme Demon" },
        { "Enum_TimeUnit_Segundos", "Segundos" },
        { "Enum_TimeUnit_Minutos", "Minutos" },
        { "Enum_TimeUnit_Horas", "Horas" },

        // 6. RAZONES DE FILTRADO (se usan en notificaciones y en la consola)
        { "FilterReason_Repeated", "Nivel repetido." },
        { "FilterReason_Blacklisted", "ID en lista negra." },
        { "FilterReason_Unrated", "Nivel sin Rate." },
        { "FilterReason_OnlyType", "Solo se permite: {0}" },
        { "FilterReason_BlockedDiff", "Dificultad bloqueada." },
        { "FilterReason_MaxDiff", "Supera la dificultad máxima permitida ({0})" },
        { "FilterReason_MinDiff", "No alcanza la dificultad mínima permitida ({0})" },

        // 7. NOTIFICACIONES (panel de la izquierda, con emoji)
        { "Notif_FilterOnlyIds", "❗ {0} ignorado: {1} no es un ID." },
        { "Notif_FilterUserLimit", "❗ {0} alcanzó su límite de niveles en la fila." },
        { "Notif_LevelBlocked", "⛔ Nivel {1} de {0} bloqueado: {2}" },
        { "Notif_GDLevelNotFound", "❓ El nivel {1} de {0} no existe en GD." },
        { "Notif_GDSearchTimeout", "⌛ No se pudo buscar {1} de {0}: los servidores de GD no responden." },
        { "Notif_LevelAdded", "✅ Nivel {1} de {0} añadido con éxito." },
        { "Notif_AutoSkipNew", "⏭️ Nivel en pantalla expirado, saltando..." },
        { "Notif_AutoSkipExpired", "⏱️ El tiempo del nivel actual expiró." },
        { "Notif_AutoSkipNext", "⏭️ Pasando al siguiente nivel..." },
        { "Notif_QueueEmpty", "📭 La lista de peticiones está vacía." },
        { "Notif_ChatDisconnected", "🔌 Se perdió la conexión con {1}. Reintentando..." },
        { "Notif_LevelPrioritized", "⬆️ Nivel {1} de {0} subido a prioridad." },
        { "Notif_RequestsEnabled", "✅ Sistema: Se han ABIERTO las solicitudes de niveles." },
        { "Notif_RequestsDisabled", "⛔ Sistema: Se han PAUSADO las solicitudes de niveles." },
        { "Notif_FilterGlobalLimit", "⛔ {0} ignorado: La lista alcanzó el límite máximo de {1} niveles." },
        { "Log_FilterGlobalLimit", "Petición de {0} ignorada: Lista llena." },
        { "Notif_TikTokDisconnected", "🔌 Se perdió la conexión con TikTok ({1}). Reintentando..." },
        { "Notif_TikTokReconnected", "🔗 Conexión con TikTok ({1}) restablecida." },
        { "Notif_ModInstalled", "🧩 Mod instalado. Reinicia Geometry Dash para que cargue." },
        { "Notif_ModGeodeMissing", "⚠️ Esa carpeta no tiene Geode instalado. Instala Geode primero." },
        { "Notif_ModFileMissing", "⚠️ No se encontró el archivo .geode dentro del programa." },
        { "Notif_ModUpdated", "🧩 Mod actualizado a la {1}. Reinicia Geometry Dash para que cargue." },
        { "Notif_ModUpdateFailed", "⚠️ No se pudo descargar la actualización del mod. Inténtalo más tarde con «Instalar Mod de Geode»." },
        { "Notif_ModInstallFailed", "⚠️ No se pudo instalar el mod. Si Geometry Dash está abierto, ciérralo y vuelve a intentarlo." },
        { "Notif_ModAlreadyNewer", "🧩 Ya tienes instalada una versión más nueva del mod ({1}); no se ha cambiado." },
        { "Notif_DiscordWaitingCheck", "⏳ {0} pidió {1}. Esperando aprobación." },
        { "Notif_SettingsUnreadable", "⚠️ No se pudieron leer tus ajustes. Se guardaron aparte y se usan los de por defecto." },
        { "Notif_ChatReconnected", "🔗 Conexión con {1} restablecida." },
        { "Notif_WidgetServerError", "⚠️ Los widgets no funcionan: el puerto {1} no está disponible." },

        // 8. CONSOLA DE DEPURACIÓN (registros técnicos, sin emoji)
        { "Log_AntiSpam", "[Anti-Spam] La petición de {0} en {1} estaba duplicada." },
        { "Log_SearchAttempt", "\n=> [{0}] REQUEST DE {1} BÚSQUEDA: '{2}' (Intento {3})" },
        { "Log_SearchGaveUp", "[Búsqueda] Se agotaron los {0} intentos para '{1}'. Los servidores de GD no responden." },
        { "Log_ProcessorError", "[Error Crítico en Procesador] Falló al procesar nivel: {0}" },
        { "Log_GDServerError", "[Error GD] Respuesta vacía o error del servidor para el nivel." },
        { "Log_GDDLFailed", "[Aviso] No se pudo obtener GDDL para {0}" },
        { "Log_GDDLHttpError", "[GDDL HTTP ERROR]: El servidor respondió con código {0} para el nivel {1}" },
        { "Log_AREDLCloudflare", "[AREDL AVISO]: Cloudflare interceptó la petición directa y devolvió HTML." },
        { "Log_AREDLHttpError", "[AREDL HTTP ERROR]: El servidor respondió con código {0}" },
        { "Log_AREDLCritical", "[AREDL CRÍTICO]: {0}" },
        { "Log_GDInternalError", "[Error Interno GD]: {0}" },
        { "Log_ObsExportError", "[Error OBS Export] No se pudieron guardar los textos: {0}" },
        { "Log_SettingsCreated", "[Sistema] Archivo de configuración creado con éxito." },
        { "Log_SettingsLoadError", "[Error al cargar ajustes]: {0}" },
        { "Log_SettingsSaveError", "[Error Crítico al guardar ajustes]: {0}" },
        { "Log_InterceptorStarting", "\n=== INICIANDO INTERCEPTOR MULTI-FUENTE ===" },
        { "Log_BotPaused", "[Esperando] El bot está en pausa. Agrega un enlace y dale a Guardar Cambios..." },
        { "Log_RestartingConnection", "[Sistema] Reiniciando conexión con {0}..." },
        { "Log_StartingNewConnection", "[Sistema] Iniciando nueva conexión con {0}..." },
        { "Log_LoginClosed", "[Sistema] Ventana de Login cerrada. Guardando sesión..." },
        { "Log_FirstTimeLogin", "[Sistema] Primer acceso detectado para {0}. Abriendo ventana de inicio de sesión..." },
        { "Log_RestartingHidden", "[Sistema] Reiniciando {0} con la sesión guardada..." },
        { "Log_UniversalCmdDetected", "[Universal] Comando detectado en Otro: Usuario: {0} | Query: {1}" },
        { "Log_UniversalError", "[Error en Universal] {0}" },
        { "Log_DedicatedMsg", "[Dedicado] Mensaje de {0} en {1}: {2}" },
        { "Log_DedicatedCmdValidated", "[Dedicado] Comando validado, buscando frase: '{0}'" },
        { "Log_DedicatedAutoId", "[Dedicado] Auto-ID detectado: {0}" },
        { "Log_DedicatedError", "[Error Puente Dedicado] {0}" },
        { "Log_ConnectedInjectingEye", "[Sistema] Conectado a {0}. Inyectando interceptor Dedicado..." },
        { "Log_CleaningHistory", "[Sistema] Ignorando el historial de {0}... (esperando {1} s)" },
        { "Log_InterceptorActive", "[Sistema] ¡Interceptor activo para {0}!" },
        { "Log_ConnectedInjectingNinja", "[Sistema] Conectado a {0}. Inyectando interceptor..." },
        { "Log_WatchdogBlank", "[watcher] Otro se quedó en blanco o no pudo renderizar. ¡Recargando!" },
        { "Log_WatchdogRestored", "[watcher] Otro restaurado e interceptor reinyectado." },
        { "Log_CriticalWidgetError", "[Error] Fallo en {0}: {1}" },
        { "Log_SECmdDetected", "[StreamElements] Comando de {0}: {1}" },
        { "Log_SEAutoIdDetected", "[StreamElements] Auto-ID de {0}: {1}" },
        { "Log_SETaskError", "[Error Tarea StreamElements] {0}" },
        { "Log_TikTokCmd", "[TikTok API] Comando de {0}: {1}" },
        { "Log_TikTokAutoId", "[TikTok API] Auto-ID de {0}: {1}" },
        { "Log_TikTokTaskError", "[Error Tarea TikTok API] {0}" },
        { "Log_TikTokConnecting", "[Sistema] Conectando a la API de TikTok para el usuario: @{0}..." },
        { "Log_TikTokConnError", "[Error API TikTok] No se pudo conectar: {0}" },
        { "Log_TikTokDisconnected", "[TikTok] Conexión con {0} caída. Reintentando en {1} s." },
        { "Log_TikTokReconnected", "[TikTok] Conexión con {0} restablecida." },
        { "Log_TikTokLoopStopped", "[TikTok] Vigilancia de {0} detenida." },
        { "Log_TikTokConnected", "[TikTok] Conectado al directo de {0}. Escuchando el chat." },
        { "Log_SettingsSaved", "[Sistema] Configuraciones guardadas correctamente." },
        { "Log_ClosingConnectionsForCache", "[Sistema] Cerrando conexiones activas para limpiar la caché..." },
        { "Log_CacheClearedSuccess", "[Sistema] ¡Caché borrada con éxito! (Deberás iniciar sesión de nuevo)." },
        { "Log_CacheAlreadyClean", "[Sistema] La caché ya estaba limpia." },
        { "Log_CacheClearError", "[Error] No se pudo limpiar la caché: {0}" },
        { "Log_QueueCleared", "[Sistema] La lista de niveles ha sido limpiada." },
        { "Log_NotificationsCleared", "[Sistema] Historial de notificaciones borrado con éxito." },
        { "Log_SEJsonError", "[SE JSON Error]: {0}" },
        { "Log_LevelAdded", "[Sistema] Nivel añadido con éxito (Plataforma: {0})" },
        { "Log_ChatLinkRemoved", "[Enlaces] Fuente {0} eliminada y desconectada." },
        { "Log_PlaywrightPreparing", "[Navegador] Preparando el navegador interno. Si es la primera vez descargará unos 150 MB y puede tardar varios minutos." },
        { "Log_PlaywrightReady", "[Navegador] Navegador interno listo." },
        { "Log_PlaywrightFailed", "[Navegador] No se pudo preparar el navegador (código {0}). Revisa tu conexión a internet. Las fuentes que usan navegador no conectarán; TikTok y Discord sí." },
        { "Log_PlaywrightError", "[Navegador] Error preparando el navegador: {0}" },
        { "Log_UserAgentFallback", "[Navegador] No se pudo leer la versión de Chromium ({0}); se usa un identificador genérico." },
        { "Log_QueueFileCreated", "[Cola] queue_data.json creado en {0}" },
        { "Log_ThemeReadError", "[Temas] Error al leer {0}: {1}" },
        { "Log_OpenPathError", "[Abrir] No se pudo abrir {0}: {1}" },
        { "Log_EditorNotFound", "[Editor] No se encontró el ejecutable de SheepGDEditor. Se buscó en: {0}" },
        { "Log_ModInstallError", "[Mod] No se pudo instalar: {0}" },
        { "Log_ModReleaseUnavailable", "[Mod] No se pudo descargar la última versión de GitHub; se usa la copia incluida en el programa ({0})." },
        { "Log_ModSkippedOlder", "[Mod] No se instala la copia incluida ({0}) porque la instalada es más nueva ({1})." },
        { "Log_ModInstalledFromGitHub", "[Mod] Instalada la versión {0} de GitHub en {1}" },
        { "Log_ModInstalledBundled", "[Mod] Instalada la versión {0} incluida en el programa en {1}" },
        { "Log_ModUpdateAvailable", "[Mod] Hay una versión nueva del mod: {0} (instalada: {1})." },
        { "Log_ModUpdateCheckFailed", "[Mod] No se pudo comprobar en GitHub si hay una versión nueva del mod." },
        { "Log_DiscordWaitingCheck", "[Discord] {0} pidió {1}. Esperando aprobación." },
        { "Log_DiscordApproved", "[Discord] aprobado: {1} de {0} entra a la fila." },
        { "Log_DiscordAlreadyQueued", "[Discord] Ese mensaje ya tenía un nivel en la fila; se ignora la reacción." },
        { "Log_QueueUnreadable", "[Cola] No se pudo leer queue_data.json. Se guardó una copia en {0} y se sigue con la cola vacía." },
        { "Log_QueueWriteError", "[Cola] No se pudo guardar queue_data.json: {0}" },
        { "Log_AutoSkipStampError", "[AutoSkip] No se pudo guardar el sello de tiempo: {0}" },
        { "Log_DiscordMessageDeleted", "[Discord] Mensaje borrado: el nivel {0} de {1} se quitó de la fila." },
        { "Log_SettingsMovedAside", "[Sistema] settings.json no se pudo leer y se ha guardado aparte como {0}. Se usan los valores por defecto." },
        { "Log_ThumbnailUnavailable", "[Fondo] No se pudo descargar la miniatura del nivel ({0}). Se usa el fondo por defecto." },
        { "Log_PlaceholderMissing", "[Fondo] No se encontró placeholder_bg.png en los recursos del programa: {0}" },
        { "Log_GDHttpError", "[Error GD] El servidor respondió con el código {0}." },
        { "Log_DiscordInvalidLink", "[Discord] Enlace de canal no válido. Usa el enlace de un canal: https://discord.com/channels/servidor/canal" },
        { "Log_DiscordConnected", "[Discord] Conectado. El bot está escuchando el canal." },
        { "Log_DiscordConnecting", "[Discord] Iniciando conexión con los servidores..." },
        { "Log_DiscordCriticalError", "[Error Discord] {0}" },
        { "Log_DiscordLibrary", "[Discord] {0}" },
        { "Log_DiscordCloseError", "[Discord] Error al cerrar: {0}" },
        { "Log_DiscordDisconnected", "[Discord] Bot desconectado." },
        { "Log_DiscordTokenEmpty", "[Discord] El token del bot está vacío. Pégalo en la sección de Discord de Ajustes." },
        { "Log_InterceptorStartError", "[Error] No se pudieron conectar los enlaces de chat: {0}" },
        { "Log_ChatReconnecting", "[{0}] Conexión perdida. Reintentando en {1} s." },
        { "Log_ChatReconnected", "[{0}] Conexión restablecida." },
        { "Log_BrowserClosed", "[{0}] El navegador interno se cerró inesperadamente." },
        { "Log_WatchdogError", "[watcher] {0}: {1}" },
        { "Log_ProfileMigrated", "[Navegador] La sesión guardada de {0} se adaptó al nuevo formato de carpeta." },
        { "Log_ProfileMigrationError", "[Navegador] No se pudo adaptar la sesión guardada de {0}: {1}" },
        { "Log_ReLoginRequested", "[{0}] Sesión guardada borrada. Se abrirá la ventana de inicio de sesión." },
        { "Log_ReLoginError", "[{0}] No se pudo borrar la sesión guardada: {1}" },
        { "Log_PageError", "[{0}] Error de JavaScript en la página: {1}" },
        { "Log_SEHistory", "[StreamElements] Historial detectado ({0} mensajes): se ignora." },
        { "Log_SEReadyHistory", "[StreamElements] Historial descartado. Listo para recibir peticiones." },
        { "Log_SEReadyTimeout", "[StreamElements] Sin historial que descartar. Listo para recibir peticiones." },
        { "Log_TtsInitError", "[Voz] No se pudo iniciar el audio (BASS): {0}. Los avisos no se leerán en voz alta." },
        { "Log_TtsHttpError", "[Voz] El servicio de voz de Google respondió con el código {0}." },
        { "Log_TtsStreamError", "[Voz] No se pudo abrir el audio recibido (BASS): {0}" },
        { "Log_TtsError", "[Voz] Error: {0}" },
        { "Log_NotifSaveError", "[Notificaciones] No se pudo guardar notifications.json: {0}" },
        { "Log_NotifObsError", "[Notificaciones] No se pudo escribir obs_notifications.txt: {0}" },
        { "Log_WidgetServerStarted", "[Widgets] Servidor iniciado en {0}" },
        { "Log_WidgetServerStopped", "[Widgets] Servidor detenido." },
        { "Log_WidgetServerError", "[Widgets] No se pudo abrir el servidor en el puerto {0}: {1}. ¿Lo está usando otro programa? Cambia el puerto en Ajustes." },
        { "Log_WidgetRequestError", "[Widgets] Mensaje de un widget no válido: {0}" },
        { "Log_AutoSkipExpired", "[AutoSkip] Se acabó el tiempo de {0}." },
        { "Log_AutoSkipNewLevelDetected", "[AutoSkip] Llegó un nivel nuevo: se salta el nivel caducado." },
        { "Log_AutoSkipNext", "[AutoSkip] Pasando al siguiente nivel de la cola." },
        { "Log_FilterOnlyIds", "[Filtro] Petición de {0} ignorada: '{1}' no es un ID (solo se aceptan IDs)." },
        { "Log_FilterUserLimit", "[Filtro] Petición de {0} ignorada: alcanzó su límite de niveles en la fila." },
        { "Log_GDLevelNotFound", "[GD] El nivel no existe." },
        { "Log_LevelBlocked", "[{0}] Nivel de {1} rechazado: {2}" },
    };

    // ==========================================
    // INGLÉS
    // ==========================================
    private static readonly Dictionary<string, string> _en = new()
    {
        // 1. VALORES POR DEFECTO Y ESTADOS
        { "Default_UnknownName", "Unknown" },
        { "Default_Unrated", "UNRATED" },
        { "Default_NA", "NA" },
        { "Default_Viewer", "Viewer" },
        { "Default_UnknownAuthor", "Unknown" },
        { "Default_WaitingRequester", "Waiting for requests..." },
        { "Default_System", "System" },

        // 2. INTERFAZ PRINCIPAL Y MENÚS
        { "UI_MenuToggle", "Expand / Collapse" },
        { "UI_MenuCollapse", "Collapse" },
        { "UI_MenuLevels", "Level List" },
        { "UI_MenuSettings", "Settings" },
        { "UI_MenuConsole", "Debug Console" },
        { "UI_ConsoleTitle", ">_ DEBUG TERMINAL" },
        { "UI_ConsoleBack", "BACK TO LEVELS" },
        { "UI_By", "BY" },
        { "UI_ThemeDefault", "Default" },
        { "UI_ThemeByAuthor", "{0} - by {1}" },
        { "UI_SendBy", "Sent by" },
        { "UI_CopySong", "Copy Song" },
        { "UI_CopyId", "Copy ID" },
        { "UI_Previous", "Previous" },
        { "UI_Next", "Next" },
        { "UI_Random", "Random" },
        { "UI_Remove", "Remove" },
        { "UI_Priority", "Priority" },
        { "UI_LoginRequiredTitle", "Login Required" },
        { "UI_LoginPrompt", "Accessing {0}\n\nPlease log in using the browser window that will open\n\n(if needed).\n\nThen click DONE." },
        { "UI_LoginReadyBtn", "DONE" },
        { "UI_TitleClearWarning", "DELETE" },
        { "UI_BtnClearQueue", "DELETE ALL SENT LEVELS" },
        { "UI_BtnClearNotifications", "CLEAR NOTIFICATIONS" },
        { "UI_RequestsEnabled", "REQUESTS ENABLED" },
        { "UI_RequestsDisabled", "REQUESTS PAUSED" },

        // 3. PANTALLA DE AJUSTES
        { "UI_SettingsPanelTitle", "SETTINGS" },
        { "UI_BtnClearCache", "CLEAR CACHE" },
        { "UI_TooltipClearCache", "Clears saved chat sessions" },
        { "UI_BtnSaveChanges", "SAVE CHANGES" },
        { "UI_SecNetworkCmds", "NETWORK & COMMANDS" },
        { "UI_ChatLinksTitle", "Chat Links (WebSockets or Popouts)" },
        { "UI_ChatUrlWatermark", "Paste the chat URL here..." },
        { "UI_TooltipReconnect", "Reconnect this link" },
        { "UI_TooltipDelete", "Delete" },
        { "UI_BtnAddLink", "+ Add Link" },
        { "UI_CmdRequestsTitle", "Request Commands (E.g.: !add)" },
        { "UI_BtnAddCmd", "+ Add Command" },
        { "UI_SecQueueRules", "QUEUE RULES" },
        { "UI_LimitPerUser", "Limit per user" },
        { "UI_LimitRequests", "Requests limit" },
        { "UI_AutoSkip", "Level Auto-Skip" },
        { "UI_OnlyRated", "Allow ONLY Rated levels" },
        { "UI_AllowRepeated", "Allow Repeated Levels" },
        { "UI_StrictId", "Strict mode: ID only" },
        { "UI_AutoDetectId", "Auto-detect IDs" },
        { "UI_LimitByDiff", "Limit by Difficulty:" },
        { "UI_EnableBlacklist", "Enable Blacklist" },
        { "UI_BlacklistWatermark", "Level ID to block..." },
        { "UI_BtnAddBlacklist", "+ Add to Blacklist" },
        { "UI_SecInterfaceStream", "INTERFACE & STREAMING" },
        { "UI_AlwaysOnTop", "Keep window Always on Top" },
        { "UI_ExportObs", "Export live texts for OBS (.txt and widget)" },
        { "UI_ThumbOpacity", "Thumbnail Opacity (Background)" },
        { "UI_SecNotifications", "NOTIFICATIONS" },
        { "UI_NotifySuccess", "Notify successfully added levels" },
        { "UI_NotifyFilters", "Notify blocks and filters" },
        { "UI_NotifyAutoSkip", "Notify Auto-Skip events" },
        { "UI_NotifySystem", "Notify system events" },
        { "UI_TtsVolume", "TTS Volume:" },
        { "UI_TtsReadSuccess", "TTS: Read accepted" },
        { "UI_TtsReadBlocks", "TTS: Read blocked" },
        { "UI_TtsReadSystem", "TTS: Read system events" },
        { "UI_DiscordTokenTitle", "Discord Bot Token:" },
        { "UI_DiscordTokenWatermark", "Paste your bot Token here..." },
        { "UI_DiscordTutorialBtn", "Get token" },
        { "UI_SecTools", "TOOLS" },
        { "UI_BtnOpenThemesFolder", "Open widget themes folder" },
        { "UI_BtnOpenThemeEditor", "Open widget theme editor" },
        { "UI_BtnInstallGeodeMod", "Install Geode Mod" },
        { "UI_PickGdFolder", "Select your Geometry Dash installation folder" },
        { "UI_ConfirmGdTitle", "Install Geode Mod" },
        { "UI_ConfirmGdMsg", "The mod will be installed in this folder:" },
        { "UI_BtnYesInstallHere", "Yes, install here" },
        { "UI_BtnChooseAnother", "Choose another folder" },
        { "UI_BtnCancelDialog", "Cancel" },
        { "UI_ModUpdateTitle", "Geode mod update" },
        { "UI_ModUpdateMsg", "A new version of the SheepGD mod for Geometry Dash is available: {0}. You have {1} installed." },
        { "UI_ModUpdateHint", "Close Geometry Dash before updating; the new version loads when you open it again." },
        { "UI_BtnUpdate", "Update" },
        { "UI_BtnLater", "Later" },
        { "UI_DiscordOnlyChecked", "Only list levels reacted with ✅ ☑️ ✔️ ♥️ ❤️ 👍" },
        { "UI_DiscordOnlyCheckedTip", "The level joins the queue when someone reacts to the message, not when it is sent." },

        // WIDGETS (sección de Ajustes)
        { "UI_SecWidgets", "OBS WIDGETS (LOCAL SERVER)" },
        { "UI_EnableWidgets", "Enable Widget Server" },
        { "UI_WidgetPort", "Server Port:" },
        { "UI_WidgetNotification", "Notifications Widget" },
        { "UI_WidgetPlaying", "Now Playing Widget" },
        { "UI_WidgetQueue", "Level Queue Widget" },
        { "UI_WidgetUnified", "Unified Widget (All in one)" },
        { "UI_Theme", "Theme:" },
        { "UI_Copy", "Copy" },
        { "UI_BtnReLogin", "Login" },
        { "UI_TooltipReLogin", "Deletes this link's saved session and opens the login again" },


        // 4. TEXTOS PARA EXPORTACIÓN A OBS (TXT)
        { "Obs_WaitingLevels", "Waiting for levels..." },
        { "Obs_PlayingFormat", "Playing: {0} ({1}) | ID: {2} | Sent by: {3}" },
        { "Obs_NextFormat", "Next: {0} (Sent by {1})" },
        { "Obs_NextEmpty", "Next: --" },
        { "Obs_QueueItemFormat", "{0}. {1} (Sent by {2})" },
        { "Obs_QueueEmpty", "The queue is empty." },

        // 5. ENUMS (TEXTOS DE LOS DESPLEGABLES)
        { "Enum_ChatPlatform_SheepChat", "SheepChat (Dedicated)" },
        { "Enum_ChatPlatform_SocialStreamNinja", "SocialStream.Ninja (Dedicated)" },
        { "Enum_ChatPlatform_StreamElements", "StreamElements (Dedicated)" },
        { "Enum_ChatPlatform_TikTok", "TikTok Live (API)" },
        { "Enum_ChatPlatform_Discord", "Discord (Channel with private Bot)" },
        { "Enum_ChatPlatform_Otro", "Others (Universal)" },
        { "Enum_DifficultyFilterCondition_Solo", "Only" },
        { "Enum_DifficultyFilterCondition_TodosExcepto", "All EXCEPT" },
        { "Enum_DifficultyFilterCondition_Hasta", "Maximum up to" },
        { "Enum_DifficultyFilterCondition_APartirDe", "Minimum starting from" },
        { "Enum_DifficultyRating_Todos", "Any Difficulty" },
        { "Enum_DifficultyRating_None", "N/A (No Difficulty)" },
        { "Enum_DifficultyRating_Auto", "Auto" },
        { "Enum_DifficultyRating_Easy", "Easy" },
        { "Enum_DifficultyRating_Normal", "Normal" },
        { "Enum_DifficultyRating_Hard", "Hard" },
        { "Enum_DifficultyRating_Harder", "Harder" },
        { "Enum_DifficultyRating_Insane", "Insane" },
        { "Enum_DifficultyRating_DemonTodos", "Any Demon" },
        { "Enum_DifficultyRating_EasyDemon", "Easy Demon" },
        { "Enum_DifficultyRating_MediumDemon", "Medium Demon" },
        { "Enum_DifficultyRating_HardDemon", "Hard Demon" },
        { "Enum_DifficultyRating_InsaneDemon", "Insane Demon" },
        { "Enum_DifficultyRating_ExtremeDemon", "Extreme Demon" },
        { "Enum_TimeUnit_Segundos", "Seconds" },
        { "Enum_TimeUnit_Minutos", "Minutes" },
        { "Enum_TimeUnit_Horas", "Hours" },

        // 6. RAZONES DE FILTRADO
        { "FilterReason_Repeated", "Repeated level." },
        { "FilterReason_Blacklisted", "ID is blacklisted." },
        { "FilterReason_Unrated", "Level has no Rate/Stars." },
        { "FilterReason_OnlyType", "Only allowed: {0}" },
        { "FilterReason_BlockedDiff", "Difficulty blocked." },
        { "FilterReason_MaxDiff", "Exceeds maximum ({0})" },
        { "FilterReason_MinDiff", "Below minimum ({0})" },

        // 7. NOTIFICACIONES
        { "Notif_FilterOnlyIds", "❗ {0} ignored: {1} is not an ID." },
        { "Notif_FilterUserLimit", "❗ {0} reached their queue limit." },
        { "Notif_LevelBlocked", "⛔ Level {1} by {0} blocked: {2}" },
        { "Notif_GDLevelNotFound", "❓ Level {1} by {0} does not exist in GD." },
        { "Notif_GDSearchTimeout", "⌛ Could not look up {1} by {0}: GD servers are not responding." },
        { "Notif_LevelAdded", "✅ Level {1} by {0} added successfully." },
        { "Notif_AutoSkipNew", "⏭️ Level on screen expired, skipping..." },
        { "Notif_AutoSkipExpired", "⏱️ The current level's time has expired." },
        { "Notif_AutoSkipNext", "⏭️ Skipping to the next level..." },
        { "Notif_QueueEmpty", "📭 The request queue is empty." },
        { "Notif_ChatDisconnected", "🔌 Lost connection to {1}. Retrying..." },
        { "Notif_LevelPrioritized", "⬆️ Level {1} by {0} moved to priority." },
        { "Notif_RequestsEnabled", "✅ System: Level requests are now OPEN." },
        { "Notif_RequestsDisabled", "⛔ System: Level requests have been PAUSED." },
        { "Notif_FilterGlobalLimit", "⛔ {0} ignored: Queue reached max limit of {1} levels." },
        { "Log_FilterGlobalLimit", "[Filter] Request by {0} ignored: Queue is full." },
        { "Notif_TikTokDisconnected", "🔌 Lost connection to TikTok ({1}). Retrying..." },
        { "Notif_TikTokReconnected", "🔗 Connection to TikTok ({1}) restored." },
        { "Notif_ModInstalled", "🧩 Mod installed. Restart Geometry Dash to load it." },
        { "Notif_ModGeodeMissing", "⚠️ That folder has no Geode installed. Install Geode first." },
        { "Notif_ModFileMissing", "⚠️ The .geode file was not found inside the program." },
        { "Notif_ModUpdated", "🧩 Mod updated to {1}. Restart Geometry Dash to load it." },
        { "Notif_ModUpdateFailed", "⚠️ The mod update could not be downloaded. Try again later with \"Install Geode Mod\"." },
        { "Notif_ModInstallFailed", "⚠️ The mod could not be installed. If Geometry Dash is open, close it and try again." },
        { "Notif_ModAlreadyNewer", "🧩 You already have a newer version of the mod installed ({1}); nothing was changed." },
        { "Notif_DiscordWaitingCheck", "⏳ {0} requested {1}. Waiting for approval." },
        { "Notif_SettingsUnreadable", "⚠️ Your settings could not be read. They were set aside and the defaults are in use." },
        { "Notif_ChatReconnected", "🔗 Connection to {1} restored." },
        { "Notif_WidgetServerError", "⚠️ Widgets are not working: port {1} is not available." },

        // 8. CONSOLA DE DEPURACIÓN
        { "Log_AntiSpam", "[Anti-Spam] Request by {0} on {1} was duplicated." },
        { "Log_SearchAttempt", "\n=> [{0}] REQUEST FROM {1} SEARCH: '{2}' (Attempt {3})" },
        { "Log_SearchGaveUp", "[Search] Ran out of {0} attempts for '{1}'. GD servers are not responding." },
        { "Log_ProcessorError", "[Critical Error in Processor] Failed to process level: {0}" },
        { "Log_GDServerError", "[GD Error] Empty response or server error for the level." },
        { "Log_GDDLFailed", "[Warning] Could not fetch GDDL for {0}" },
        { "Log_GDDLHttpError", "[GDDL HTTP ERROR]: Server responded with code {0} for level {1}" },
        { "Log_AREDLCloudflare", "[AREDL WARNING]: Cloudflare intercepted the direct request and returned HTML." },
        { "Log_AREDLHttpError", "[AREDL HTTP ERROR]: Server responded with code {0}" },
        { "Log_AREDLCritical", "[AREDL CRITICAL]: {0}" },
        { "Log_GDInternalError", "[Internal GD Error]: {0}" },
        { "Log_ObsExportError", "[OBS Export Error] Could not save texts: {0}" },
        { "Log_SettingsCreated", "[System] Configuration file created successfully." },
        { "Log_SettingsLoadError", "[Error loading settings]: {0}" },
        { "Log_SettingsSaveError", "[Critical Error saving settings]: {0}" },
        { "Log_InterceptorStarting", "\n=== STARTING MULTI-SOURCE INTERCEPTOR ===" },
        { "Log_BotPaused", "[Waiting] Bot is paused. Add a link and click Save Changes..." },
        { "Log_RestartingConnection", "[System] Restarting connection with {0}..." },
        { "Log_StartingNewConnection", "[System] Starting new connection with {0}..." },
        { "Log_LoginClosed", "[System] Login window closed. Saving session..." },
        { "Log_FirstTimeLogin", "[System] First-time access detected for {0}. Opening login window..." },
        { "Log_RestartingHidden", "[System] Restarting {0} with saved session..." },
        { "Log_UniversalCmdDetected", "[Universal] Command detected on Other: User: {0} | Query: {1}" },
        { "Log_UniversalError", "[Universal Error] {0}" },
        { "Log_DedicatedMsg", "[Dedicated] Message from {0} on {1}: {2}" },
        { "Log_DedicatedCmdValidated", "[Dedicated] Command validated, searching phrase: '{0}'" },
        { "Log_DedicatedAutoId", "[Dedicated] Auto-ID detected: {0}" },
        { "Log_DedicatedError", "[Dedicated Bridge Error] {0}" },
        { "Log_ConnectedInjectingEye", "[System] Connected to {0}. Injecting Dedicated interceptor..." },
        { "Log_CleaningHistory", "[System] Ignoring the history of {0}... (waiting {1} s)" },
        { "Log_InterceptorActive", "[System] Interceptor active for {0}!" },
        { "Log_ConnectedInjectingNinja", "[System] Connected to {0}. Injecting interceptor..." },
        { "Log_WatchdogBlank", "[watcher] Other went blank or failed to render. Reloading!" },
        { "Log_WatchdogRestored", "[watcher] Other restored and interceptor reinjected." },
        { "Log_CriticalWidgetError", "[Error] Failure in {0}: {1}" },
        { "Log_SECmdDetected", "[StreamElements] Command by {0}: {1}" },
        { "Log_SEAutoIdDetected", "[StreamElements] Auto-ID by {0}: {1}" },
        { "Log_SETaskError", "[StreamElements Task Error] {0}" },
        { "Log_TikTokCmd", "[TikTok API] Command by {0}: {1}" },
        { "Log_TikTokAutoId", "[TikTok API] Auto-ID by {0}: {1}" },
        { "Log_TikTokTaskError", "[TikTok API Task Error] {0}" },
        { "Log_TikTokConnecting", "[System] Connecting to TikTok API for user: @{0}..." },
        { "Log_TikTokConnError", "[TikTok API Error] Could not connect: {0}" },
        { "Log_TikTokDisconnected", "[TikTok] Connection to {0} dropped. Retrying in {1}s." },
        { "Log_TikTokReconnected", "[TikTok] Connection to {0} restored." },
        { "Log_TikTokLoopStopped", "[TikTok] Watchdog for {0} stopped." },
        { "Log_TikTokConnected", "[TikTok] Connected to {0}'s live. Listening to chat." },
        { "Log_SettingsSaved", "[System] Settings saved successfully." },
        { "Log_ClosingConnectionsForCache", "[System] Closing active connections to clear cache..." },
        { "Log_CacheClearedSuccess", "[System] Cache cleared successfully! (You will need to log in again)." },
        { "Log_CacheAlreadyClean", "[System] Cache was already clean." },
        { "Log_CacheClearError", "[Error] Could not clear cache: {0}" },
        { "Log_QueueCleared", "[System] The level list has been cleared." },
        { "Log_NotificationsCleared", "[System] Notification history cleared successfully." },
        { "Log_SEJsonError", "[SE JSON Error]: {0}" },
        { "Log_LevelAdded", "[System] Level successfully added (Platform: {0})" },
        { "Log_ChatLinkRemoved", "[Links] Source {0} removed and disconnected." },
        { "Log_PlaywrightPreparing", "[Browser] Preparing the internal browser. On first run it downloads about 150 MB and may take several minutes." },
        { "Log_PlaywrightReady", "[Browser] Internal browser ready." },
        { "Log_PlaywrightFailed", "[Browser] Could not prepare the browser (exit code {0}). Check your internet connection. Browser-based sources will not connect; TikTok and Discord will." },
        { "Log_PlaywrightError", "[Browser] Error preparing the browser: {0}" },
        { "Log_UserAgentFallback", "[Browser] Could not read the Chromium version ({0}); using a generic identifier." },
        { "Log_QueueFileCreated", "[Queue] queue_data.json created at {0}" },
        { "Log_ThemeReadError", "[Themes] Error reading {0}: {1}" },
        { "Log_OpenPathError", "[Open] Could not open {0}: {1}" },
        { "Log_EditorNotFound", "[Editor] SheepGDEditor executable not found. Looked in: {0}" },
        { "Log_ModInstallError", "[Mod] Could not install: {0}" },
        { "Log_ModReleaseUnavailable", "[Mod] Could not download the latest version from GitHub; using the copy bundled with the program ({0})." },
        { "Log_ModSkippedOlder", "[Mod] The bundled copy ({0}) is not installed because the installed one is newer ({1})." },
        { "Log_ModInstalledFromGitHub", "[Mod] Installed version {0} from GitHub at {1}" },
        { "Log_ModInstalledBundled", "[Mod] Installed the bundled version {0} at {1}" },
        { "Log_ModUpdateAvailable", "[Mod] A new version of the mod is available: {0} (installed: {1})." },
        { "Log_ModUpdateCheckFailed", "[Mod] Could not check GitHub for a new version of the mod." },
        { "Log_DiscordWaitingCheck", "[Discord] {0} requested {1}. Waiting for approval." },
        { "Log_DiscordApproved", "[Discord] Approved: {1} by {0} joins the queue." },
        { "Log_DiscordAlreadyQueued", "[Discord] That message already had a level in the queue; reaction ignored." },
        { "Log_QueueUnreadable", "[Queue] Could not read queue_data.json. A copy was saved to {0} and the queue starts empty." },
        { "Log_QueueWriteError", "[Queue] Could not save queue_data.json: {0}" },
        { "Log_AutoSkipStampError", "[AutoSkip] Could not save the timer stamp: {0}" },
        { "Log_DiscordMessageDeleted", "[Discord] Message deleted: level {0} by {1} was removed from the queue." },
        { "Log_SettingsMovedAside", "[System] settings.json could not be read and was set aside as {0}. Using default values." },
        { "Log_ThumbnailUnavailable", "[Background] Could not download the level thumbnail ({0}). Using the default background." },
        { "Log_PlaceholderMissing", "[Background] placeholder_bg.png was not found in the program resources: {0}" },
        { "Log_GDHttpError", "[GD Error] The server responded with code {0}." },
        { "Log_DiscordInvalidLink", "[Discord] Invalid channel link. Use a channel link: https://discord.com/channels/server/channel" },
        { "Log_DiscordConnected", "[Discord] Connected. The bot is listening to the channel." },
        { "Log_DiscordConnecting", "[Discord] Connecting to the servers..." },
        { "Log_DiscordCriticalError", "[Discord Error] {0}" },
        { "Log_DiscordLibrary", "[Discord] {0}" },
        { "Log_DiscordCloseError", "[Discord] Error while closing: {0}" },
        { "Log_DiscordDisconnected", "[Discord] Bot disconnected." },
        { "Log_DiscordTokenEmpty", "[Discord] The bot token is empty. Paste it in the Discord section of Settings." },
        { "Log_InterceptorStartError", "[Error] Could not connect the chat links: {0}" },
        { "Log_ChatReconnecting", "[{0}] Connection lost. Retrying in {1} s." },
        { "Log_ChatReconnected", "[{0}] Connection restored." },
        { "Log_BrowserClosed", "[{0}] The internal browser closed unexpectedly." },
        { "Log_WatchdogError", "[watcher] {0}: {1}" },
        { "Log_ProfileMigrated", "[Browser] The saved session for {0} was moved to the new folder format." },
        { "Log_ProfileMigrationError", "[Browser] Could not move the saved session for {0}: {1}" },
        { "Log_ReLoginRequested", "[{0}] Saved session deleted. The login window will open." },
        { "Log_ReLoginError", "[{0}] Could not delete the saved session: {1}" },
        { "Log_PageError", "[{0}] JavaScript error on the page: {1}" },
        { "Log_SEHistory", "[StreamElements] History detected ({0} messages): ignored." },
        { "Log_SEReadyHistory", "[StreamElements] History discarded. Ready for requests." },
        { "Log_SEReadyTimeout", "[StreamElements] No history to discard. Ready for requests." },
        { "Log_TtsInitError", "[Voice] Could not start audio (BASS): {0}. Notifications will not be read aloud." },
        { "Log_TtsHttpError", "[Voice] Google's voice service responded with code {0}." },
        { "Log_TtsStreamError", "[Voice] Could not open the received audio (BASS): {0}" },
        { "Log_TtsError", "[Voice] Error: {0}" },
        { "Log_NotifSaveError", "[Notifications] Could not save notifications.json: {0}" },
        { "Log_NotifObsError", "[Notifications] Could not write obs_notifications.txt: {0}" },
        { "Log_WidgetServerStarted", "[Widgets] Server started at {0}" },
        { "Log_WidgetServerStopped", "[Widgets] Server stopped." },
        { "Log_WidgetServerError", "[Widgets] Could not open the server on port {0}: {1}. Is another program using it? Change the port in Settings." },
        { "Log_WidgetRequestError", "[Widgets] Invalid message from a widget: {0}" },
        { "Log_AutoSkipExpired", "[AutoSkip] Time is up for {0}." },
        { "Log_AutoSkipNewLevelDetected", "[AutoSkip] A new level arrived: skipping the expired level." },
        { "Log_AutoSkipNext", "[AutoSkip] Moving to the next level in the queue." },
        { "Log_FilterOnlyIds", "[Filter] Request by {0} ignored: '{1}' is not an ID (only IDs are accepted)." },
        { "Log_FilterUserLimit", "[Filter] Request by {0} ignored: they reached their level limit in the queue." },
        { "Log_GDLevelNotFound", "[GD] The level does not exist." },
        { "Log_LevelBlocked", "[{0}] Level by {1} rejected: {2}" },
    };

    // ==========================================
    // PORTUGUÉS
    // ==========================================
    private static readonly Dictionary<string, string> _pt = new()
    {
        // 1. VALORES POR DEFECTO Y ESTADOS
        { "Default_UnknownName", "Desconhecido" },
        { "Default_Unrated", "UNRATED" },
        { "Default_NA", "NA" },
        { "Default_Viewer", "Espectador" },
        { "Default_UnknownAuthor", "Desconhecido" },
        { "Default_WaitingRequester", "Aguardando pedidos..." },
        { "Default_System", "Sistema" },

        // 2. INTERFAZ PRINCIPAL Y MENÚS
        { "UI_MenuToggle", "Expandir / Recolher" },
        { "UI_MenuCollapse", "Recolher" },
        { "UI_MenuLevels", "Lista de Níveis" },
        { "UI_MenuSettings", "Configurações" },
        { "UI_MenuConsole", "Console Debug" },
        { "UI_ConsoleTitle", ">_ TERMINAL DE DEPURAÇÃO" },
        { "UI_ConsoleBack", "VOLTAR AOS NÍVEIS" },
        { "UI_By", "POR" },
        { "UI_ThemeDefault", "Padrão" },
        { "UI_ThemeByAuthor", "{0} - por {1}" },
        { "UI_SendBy", "Enviado por" },
        { "UI_CopySong", "Copiar Música" },
        { "UI_CopyId", "Copiar ID" },
        { "UI_Previous", "Anterior" },
        { "UI_Next", "Próximo" },
        { "UI_Random", "Aleatório" },
        { "UI_Remove", "Remover" },
        { "UI_Priority", "Prioridade" },
        { "UI_LoginRequiredTitle", "Login Necessário" },
        { "UI_LoginPrompt", "Acessando {0}\n\nFaça login usando a janela do navegador que se abrirá\n\n(se necessário).\n\nDepois clique em PRONTO." },
        { "UI_LoginReadyBtn", "PRONTO" },
        { "UI_TitleClearWarning", "EXCLUIR" },
        { "UI_BtnClearQueue", "EXCLUIR TODOS OS NÍVEIS ENVIADOS" },
        { "UI_BtnClearNotifications", "APAGAR NOTIFICAÇÕES" },
        { "UI_RequestsEnabled", "SOLICITAÇÕES ATIVADAS" },
        { "UI_RequestsDisabled", "SOLICITAÇÕES PAUSADAS" },

        // 3. PANTALLA DE AJUSTES
        { "UI_SettingsPanelTitle", "CONFIGURAÇÕES" },
        { "UI_BtnClearCache", "LIMPAR CACHE" },
        { "UI_TooltipClearCache", "Limpa as sessões de chat salvas" },
        { "UI_BtnSaveChanges", "SALVAR ALTERAÇÕES" },
        { "UI_SecNetworkCmds", "REDE E COMANDOS" },
        { "UI_ChatLinksTitle", "Links de Chat (WebSockets ou Popouts)" },
        { "UI_ChatUrlWatermark", "Cole a URL do chat aqui..." },
        { "UI_TooltipReconnect", "Reconectar este link" },
        { "UI_TooltipDelete", "Excluir" },
        { "UI_BtnAddLink", "+ Adicionar Link" },
        { "UI_CmdRequestsTitle", "Comandos de Request (Ex: !add)" },
        { "UI_BtnAddCmd", "+ Adicionar Comando" },
        { "UI_SecQueueRules", "REGRAS DA FILA" },
        { "UI_LimitPerUser", "Limite por usuário" },
        { "UI_LimitRequests", "Limite de solicitações" },
        { "UI_AutoSkip", "Auto-Skip de Níveis" },
        { "UI_OnlyRated", "Permitir APENAS níveis com Rate" },
        { "UI_AllowRepeated", "Permitir Níveis Repetidos" },
        { "UI_StrictId", "Modo estrito: Apenas ID" },
        { "UI_AutoDetectId", "Auto-detectar IDs" },
        { "UI_LimitByDiff", "Limitar por Dificuldade:" },
        { "UI_EnableBlacklist", "Ativar Lista Negra" },
        { "UI_BlacklistWatermark", "ID do nível para bloquear..." },
        { "UI_BtnAddBlacklist", "+ Adicionar à Lista Negra" },
        { "UI_SecInterfaceStream", "INTERFACE E STREAMING" },
        { "UI_AlwaysOnTop", "Manter janela Sempre no Topo" },
        { "UI_ExportObs", "Exportar textos ao vivo para OBS (.txt e widget)" },
        { "UI_ThumbOpacity", "Opacidade da Miniatura (Fundo)" },
        { "UI_SecNotifications", "NOTIFICAÇÕES VISUAIS E OBS" },
        { "UI_NotifySuccess", "Notificar níveis adicionados com sucesso" },
        { "UI_NotifyFilters", "Notificar bloqueios e filtros" },
        { "UI_NotifyAutoSkip", "Notificar eventos de Auto-Skip" },
        { "UI_NotifySystem", "Notificar eventos do sistema" },
        { "UI_TtsVolume", "Volume TTS:" },
        { "UI_TtsReadSuccess", "TTS: Ler aceitos" },
        { "UI_TtsReadBlocks", "TTS: Ler bloqueados" },
        { "UI_TtsReadSystem", "TTS: Ler eventos do sistema" },
        { "UI_DiscordTokenTitle", "Token do Bot do Discord:" },
        { "UI_DiscordTokenWatermark", "Cole o Token do seu bot aqui..." },
        { "UI_DiscordTutorialBtn", "Obter Token" },
        { "UI_SecTools", "FERRAMENTAS" },
        { "UI_BtnOpenThemesFolder", "Abrir pasta de temas para widgets" },
        { "UI_BtnOpenThemeEditor", "Abrir editor de temas para widgets" },
        { "UI_BtnInstallGeodeMod", "Instalar Mod do Geode" },
        { "UI_PickGdFolder", "Selecione a pasta onde o Geometry Dash está instalado" },
        { "UI_ConfirmGdTitle", "Instalar Mod do Geode" },
        { "UI_ConfirmGdMsg", "O mod será instalado nesta pasta:" },
        { "UI_BtnYesInstallHere", "Sim, instalar aqui" },
        { "UI_BtnChooseAnother", "Escolher outra pasta" },
        { "UI_BtnCancelDialog", "Cancelar" },
        { "UI_ModUpdateTitle", "Atualização do mod do Geode" },
        { "UI_ModUpdateMsg", "Há uma versão nova do mod do SheepGD para o Geometry Dash: {0}. Você tem a {1} instalada." },
        { "UI_ModUpdateHint", "Feche o Geometry Dash antes de atualizar; a versão nova carrega quando você abri-lo de novo." },
        { "UI_BtnUpdate", "Atualizar" },
        { "UI_BtnLater", "Mais tarde" },
        { "UI_DiscordOnlyChecked", "Listar apenas níveis reagidos com ✅ ☑️ ✔️ ♥️ ❤️ 👍" },
        { "UI_DiscordOnlyCheckedTip", "O nível entra na fila quando alguém reage à mensagem, não ao enviá-la." },

        // WIDGETS (sección de Ajustes)
        { "UI_SecWidgets", "WIDGETS DO OBS (SERVIDOR LOCAL)" },
        { "UI_EnableWidgets", "Ativar Servidor de Widgets" },
        { "UI_WidgetPort", "Porta do Servidor:" },
        { "UI_WidgetNotification", "Widget de Notificações" },
        { "UI_WidgetPlaying", "Widget do Nível em Jogo" },
        { "UI_WidgetQueue", "Widget da Fila de Níveis" },
        { "UI_WidgetUnified", "Widget Unificado (Tudo em um)" },
        { "UI_Theme", "Tema:" },
        { "UI_Copy", "Copiar" },
        { "UI_BtnReLogin", "Login" },
        { "UI_TooltipReLogin", "Apaga a sessão salva deste link e abre o login novamente" },


        // 4. TEXTOS PARA EXPORTACIÓN A OBS (TXT)
        { "Obs_WaitingLevels", "Aguardando níveis..." },
        { "Obs_PlayingFormat", "Jogando: {0} ({1}) | ID: {2} | Pedido por: {3}" },
        { "Obs_NextFormat", "Próximo: {0} (Pedido por {1})" },
        { "Obs_NextEmpty", "Próximo: --" },
        { "Obs_QueueItemFormat", "{0}. {1} (Pedido por {2})" },
        { "Obs_QueueEmpty", "A fila está vazia." },

        // 5. ENUMS (TEXTOS DE LOS DESPLEGABLES)
        { "Enum_ChatPlatform_SheepChat", "SheepChat (Dedicado)" },
        { "Enum_ChatPlatform_SocialStreamNinja", "SocialStream.Ninja (Dedicado)" },
        { "Enum_ChatPlatform_StreamElements", "StreamElements (Dedicado)" },
        { "Enum_ChatPlatform_TikTok", "TikTok Live (API)" },
        { "Enum_ChatPlatform_Discord", "Discord (Canal com Bot Privado)" },
        { "Enum_ChatPlatform_Otro", "Outros (Universal)" },
        { "Enum_DifficultyFilterCondition_Solo", "Apenas" },
        { "Enum_DifficultyFilterCondition_TodosExcepto", "Todas EXCETO" },
        { "Enum_DifficultyFilterCondition_Hasta", "Máximo até" },
        { "Enum_DifficultyFilterCondition_APartirDe", "Mínimo a partir de" },
        { "Enum_DifficultyRating_Todos", "Qualquer Dificuldade" },
        { "Enum_DifficultyRating_None", "N/A (Sem Dificuldade)" },
        { "Enum_DifficultyRating_Auto", "Auto" },
        { "Enum_DifficultyRating_Easy", "Easy" },
        { "Enum_DifficultyRating_Normal", "Normal" },
        { "Enum_DifficultyRating_Hard", "Hard" },
        { "Enum_DifficultyRating_Harder", "Harder" },
        { "Enum_DifficultyRating_Insane", "Insane" },
        { "Enum_DifficultyRating_DemonTodos", "Qualquer Demon" },
        { "Enum_DifficultyRating_EasyDemon", "Easy Demon" },
        { "Enum_DifficultyRating_MediumDemon", "Medium Demon" },
        { "Enum_DifficultyRating_HardDemon", "Hard Demon" },
        { "Enum_DifficultyRating_InsaneDemon", "Insane Demon" },
        { "Enum_DifficultyRating_ExtremeDemon", "Extreme Demon" },
        { "Enum_TimeUnit_Segundos", "Segundos" },
        { "Enum_TimeUnit_Minutos", "Minutos" },
        { "Enum_TimeUnit_Horas", "Horas" },

        // 6. RAZONES DE FILTRADO
        { "FilterReason_Repeated", "Nível repetido." },
        { "FilterReason_Blacklisted", "ID na lista negra." },
        { "FilterReason_Unrated", "Nível sem Rate/Estrelas." },
        { "FilterReason_OnlyType", "Apenas permitido: {0}" },
        { "FilterReason_BlockedDiff", "Dificuldade bloqueada." },
        { "FilterReason_MaxDiff", "Excede o máximo ({0})" },
        { "FilterReason_MinDiff", "Abaixo do mínimo ({0})" },

        // 7. NOTIFICACIONES
        { "Notif_FilterOnlyIds", "❗ {0} ignorado: {1} não é um ID." },
        { "Notif_FilterUserLimit", "❗ {0} atingiu seu limite na fila." },
        { "Notif_LevelBlocked", "⛔ Nível {1} de {0} bloqueado: {2}" },
        { "Notif_GDLevelNotFound", "❓ O nível {1} de {0} não existe no GD." },
        { "Notif_GDSearchTimeout", "⌛ Não foi possível buscar {1} de {0}: os servidores do GD não respondem." },
        { "Notif_LevelAdded", "✅ Nível {1} de {0} adicionado com sucesso." },
        { "Notif_AutoSkipNew", "⏭️ Nível na tela expirou, pulando..." },
        { "Notif_AutoSkipExpired", "⏱️ O tempo do nível atual expirou." },
        { "Notif_AutoSkipNext", "⏭️ Pulando para o próximo nível..." },
        { "Notif_QueueEmpty", "📭 A lista de pedidos está vazia." },
        { "Notif_ChatDisconnected", "🔌 Conexão perdida com {1}. Tentando novamente..." },
        { "Notif_LevelPrioritized", "⬆️ Nível {1} de {0} movido para prioridade." },
        { "Notif_RequestsEnabled", "✅ Sistema: As solicitações de níveis foram ABERTAS." },
        { "Notif_RequestsDisabled", "⛔ Sistema: As solicitações de níveis foram PAUSADAS." },
        { "Notif_FilterGlobalLimit", "⛔ {0} ignorado: A fila atingiu o limite máximo de {1} níveis." },
        { "Log_FilterGlobalLimit", "[Filtro] Pedido de {0} ignorado: Fila cheia." },
        { "Notif_TikTokDisconnected", "🔌 Conexão com o TikTok ({1}) perdida. Tentando novamente..." },
        { "Notif_TikTokReconnected", "🔗 Conexão com o TikTok ({1}) restabelecida." },
        { "Notif_ModInstalled", "🧩 Mod instalado. Reinicie o Geometry Dash para carregá-lo." },
        { "Notif_ModGeodeMissing", "⚠️ Essa pasta não tem o Geode instalado. Instale o Geode primeiro." },
        { "Notif_ModFileMissing", "⚠️ O arquivo .geode não foi encontrado dentro do programa." },
        { "Notif_ModUpdated", "🧩 Mod atualizado para a {1}. Reinicie o Geometry Dash para carregá-lo." },
        { "Notif_ModUpdateFailed", "⚠️ Não foi possível baixar a atualização do mod. Tente mais tarde com \"Instalar Mod do Geode\"." },
        { "Notif_ModInstallFailed", "⚠️ Não foi possível instalar o mod. Se o Geometry Dash estiver aberto, feche-o e tente novamente." },
        { "Notif_ModAlreadyNewer", "🧩 Você já tem uma versão mais nova do mod instalada ({1}); nada foi alterado." },
        { "Notif_DiscordWaitingCheck", "⏳ {0} pediu {1}. Aguardando aprovação." },
        { "Notif_SettingsUnreadable", "⚠️ Não foi possível ler suas configurações. Foram guardadas à parte e os padrões estão em uso." },
        { "Notif_ChatReconnected", "🔗 Conexão com {1} restabelecida." },
        { "Notif_WidgetServerError", "⚠️ Os widgets não estão funcionando: a porta {1} não está disponível." },

        // 8. CONSOLA DE DEPURACIÓN
        { "Log_AntiSpam", "[Anti-Spam] O pedido de {0} em {1} estava duplicado." },
        { "Log_SearchAttempt", "\n=> [{0}] REQUEST DE {1} BUSCA: '{2}' (Tentativa {3})" },
        { "Log_SearchGaveUp", "[Busca] Esgotaram-se as {0} tentativas para '{1}'. Os servidores do GD não respondem." },
        { "Log_ProcessorError", "[Erro Crítico no Processador] Falha ao processar nível: {0}" },
        { "Log_GDServerError", "[Erro GD] Resposta vazia ou erro no servidor para o nível." },
        { "Log_GDDLFailed", "[Aviso] Não foi possível obter GDDL para {0}" },
        { "Log_GDDLHttpError", "[ERRO HTTP GDDL]: O servidor respondeu com código {0} para o nível {1}" },
        { "Log_AREDLCloudflare", "[AVISO AREDL]: Cloudflare interceptou o pedido direto e retornou HTML." },
        { "Log_AREDLHttpError", "[ERRO HTTP AREDL]: O servidor respondeu com código {0}" },
        { "Log_AREDLCritical", "[CRÍTICO AREDL]: {0}" },
        { "Log_GDInternalError", "[Erro Interno GD]: {0}" },
        { "Log_ObsExportError", "[Erro Exportação OBS] Não foi possível salvar os textos: {0}" },
        { "Log_SettingsCreated", "[Sistema] Arquivo de configurações criado com sucesso." },
        { "Log_SettingsLoadError", "[Erro ao carregar configurações]: {0}" },
        { "Log_SettingsSaveError", "[Erro Crítico ao salvar configurações]: {0}" },
        { "Log_InterceptorStarting", "\n=== INICIANDO INTERCEPTADOR MULTI-FONTE ===" },
        { "Log_BotPaused", "[Aguardando] O bot está em pausa. Adicione um link e clique em Salvar Alterações..." },
        { "Log_RestartingConnection", "[Sistema] Reiniciando conexão com {0}..." },
        { "Log_StartingNewConnection", "[Sistema] Iniciando nova conexão com {0}..." },
        { "Log_LoginClosed", "[Sistema] Janela de Login fechada. Salvando sessão..." },
        { "Log_FirstTimeLogin", "[Sistema] Primeiro acesso detectado para {0}. Abrindo janela de login..." },
        { "Log_RestartingHidden", "[Sistema] Reiniciando {0} com a sessão salva..." },
        { "Log_UniversalCmdDetected", "[Universal] Comando detectado em Outro: Usuário: {0} | Query: {1}" },
        { "Log_UniversalError", "[Erro Universal] {0}" },
        { "Log_DedicatedMsg", "[Dedicado] Mensagem de {0} em {1}: {2}" },
        { "Log_DedicatedCmdValidated", "[Dedicado] Comando validado, buscando frase: '{0}'" },
        { "Log_DedicatedAutoId", "[Dedicado] Auto-ID detectado: {0}" },
        { "Log_DedicatedError", "[Erro Ponte Dedicada] {0}" },
        { "Log_ConnectedInjectingEye", "[Sistema] Conectado a {0}. Injetando interceptador Dedicado..." },
        { "Log_CleaningHistory", "[Sistema] Ignorando o histórico de {0}... (aguardando {1} s)" },
        { "Log_InterceptorActive", "[Sistema] Interceptador ativo para {0}!" },
        { "Log_ConnectedInjectingNinja", "[Sistema] Conectado a {0}. Injetando interceptador..." },
        { "Log_WatchdogBlank", "[watcher] Outro ficou em branco ou falhou ao renderizar. Recarregando!" },
        { "Log_WatchdogRestored", "[watcher] Outro restaurado e interceptador reinjetado." },
        { "Log_CriticalWidgetError", "[Erro] Falha em {0}: {1}" },
        { "Log_SECmdDetected", "[StreamElements] Comando de {0}: {1}" },
        { "Log_SEAutoIdDetected", "[StreamElements] Auto-ID de {0}: {1}" },
        { "Log_SETaskError", "[Erro Tarefa StreamElements] {0}" },
        { "Log_TikTokCmd", "[TikTok API] Comando de {0}: {1}" },
        { "Log_TikTokAutoId", "[TikTok API] Auto-ID de {0}: {1}" },
        { "Log_TikTokTaskError", "[Erro Tarefa TikTok API] {0}" },
        { "Log_TikTokConnecting", "[Sistema] Conectando à API do TikTok para o usuário: @{0}..." },
        { "Log_TikTokConnError", "[Erro API TikTok] Não foi possível conectar: {0}" },
        { "Log_TikTokDisconnected", "[TikTok] Conexão com {0} caiu. Nova tentativa em {1} s." },
        { "Log_TikTokReconnected", "[TikTok] Conexão com {0} restabelecida." },
        { "Log_TikTokLoopStopped", "[TikTok] Vigilância de {0} interrompida." },
        { "Log_TikTokConnected", "[TikTok] Conectado à live de {0}. Ouvindo o chat." },
        { "Log_SettingsSaved", "[Sistema] Configurações salvas com sucesso." },
        { "Log_ClosingConnectionsForCache", "[Sistema] Fechando conexões ativas para limpar o cache..." },
        { "Log_CacheClearedSuccess", "[Sistema] Cache limpo com sucesso! (Você precisará fazer login novamente)." },
        { "Log_CacheAlreadyClean", "[Sistema] O cache já estava limpo." },
        { "Log_CacheClearError", "[Erro] Não foi possível limpar o cache: {0}" },
        { "Log_QueueCleared", "[Sistema] A lista de níveis foi apagada." },
        { "Log_NotificationsCleared", "[Sistema] Histórico de notificações apagado com sucesso." },
        { "Log_SEJsonError", "[SE JSON Error]: {0}" },
        { "Log_LevelAdded", "[Sistema] Nível adicionado com sucesso (Plataforma: {0})" },
        { "Log_ChatLinkRemoved", "[Links] Fonte {0} removida e desconectada." },
        { "Log_PlaywrightPreparing", "[Navegador] Preparando o navegador interno. Se for a primeira vez, baixará cerca de 150 MB e pode demorar vários minutos." },
        { "Log_PlaywrightReady", "[Navegador] Navegador interno pronto." },
        { "Log_PlaywrightFailed", "[Navegador] Não foi possível preparar o navegador (código {0}). Verifique sua conexão. As fontes que usam navegador não conectarão; TikTok e Discord sim." },
        { "Log_PlaywrightError", "[Navegador] Erro ao preparar o navegador: {0}" },
        { "Log_UserAgentFallback", "[Navegador] Não foi possível ler a versão do Chromium ({0}); usando um identificador genérico." },
        { "Log_QueueFileCreated", "[Fila] queue_data.json criado em {0}" },
        { "Log_ThemeReadError", "[Temas] Erro ao ler {0}: {1}" },
        { "Log_OpenPathError", "[Abrir] Não foi possível abrir {0}: {1}" },
        { "Log_EditorNotFound", "[Editor] Executável do SheepGDEditor não encontrado. Procurado em: {0}" },
        { "Log_ModInstallError", "[Mod] Não foi possível instalar: {0}" },
        { "Log_ModReleaseUnavailable", "[Mod] Não foi possível baixar a última versão do GitHub; usando a cópia incluída no programa ({0})." },
        { "Log_ModSkippedOlder", "[Mod] A cópia incluída ({0}) não foi instalada porque a instalada é mais nova ({1})." },
        { "Log_ModInstalledFromGitHub", "[Mod] Instalada a versão {0} do GitHub em {1}" },
        { "Log_ModInstalledBundled", "[Mod] Instalada a versão {0} incluída no programa em {1}" },
        { "Log_ModUpdateAvailable", "[Mod] Há uma versão nova do mod: {0} (instalada: {1})." },
        { "Log_ModUpdateCheckFailed", "[Mod] Não foi possível verificar no GitHub se há uma versão nova do mod." },
        { "Log_DiscordWaitingCheck", "[Discord] {0} pediu {1}. Aguardando aprovação." },
        { "Log_DiscordApproved", "[Discord] Aprovado: {1} de {0} entra na fila." },
        { "Log_DiscordAlreadyQueued", "[Discord] Essa mensagem já tinha um nível na fila; reação ignorada." },
        { "Log_QueueUnreadable", "[Fila] Não foi possível ler queue_data.json. Uma cópia foi salva em {0} e a fila começa vazia." },
        { "Log_QueueWriteError", "[Fila] Não foi possível salvar queue_data.json: {0}" },
        { "Log_AutoSkipStampError", "[AutoSkip] Não foi possível salvar o carimbo de tempo: {0}" },
        { "Log_DiscordMessageDeleted", "[Discord] Mensagem apagada: o nível {0} de {1} foi removido da fila." },
        { "Log_SettingsMovedAside", "[Sistema] settings.json não pôde ser lido e foi guardado à parte como {0}. Usando os valores padrão." },
        { "Log_ThumbnailUnavailable", "[Fundo] Não foi possível baixar a miniatura do nível ({0}). Usando o fundo padrão." },
        { "Log_PlaceholderMissing", "[Fundo] placeholder_bg.png não foi encontrado nos recursos do programa: {0}" },
        { "Log_GDHttpError", "[Erro GD] O servidor respondeu com o código {0}." },
        { "Log_DiscordInvalidLink", "[Discord] Link de canal inválido. Use o link de um canal: https://discord.com/channels/servidor/canal" },
        { "Log_DiscordConnected", "[Discord] Conectado. O bot está ouvindo o canal." },
        { "Log_DiscordConnecting", "[Discord] Iniciando conexão com os servidores..." },
        { "Log_DiscordCriticalError", "[Erro Discord] {0}" },
        { "Log_DiscordLibrary", "[Discord] {0}" },
        { "Log_DiscordCloseError", "[Discord] Erro ao fechar: {0}" },
        { "Log_DiscordDisconnected", "[Discord] Bot desconectado." },
        { "Log_DiscordTokenEmpty", "[Discord] O token do bot está vazio. Cole-o na seção do Discord nas Configurações." },
        { "Log_InterceptorStartError", "[Erro] Não foi possível conectar os links de chat: {0}" },
        { "Log_ChatReconnecting", "[{0}] Conexão perdida. Tentando novamente em {1} s." },
        { "Log_ChatReconnected", "[{0}] Conexão restabelecida." },
        { "Log_BrowserClosed", "[{0}] O navegador interno fechou inesperadamente." },
        { "Log_WatchdogError", "[watcher] {0}: {1}" },
        { "Log_ProfileMigrated", "[Navegador] A sessão salva de {0} foi adaptada ao novo formato de pasta." },
        { "Log_ProfileMigrationError", "[Navegador] Não foi possível adaptar a sessão salva de {0}: {1}" },
        { "Log_ReLoginRequested", "[{0}] Sessão salva apagada. A janela de login será aberta." },
        { "Log_ReLoginError", "[{0}] Não foi possível apagar a sessão salva: {1}" },
        { "Log_PageError", "[{0}] Erro de JavaScript na página: {1}" },
        { "Log_SEHistory", "[StreamElements] Histórico detectado ({0} mensagens): ignorado." },
        { "Log_SEReadyHistory", "[StreamElements] Histórico descartado. Pronto para receber pedidos." },
        { "Log_SEReadyTimeout", "[StreamElements] Sem histórico para descartar. Pronto para receber pedidos." },
        { "Log_TtsInitError", "[Voz] Não foi possível iniciar o áudio (BASS): {0}. Os avisos não serão lidos em voz alta." },
        { "Log_TtsHttpError", "[Voz] O serviço de voz do Google respondeu com o código {0}." },
        { "Log_TtsStreamError", "[Voz] Não foi possível abrir o áudio recebido (BASS): {0}" },
        { "Log_TtsError", "[Voz] Erro: {0}" },
        { "Log_NotifSaveError", "[Notificações] Não foi possível salvar notifications.json: {0}" },
        { "Log_NotifObsError", "[Notificações] Não foi possível escrever obs_notifications.txt: {0}" },
        { "Log_WidgetServerStarted", "[Widgets] Servidor iniciado em {0}" },
        { "Log_WidgetServerStopped", "[Widgets] Servidor parado." },
        { "Log_WidgetServerError", "[Widgets] Não foi possível abrir o servidor na porta {0}: {1}. Outro programa está usando? Mude a porta nas Configurações." },
        { "Log_WidgetRequestError", "[Widgets] Mensagem inválida de um widget: {0}" },
        { "Log_AutoSkipExpired", "[AutoSkip] O tempo de {0} acabou." },
        { "Log_AutoSkipNewLevelDetected", "[AutoSkip] Chegou um nível novo: pulando o nível expirado." },
        { "Log_AutoSkipNext", "[AutoSkip] Passando para o próximo nível da fila." },
        { "Log_FilterOnlyIds", "[Filtro] Pedido de {0} ignorado: '{1}' não é um ID (só são aceitos IDs)." },
        { "Log_FilterUserLimit", "[Filtro] Pedido de {0} ignorado: atingiu seu limite de níveis na fila." },
        { "Log_GDLevelNotFound", "[GD] O nível não existe." },
        { "Log_LevelBlocked", "[{0}] Nível de {1} rejeitado: {2}" },
    };
}
