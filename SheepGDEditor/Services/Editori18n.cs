using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia.Markup.Xaml;
using SheepGDRequest.Services; // AppPaths: enlazado desde SheepGDRequest en el .csproj

namespace SheepGDEditor.Services;

/// <summary>
/// Traducciones del Editor. Mismo enfoque que SheepI18n.hpp del mod (descrito en la §6.3.24 del
/// CLAUDE.md):
///
///  - El idioma NO lo elige el Editor: lo lee del settings.json de SheepGDRequest, clave
///    "AppLanguage" en PascalCase (SettingsService serializa sin naming policy).
///  - Respaldo: idioma actual → inglés → [Llave]. Lo último es a propósito: una llave que
///    falte tiene que VERSE, no quedar tapada por el inglés.
///  - Se lee UNA sola vez, en el primer uso (constructor estático). Cambiar el idioma en
///    SheepGDRequest con el Editor abierto pide reabrirlo. Recargar en caliente dejaría la
///    interfaz a medias: el XAML cambiaría, pero los textos que ya construyó el C# no.
/// </summary>
public static class EditorI18n
{
    /// <summary>"es", "en" o "pt". Nunca vacío: si no se pudo decidir, "en".</summary>
    public static string Idioma { get; }

    // El diccionario del idioma elegido: _es, _en o _pt.
    private static readonly Dictionary<string, string> _actual;

    // Decide el idioma la primera vez que se usa la clase, y lo apunta en la consola.
    static EditorI18n()
    {
        // Los diccionarios de abajo ya existen aquí: los inicializadores estáticos corren
        // antes que el cuerpo de este constructor, estén donde estén en el archivo.
        var (idioma, llaveLog, detalle) = LeerIdioma();

        Idioma = idioma;
        _actual = idioma switch
        {
            "es" => _es,
            "pt" => _pt,
            _ => _en
        };

        Console.WriteLine(Trf(llaveLog, detalle));
    }

    /// <summary>
    /// Decide el idioma y devuelve también qué llave de log lo explica. Son CINCO casos y se
    /// distinguen a propósito: no hay archivo, nadie ha elegido idioma todavía, un código que
    /// el Editor no conoce, no se pudo leer y leído bien. Los cuatro primeros acaban en inglés,
    /// pero el log dice cuál fue.
    /// </summary>
    private static (string idioma, string llaveLog, string detalle) LeerIdioma()
    {
        string ruta = "settings.json";
        try
        {
            ruta = AppPaths.SettingsFile;
            if (!File.Exists(ruta)) return ("en", "Log_I18nNoSettings", ruta);

            // FileShare.ReadWrite: SheepGDRequest puede tenerlo abierto para escribir. Si se lee
            // a medio escribir, el JSON sale roto y se cae en el catch.
            using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(flujo);

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("AppLanguage", out var valor) &&
                valor.ValueKind == JsonValueKind.String)
            {
                string codigo = (valor.GetString() ?? "").Trim().ToLowerInvariant();
                if (codigo is "es" or "en" or "pt") return (codigo, "Log_I18nLoaded", ruta);
                if (codigo.Length > 0) return ("en", "Log_I18nUnknown", codigo);
            }

            // "" es como lo deja SheepGDRequest mientras todavía no ha preguntado el idioma al
            // usuario.
            return ("en", "Log_I18nNotChosen", ruta);
        }
        catch (Exception ex)
        {
            return ("en", "Log_I18nReadError", $"{ruta} — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Texto de una llave: idioma actual → inglés → [Llave].</summary>
    public static string Tr(string clave)
    {
        if (string.IsNullOrEmpty(clave)) return "";
        if (_actual.TryGetValue(clave, out var texto)) return texto;
        if (_en.TryGetValue(clave, out texto)) return texto;
        return $"[{clave}]";
    }

    /// <summary>
    /// Tr + string.Format. Con try/catch por lo mismo que trf() en el mod: una traducción con
    /// las llaves {0} descuadradas no debe tumbar nada. Sale el patrón sin rellenar y un aviso
    /// con la llave exacta que hay que corregir.
    /// </summary>
    /// <param name="clave">La llave de la traducción.</param>
    /// <param name="args">Los valores de {0}, {1}...</param>
    public static string Trf(string clave, params object?[] args)
    {
        string patron = Tr(clave);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, patron, args);
        }
        catch (FormatException)
        {
            // Tr y no Trf: si también fallara esta, no habría recursión.
            Console.WriteLine($"{Tr("Log_I18nBadFormat")} {clave}");
            return patron;
        }
    }

    // ==========================================
    // ESPAÑOL
    // ==========================================
    private static readonly Dictionary<string, string> _es = new()
    {
        // --- Registro ---
        { "Log_I18nLoaded", "[Idioma] Español, leído de {0}" },
        { "Log_I18nBadFormat", "[Idioma] Traducción con el formato roto, revisa la llave:" },

        // --- Valores por defecto del tema ---
        // Se ESCRIBEN en el theme.json, así que viajan en el idioma de quien crea el tema.
        // No afecta a los temas que ya existen: esos traen su nombre y autor guardados.
        { "Default_ThemeName", "Mi tema" },
        { "Default_ThemeAuthor", "Desconocido" },

        // --- Vista previa en el navegador (PreviewServerService) ---
        { "UI_PreviewNoFreePort", "No hay ningún puerto libre entre el {0} y el {1}." },

        // --- Fuentes del sistema (SystemFonts.cs) ---
        // Los motivos son FRAGMENTOS: van dentro del aviso del panel de propiedades, después de
        // "No se puede incrustar:". Por eso en minúscula y sin punto final, en los tres idiomas.
        { "UI_FontReason_NoneChosen", "no hay fuente elegida" },
        { "UI_FontReason_InterMissing", "no se encontró la fuente Inter dentro de Avalonia" },
        { "UI_FontReason_NotFound", "no se encontró el archivo en el sistema" },
        { "UI_FontReason_Substitute", "el sistema solo ofrece una sustituta ({0})" },
        { "UI_FontReason_Unreadable", "el sistema no deja leer el archivo" },
        { "UI_FontReason_EmptyFile", "el archivo está vacío o no se puede leer" },
        { "UI_FontReason_Collection", "es una colección .ttc, es probable que el navegador no la cargue" },
        { "UI_FontReason_UnknownFormat", "formato de archivo desconocido" },
        { "Log_FontExtractError", "[Fuentes] No se pudo extraer '{0}': {1} — {2}" },

        // --- Ventana principal: barra de estado y diálogos (MainWindowViewModel) ---
        { "UI_SaveStatusDirty", "● Sin guardar" },
        { "UI_SaveStatusSaved", "Guardado" },
        { "UI_SaveThemeTitle", "Guardar Tema de SheepGD" },
        { "UI_OpenThemeTitle", "Abrir Tema de SheepGD" },
        { "UI_FilterThemeZip", "Tema ZIP" },
        { "UI_PickFontTitle", "Seleccionar Archivo de Fuente" },
        { "UI_FilterFonts", "Tipografías" },
        { "UI_PickVideoTitle", "Seleccionar Video" },
        { "UI_PickImageTitle", "Seleccionar Imagen" },
        { "UI_PickPosterTitle", "Elegir fotograma de muestra" },
        { "UI_FilterVideos", "Videos" },
        { "UI_FilterImages", "Imágenes" },
        // Los dos añadidos llevan el " — " DENTRO: se pegan detrás de UI_SaveOk.
        { "UI_SaveOk", "¡Guardado! ({0})" },
        { "UI_SaveFontsEmbedded", " — Fuentes incrustadas: {0}." },
        { "UI_SaveFontsNotEmbedded", " — Sin incrustar, solo se verán con la fuente instalada: {0}." },
        { "UI_SaveError", "NO se pudo guardar: {0} — {1}" },
        { "UI_RescueSaved", "Error interno ({0}). Copia de recuperación en: {1} — guarda tu trabajo y reinicia el Editor." },
        { "UI_RescueFailed", "Error interno ({0}) y NO se pudo escribir la copia de recuperación. Guarda como archivo NUEVO y reinicia." },
        { "UI_PreviewOpenError", "No se pudo abrir la vista previa: {0}" },
        { "UI_LoadError", "No se pudo abrir el tema: {0}" },
        { "UI_LoadErrorNoThemeJson", "el ZIP no contiene ningún theme.json, no es un tema de SheepGD." },
        { "UI_ThemeLoaded", "Tema cargado ({0})" },
        { "UI_ThemeJsonReadOnly", "Generado en vivo · solo lectura" },
        { "UI_MultiSelection", "Selección Múltiple ({0})" },
        { "UI_NoSelection", "Sin Selección" },
        { "Log_ThemesFolderError", "[Temas] No se pudo preparar la carpeta de temas: {0}" },

        // --- Desplegable "Tipo de Widget" ---
        // Se eligen por ÍNDICE, nunca por texto, así que traducirlos no rompe nada. Si se prefiere
        // que digan lo mismo que las carpetas (playing/, queue/...), basta con borrar estas cuatro
        // líneas aquí y en portugués: el respaldo al inglés hace el resto.
        { "UI_WidgetType_Playing", "Nivel jugando" },
        { "UI_WidgetType_Queue", "Cola de niveles" },
        { "UI_WidgetType_Notification", "Notificaciones" },
        { "UI_WidgetType_Unified", "Unificado" },

        // --- Datos de demostración del lienzo y la vista previa ---
        // Las mismas palabras que pinta engine.js en OBS, para que el lienzo y el widget coincidan.
        { "Preview_RequestsEnabled", "Activadas" },
        { "Preview_RequestsDisabled", "Desactivadas" },
        // Copiadas de las Notif_ de SheepGDRequest. Los marcadores <USUARIO> y <NIVEL> NO se
        // traducen en ningún idioma: MainWindowViewModel los sustituye con .Replace.
        { "Preview_NotifNotAnId", "❗ <USUARIO> ignorado: <NIVEL> no es un ID." },
        { "Preview_NotifUserLimit", "❗ <USUARIO> alcanzó su límite de niveles en la fila." },
        { "Preview_NotifQueueFull", "⛔ <USUARIO> ignorado: La lista alcanzó el límite máximo de 10 niveles." },
        { "Preview_NotifTooHard", "⛔ Nivel <NIVEL> de <USUARIO> bloqueado: Supera la dificultad máxima permitida (TuSuegraDemon)" },
        { "Preview_NotifTimeExpired", "⏱️ El tiempo del nivel actual expiró." },
        { "Preview_NotifNotFound", "❓ El nivel <NIVEL> de <USUARIO> no existe en GD." },
        { "Preview_NotifAdded", "✅ Nivel <NIVEL> de <USUARIO> añadido con éxito." },
        { "Preview_NotifSkipping", "⏭️ Nivel en pantalla expirado, saltando..." },
        { "Preview_NotifUnrated", "⛔ Nivel <NIVEL> de <USUARIO> bloqueado: Nivel sin Rate." },
        { "Preview_NotifNext", "⏭️ Pasando al siguiente nivel..." },
        { "Preview_NotifQueueEmpty", "📭 La lista de peticiones está vacía." },
        { "Preview_NotifConnectionLost", "⚠️ Conexión perdida. Reintentando..." },
        { "Preview_NotifPriority", "⬆️ Nivel <NIVEL> de <USUARIO> subido de prioridad." },
        { "Preview_NotifRequestsOpened", "✅ Sistema: Se han ABIERTO las solicitudes de niveles." },
        { "Preview_NotifRequestsPaused", "⛔ Sistema: Se han PAUSADO las solicitudes de niveles." },
        { "Preview_NotifRequestIgnored", "Petición de <USUARIO> ignorada: Lista llena." },

        // --- Objetos y panel de propiedades (WidgetItemViewModel) ---
        { "Default_ObjectName", "Nuevo Objeto" },
        { "UI_BrowseFontFile", "Buscar archivo .ttf / .otf ..." },
        // UI_AddCustomFont es la casilla del panel Y lo que cita el aviso de abajo: una sola
        // llave para que el aviso nombre siempre la casilla tal como se ve.
        { "UI_AddCustomFont", "Agregar fuente personalizada" },
        { "UI_FontWillEmbed", "Se incrustará en el tema al guardar." },
        { "UI_FontCannotEmbed", "No se puede incrustar: {0}. Solo se verá en dispositivos que tengan esta fuente instalada. Para que viaje con el tema, usa «{1}»." },
        { "UI_NoVideo", "Sin video" },
        { "UI_NoImage", "Sin imagen" },
        { "UI_NoPoster", "Sin fotograma" },
        { "UI_DefaultImage", "Imagen por defecto" },
        { "UI_SourceFile", "Archivo de Origen" },
        { "Log_FontRegisterError", "[Fuentes] No se pudo registrar '{0}': {1}" },

        // --- Aviso de cambios sin guardar, al cerrar o abrir otro tema (MainWindow.axaml.cs) ---
        { "UI_UnsavedTitle", "Cambios sin guardar" },
        { "UI_UnsavedMessage", "El tema tiene cambios sin guardar.\n¿Quieres guardarlos antes de cerrar?" },
        { "UI_BtnCancel", "Cancelar" },
        { "UI_BtnExitWithoutSaving", "Salir sin guardar" },
        { "UI_UnsavedOpenMessage", "El tema tiene cambios sin guardar.\n¿Quieres guardarlos antes de abrir otro?" },
        { "UI_BtnOpenWithoutSaving", "Abrir sin guardar" },
        { "UI_BtnSave", "Guardar" },

        // --- Editor de código, lista de archivos (CodeEditorSideView.axaml) ---
        { "UI_Code_BaseFiles", "ARCHIVOS BASE" },
        { "UI_Code_ThemeJson", "theme.json (Lectura)" },
        { "UI_Code_HtmlCanvases", "LIENZOS HTML" },
        { "UI_Code_IndexUnified", "index.html (Unificado)" },

        // --- Caja de herramientas (ToolboxView.axaml) ---
        { "UI_CanvasSize", "Tamaño del lienzo" },
        { "UI_Tb_Basics", "BÁSICOS" },
        { "UI_Tb_PredefinedImages", "Imágenes Predefinidas" },
        { "UI_Tb_Notifications", "NOTIFICACIONES" },
        { "UI_Tb_LevelData", "DATOS DEL NIVEL" },
        { "UI_Tb_StreamMeta", "METADATOS DEL STREAM" },
        { "UI_Tb_Stats", "ESTADÍSTICAS Y DIFICULTAD" },
        { "UI_Tb_ExternalLists", "LISTAS EXTERNAS" },

        // --- Tipos de objeto ---
        // La llave es UI_Obj_ + el nombre EXACTO del enum WidgetObjectType. Las usan la caja de
        // herramientas y AddWidgetObject, que pone este texto como nombre del objeto nuevo: no
        // renombrar una llave sin renombrar el valor del enum.
        { "UI_Obj_Text", "Texto" },
        { "UI_Obj_Image", "Imagen" },
        { "UI_Obj_Video", "Video" },
        { "UI_Obj_NotificationBox", "Cuadro de Notificación" },
        { "UI_Obj_LevelBackground", "Fondo del Nivel" },
        { "UI_Obj_LevelName", "Nombre del Nivel" },
        { "UI_Obj_CreatorName", "Creador" },
        { "UI_Obj_LevelId", "ID del Nivel" },
        { "UI_Obj_SentBy", "Enviado por" },
        { "UI_Obj_PlatformIcon", "Ícono de Plataforma" },
        { "UI_Obj_PlatformName", "Nombre de Plataforma" },
        { "UI_Obj_RequestStatus", "Estado (Request)" },
        { "UI_Obj_Timer", "Temporizador" },
        { "UI_Obj_RateFace", "Rate Face" },
        { "UI_Obj_RateText", "Rate Text" },
        { "UI_Obj_Stars", "Estrellas/Lunas" },
        { "UI_Obj_Downloads", "Descargas" },
        { "UI_Obj_LikeIcon", "Ícono de Like/Dislike" },
        { "UI_Obj_StarIcon", "Ícono de Estrella/Luna" },
        { "UI_Obj_LikesAndDislikes", "Likes / Dislikes" },
        { "UI_Obj_Orbs", "Orbes" },
        { "UI_Obj_Duration", "Duración" },
        { "UI_Obj_AredlPos", "Posición en AREDL" },
        { "UI_Obj_GddlRate", "GDDL Rate" },

        // --- Panel de propiedades (PropertiesView.axaml) ---
        { "UI_Prop_CurrentLayer", "Capa actual: " },
        { "UI_Prop_PreviewOn", "Vista Previa: ON" },
        { "UI_Prop_PreviewOff", "Vista Previa: OFF" },
        { "UI_Prop_TextContent", "Contenido del texto" },
        { "UI_Prop_SystemFonts", "Fuentes del sistema" },
        { "UI_Prop_Alignment", "Alineación" },
        { "UI_AlignLeft", "Izquierda" },
        { "UI_AlignCenter", "Centro" },
        { "UI_AlignRight", "Derecha" },
        { "UI_AlignJustify", "Justificar" },
        { "UI_Prop_VerticalAlignment", "Alineación Vertical" },
        { "UI_AlignTop", "Arriba" },
        { "UI_AlignBottom", "Abajo" },
        { "UI_Prop_TextPadding", "Margen Interno del Cuadro" },
        { "UI_Prop_WordWrap", "Salto de línea automático" },
        { "UI_Prop_Outline", "Contorno" },
        { "UI_Prop_OutlineHint", "El grosor va en píxeles del widget, igual que el tamaño de fuente." },
        { "UI_Prop_RequesterColor", "Color del Espectador" },
        { "UI_Prop_LevelDataColor", "Color del Nivel/ID" },
        { "UI_Prop_BackgroundType", "Tipo de fondo" },
        { "UI_BgColor", "Color" },
        { "UI_BgTransparent", "Transparente" },
        { "UI_Prop_Blur", "Desenfoque blur" },
        { "UI_Prop_SelectFile", "Seleccionar Archivo..." },
        { "UI_Prop_LevelBgHint", "Se ve cuando el nivel no tiene miniatura, y entre peticiones." },
        { "UI_Prop_LevelBgHintEmpty", "Sin imagen por defecto: si el nivel no tiene miniatura, y entre peticiones, no se ve nada." },
        { "UI_Prop_RemoveDefaultImage", "Quitar imagen por defecto" },
        { "UI_StretchKeep", "Mantener Proporción" },
        { "UI_StretchFill", "Rellenar (Estirar)" },
        { "UI_StretchZoom", "Rellenar (Zoom)" },
        { "UI_Prop_VideoLoop", "Reproducir en bucle" },
        { "UI_Prop_VideoMute", "Silenciar" },
        { "UI_Prop_Poster", "Fotograma de muestra" },
        { "UI_Prop_PickPoster", "Elegir fotograma..." },
        { "UI_Prop_RemovePoster", "Quitar fotograma" },
        { "UI_Prop_PosterHint", "El lienzo dibuja este fotograma o el nombre del archivo si no eliges ninguno. En OBS y en la vista previa puedes ver el video reproduciéndose." },
        { "UI_Prop_Halo", "Halo de rate" },
        { "UI_Prop_ShowHalo", "Mostrar halo del nivel" },
        { "UI_HaloAuto", "Automático (rota en vista previa)" },
        { "UI_HaloNone", "Ninguno" },
        { "UI_Prop_HaloSize", "Tamaño del halo" },
        { "UI_Prop_HaloHint", "En el directo el halo lo decide el nivel real; aquí eliges cuál previsualizar." },
        { "UI_Prop_Overlay", "Color Superficial (Filtro)" },
        { "UI_Prop_Opacity", "Opacidad" },
        { "UI_Prop_Roundness", "Redondez del borde" },
        { "UI_Prop_NotifDuration", "Duración de notificación (Segundos)" },
        { "UI_Prop_Size", "Tamaño" },
        { "UI_Prop_Position", "Posición" },
        { "UI_Prop_LayerNumber", "Número de capa" },

        // --- Ventana principal (MainWindow.axaml) ---
        { "UI_Tip_AddObject", "Agregar Objeto" },
        { "UI_Tip_DeleteObject", "Eliminar Objeto" },
        { "UI_Tip_EditProperties", "Editar Propiedades" },
        { "UI_Tip_CodeEditor", "Editor de Código" },
        { "UI_Tip_SaveAs", "Guardar como... (.zip)\nCtrl+S guarda directo en el último archivo" },
        { "UI_Tip_OpenTheme", "Abrir Tema" },
        { "UI_WidgetTypeLabel", "Tipo de Widget" },
        { "UI_Queue_CardSettings", "Configuración de Tarjetas" },
        { "UI_Queue_MaxItems", "Máximo a mostrar" },
        { "UI_Queue_Direction", "Dirección" },
        { "UI_DirVertical", "Vertical" },
        { "UI_DirHorizontal", "Horizontal" },
        { "UI_Queue_Spacing", "Espaciado (Margen)" },
        { "UI_ShowGhostCards", "Mostrar tarjetas fantasma" },
        { "UI_Notif_Settings", "AJUSTES DE NOTIFICACIONES" },
        { "UI_Notif_MaxCards", "Máximo de tarjetas:" },
        { "UI_Notif_ListDirection", "Dirección de la lista:" },
        { "UI_Notif_CardSpacing", "Espaciado entre tarjetas:" },
        { "UI_Notif_TimerMode", "Modo de Temporizador:" },
        { "UI_Notif_Simultaneous", "Simultáneo (Todas a la vez)" },
        { "UI_Notif_Cascade", "En Cascada (Una por una)" },
        { "UI_Tip_Undo", "Deshacer (Ctrl+Z)" },
        { "UI_Tip_Redo", "Rehacer (Ctrl+Y)" },
        { "UI_Tip_ZoomOut", "Alejar" },
        { "UI_Tip_ZoomIn", "Acercar" },
        { "UI_BtnPreview", "Vista previa ↗" },
        { "UI_Tip_Preview", "Abre el widget entero en el navegador, con datos de ejemplo. Es el index.html real del tema." },
        { "UI_AuthorLabel", "Autor:" },
        { "UI_ThemeLabel", "Tema:" },
        { "UI_Menu_PasteObjects", "Pegar Objetos (Ctrl+V)" },
        { "UI_Menu_LayerUp", "Subir una capa (Ctrl+Up)" },
        { "UI_Menu_LayerDown", "Bajar una capa (Ctrl+Down)" },
        { "UI_Menu_Link", "Vincular (Ctrl+L)" },
        { "UI_Menu_Unlink", "Desvincular (Ctrl+Alt+L)" },
        { "UI_Menu_Duplicate", "Duplicar (Ctrl+D)" },
        { "UI_Menu_Copy", "Copiar (Ctrl+C)" },
        { "UI_Menu_Paste", "Pegar (Ctrl+V)" },
        { "UI_Menu_PasteProperties", "Pegar Propiedades (Ctrl+Alt+V)" },
        { "UI_PlaysInObs", "Se reproduce en OBS" },
        { "UI_Tip_CropTop", "Recortar Arriba" },
        { "UI_Tip_CropBottom", "Recortar Abajo" },
        { "UI_Tip_CropLeft", "Recortar Izquierda" },
        { "UI_Tip_CropRight", "Recortar Derecha" },
        { "UI_Tip_Scale", "Escalar" },
        { "UI_Tip_Rotate", "Rotar Libremente" },
    };

    // ==========================================
    // INGLÉS (el respaldo: tiene que tener TODAS las llaves)
    // ==========================================
    private static readonly Dictionary<string, string> _en = new()
    {
        // --- Registro ---
        { "Log_I18nLoaded", "[Language] English, read from {0}" },
        { "Log_I18nBadFormat", "[Language] Translation with a broken format, check key:" },

        // Estas cuatro solo existen en inglés, y no es un olvido: salen justo cuando no se pudo
        // decidir el idioma, o sea, siempre con el Editor en inglés.
        { "Log_I18nNoSettings", "[Language] No settings.json at {0}. Using English." },
        { "Log_I18nNotChosen", "[Language] No language chosen yet in SheepGDRequest ({0}). Using English." },
        { "Log_I18nUnknown", "[Language] '{0}' is not a language the Editor knows. Using English." },
        { "Log_I18nReadError", "[Language] Could not read the language ({0}). Using English." },

        // --- Valores por defecto del tema ---
        { "Default_ThemeName", "My theme" },
        { "Default_ThemeAuthor", "Unknown" },

        // --- Vista previa en el navegador (PreviewServerService) ---
        { "UI_PreviewNoFreePort", "There is no free port between {0} and {1}." },

        // --- Fuentes del sistema (SystemFonts.cs) ---
        { "UI_FontReason_NoneChosen", "no font chosen" },
        { "UI_FontReason_InterMissing", "Inter was not found inside Avalonia" },
        { "UI_FontReason_NotFound", "its file was not found on the system" },
        { "UI_FontReason_Substitute", "the system only offers a substitute ({0})" },
        { "UI_FontReason_Unreadable", "the system does not allow reading its file" },
        { "UI_FontReason_EmptyFile", "the file is empty or cannot be read" },
        { "UI_FontReason_Collection", "it is a .ttc collection, which the browser is not guaranteed to load" },
        { "UI_FontReason_UnknownFormat", "unknown file format" },
        { "Log_FontExtractError", "[Fonts] Could not extract '{0}': {1} — {2}" },

        // --- Ventana principal: barra de estado y diálogos (MainWindowViewModel) ---
        { "UI_SaveStatusDirty", "● Unsaved" },
        { "UI_SaveStatusSaved", "Saved" },
        { "UI_SaveThemeTitle", "Save SheepGD Theme" },
        { "UI_OpenThemeTitle", "Open SheepGD Theme" },
        { "UI_FilterThemeZip", "ZIP Theme" },
        { "UI_PickFontTitle", "Select Font File" },
        { "UI_FilterFonts", "Fonts" },
        { "UI_PickVideoTitle", "Select Video" },
        { "UI_PickImageTitle", "Select Image" },
        { "UI_PickPosterTitle", "Choose preview frame" },
        { "UI_FilterVideos", "Videos" },
        { "UI_FilterImages", "Images" },
        { "UI_SaveOk", "Saved successfully! ({0})" },
        { "UI_SaveFontsEmbedded", " — Embedded fonts: {0}." },
        { "UI_SaveFontsNotEmbedded", " — Not embedded, they will only show where the font is installed: {0}." },
        { "UI_SaveError", "Could NOT save: {0} — {1}" },
        { "UI_RescueSaved", "Internal error ({0}). Rescue copy at: {1} — save your work and restart the Editor." },
        { "UI_RescueFailed", "Internal error ({0}) and the rescue copy could NOT be written. Save as a NEW file and restart." },
        { "UI_PreviewOpenError", "Could not open the preview: {0}" },
        { "UI_LoadError", "Could not open the theme: {0}" },
        { "UI_LoadErrorNoThemeJson", "the ZIP has no theme.json, so it is not a SheepGD theme." },
        { "UI_ThemeLoaded", "Theme loaded ({0})" },
        { "UI_ThemeJsonReadOnly", "Generated live · read-only" },
        { "UI_MultiSelection", "Multiple Selection ({0})" },
        { "UI_NoSelection", "No Selection" },
        { "Log_ThemesFolderError", "[Themes] Could not prepare the themes folder: {0}" },

        // --- Desplegable "Tipo de Widget" ---
        { "UI_WidgetType_Playing", "Playing" },
        { "UI_WidgetType_Queue", "Queue" },
        { "UI_WidgetType_Notification", "Notification" },
        { "UI_WidgetType_Unified", "Unified" },

        // --- Datos de demostración del lienzo y la vista previa ---
        { "Preview_RequestsEnabled", "Enabled" },
        { "Preview_RequestsDisabled", "Disabled" },
        { "Preview_NotifNotAnId", "❗ <USUARIO> ignored: <NIVEL> is not an ID." },
        { "Preview_NotifUserLimit", "❗ <USUARIO> reached their queue limit." },
        { "Preview_NotifQueueFull", "⛔ <USUARIO> ignored: Queue reached max limit of 10 levels." },
        { "Preview_NotifTooHard", "⛔ Level <NIVEL> by <USUARIO> blocked: Exceeds maximum (YourMotherInLawDemon)" },
        { "Preview_NotifTimeExpired", "⏱️ The current level's time has expired." },
        { "Preview_NotifNotFound", "❓ Level <NIVEL> by <USUARIO> does not exist in GD." },
        { "Preview_NotifAdded", "✅ Level <NIVEL> by <USUARIO> added successfully." },
        { "Preview_NotifSkipping", "⏭️ Level on screen expired, skipping..." },
        { "Preview_NotifUnrated", "⛔ Level <NIVEL> by <USUARIO> blocked: Level has no Rate/Stars." },
        { "Preview_NotifNext", "⏭️ Skipping to the next level..." },
        { "Preview_NotifQueueEmpty", "📭 The request queue is empty." },
        { "Preview_NotifConnectionLost", "⚠️ Connection lost. Retrying..." },
        { "Preview_NotifPriority", "⬆️ Level <NIVEL> by <USUARIO> moved to priority." },
        { "Preview_NotifRequestsOpened", "✅ System: Level requests are now OPEN." },
        { "Preview_NotifRequestsPaused", "⛔ System: Level requests have been PAUSED." },
        { "Preview_NotifRequestIgnored", "Request from <USUARIO> ignored: Queue full." },

        // --- Objetos y panel de propiedades (WidgetItemViewModel) ---
        { "Default_ObjectName", "New Object" },
        { "UI_BrowseFontFile", "Browse .ttf / .otf file..." },
        { "UI_AddCustomFont", "Add custom font" },
        { "UI_FontWillEmbed", "It will be embedded in the theme when saving, so it looks the same on any computer." },
        { "UI_FontCannotEmbed", "Cannot be embedded: {0}. It will only show on computers that have this font installed. To make it travel with the theme, use “{1}”." },
        { "UI_NoVideo", "No video" },
        { "UI_NoImage", "No image" },
        { "UI_NoPoster", "No frame" },
        { "UI_DefaultImage", "Default image" },
        { "UI_SourceFile", "Source File" },
        { "Log_FontRegisterError", "[Fonts] Could not register '{0}': {1}" },

        // --- Aviso de cambios sin guardar, al cerrar o abrir otro tema (MainWindow.axaml.cs) ---
        { "UI_UnsavedTitle", "Unsaved changes" },
        { "UI_UnsavedMessage", "The theme has unsaved changes.\nDo you want to save them before closing?" },
        { "UI_BtnCancel", "Cancel" },
        { "UI_BtnExitWithoutSaving", "Exit without saving" },
        { "UI_UnsavedOpenMessage", "The theme has unsaved changes.\nDo you want to save them before opening another one?" },
        { "UI_BtnOpenWithoutSaving", "Open without saving" },
        { "UI_BtnSave", "Save" },

        // --- Editor de código, lista de archivos (CodeEditorSideView.axaml) ---
        { "UI_Code_BaseFiles", "BASE FILES" },
        { "UI_Code_ThemeJson", "theme.json (Read-only)" },
        { "UI_Code_HtmlCanvases", "HTML CANVASES" },
        { "UI_Code_IndexUnified", "index.html (Unified)" },

        // --- Caja de herramientas (ToolboxView.axaml) ---
        { "UI_CanvasSize", "Canvas size" },
        { "UI_Tb_Basics", "BASICS" },
        { "UI_Tb_PredefinedImages", "Predefined Images" },
        { "UI_Tb_Notifications", "NOTIFICATIONS" },
        { "UI_Tb_LevelData", "LEVEL DATA" },
        { "UI_Tb_StreamMeta", "STREAM METADATA" },
        { "UI_Tb_Stats", "STATS AND DIFFICULTY" },
        { "UI_Tb_ExternalLists", "EXTERNAL LISTS" },

        // --- Tipos de objeto ---
        // La llave es UI_Obj_ + el nombre EXACTO del enum WidgetObjectType. Las usan la caja de
        // herramientas y AddWidgetObject, que pone este texto como nombre del objeto nuevo: no
        // renombrar una llave sin renombrar el valor del enum.
        { "UI_Obj_Text", "Text" },
        { "UI_Obj_Image", "Image" },
        { "UI_Obj_Video", "Video" },
        { "UI_Obj_NotificationBox", "Notification Box" },
        { "UI_Obj_LevelBackground", "Level Background" },
        { "UI_Obj_LevelName", "Level Name" },
        { "UI_Obj_CreatorName", "Creator" },
        { "UI_Obj_LevelId", "Level ID" },
        { "UI_Obj_SentBy", "Sent by" },
        { "UI_Obj_PlatformIcon", "Platform Icon" },
        { "UI_Obj_PlatformName", "Platform Name" },
        { "UI_Obj_RequestStatus", "Status (Request)" },
        { "UI_Obj_Timer", "Timer" },
        { "UI_Obj_RateFace", "Rate Face" },
        { "UI_Obj_RateText", "Rate Text" },
        { "UI_Obj_Stars", "Stars/Moons" },
        { "UI_Obj_Downloads", "Downloads" },
        { "UI_Obj_LikeIcon", "Like/Dislike Icon" },
        { "UI_Obj_StarIcon", "Star/Moon Icon" },
        { "UI_Obj_LikesAndDislikes", "Likes / Dislikes" },
        { "UI_Obj_Orbs", "Orbs" },
        { "UI_Obj_Duration", "Duration" },
        { "UI_Obj_AredlPos", "AREDL Position" },
        { "UI_Obj_GddlRate", "GDDL Rate" },

        // --- Panel de propiedades (PropertiesView.axaml) ---
        { "UI_Prop_CurrentLayer", "Current layer: " },
        { "UI_Prop_PreviewOn", "Preview: ON" },
        { "UI_Prop_PreviewOff", "Preview: OFF" },
        { "UI_Prop_TextContent", "Text content" },
        { "UI_Prop_SystemFonts", "System fonts" },
        { "UI_Prop_Alignment", "Alignment" },
        { "UI_AlignLeft", "Left" },
        { "UI_AlignCenter", "Center" },
        { "UI_AlignRight", "Right" },
        { "UI_AlignJustify", "Justify" },
        { "UI_Prop_VerticalAlignment", "Vertical Alignment" },
        { "UI_AlignTop", "Top" },
        { "UI_AlignBottom", "Bottom" },
        { "UI_Prop_TextPadding", "Box Inner Padding" },
        { "UI_Prop_WordWrap", "Automatic line wrap" },
        { "UI_Prop_Outline", "Outline" },
        { "UI_Prop_OutlineHint", "The thickness is in widget pixels, just like the font size." },
        { "UI_Prop_RequesterColor", "Viewer Color" },
        { "UI_Prop_LevelDataColor", "Level/ID Color" },
        { "UI_Prop_BackgroundType", "Background type" },
        { "UI_BgColor", "Color" },
        { "UI_BgTransparent", "Transparent" },
        { "UI_Prop_Blur", "Blur" },
        { "UI_Prop_SelectFile", "Select File..." },
        { "UI_Prop_LevelBgHint", "Shown when the level has no thumbnail, and between requests." },
        { "UI_Prop_LevelBgHintEmpty", "No default image: when the level has no thumbnail, and between requests, nothing is shown." },
        { "UI_Prop_RemoveDefaultImage", "Remove default image" },
        { "UI_StretchKeep", "Keep Aspect Ratio" },
        { "UI_StretchFill", "Fill (Stretch)" },
        { "UI_StretchZoom", "Fill (Zoom)" },
        { "UI_Prop_VideoLoop", "Loop playback" },
        { "UI_Prop_VideoMute", "Mute" },
        { "UI_Prop_Poster", "Preview frame" },
        { "UI_Prop_PickPoster", "Choose frame..." },
        { "UI_Prop_RemovePoster", "Remove frame" },
        { "UI_Prop_PosterHint", "The canvas draws this frame, or the file name if you don't choose one. In OBS and in the preview you can see the video playing." },
        { "UI_Prop_Halo", "Rate halo" },
        { "UI_Prop_ShowHalo", "Show the level's halo" },
        { "UI_HaloAuto", "Automatic (rotates in preview)" },
        { "UI_HaloNone", "None" },
        { "UI_Prop_HaloSize", "Halo size" },
        { "UI_Prop_HaloHint", "Live, the halo is decided by the real level; here you choose which one to preview." },
        { "UI_Prop_Overlay", "Overlay Color (Filter)" },
        { "UI_Prop_Opacity", "Opacity" },
        { "UI_Prop_Roundness", "Corner roundness" },
        { "UI_Prop_NotifDuration", "Notification duration (Seconds)" },
        { "UI_Prop_Size", "Size" },
        { "UI_Prop_Position", "Position" },
        { "UI_Prop_LayerNumber", "Layer number" },

        // --- Ventana principal (MainWindow.axaml) ---
        { "UI_Tip_AddObject", "Add Object" },
        { "UI_Tip_DeleteObject", "Delete Object" },
        { "UI_Tip_EditProperties", "Edit Properties" },
        { "UI_Tip_CodeEditor", "Code Editor" },
        { "UI_Tip_SaveAs", "Save as... (.zip)\nCtrl+S saves straight to the last file" },
        { "UI_Tip_OpenTheme", "Open Theme" },
        { "UI_WidgetTypeLabel", "Widget Type" },
        { "UI_Queue_CardSettings", "Card Settings" },
        { "UI_Queue_MaxItems", "Maximum to show" },
        { "UI_Queue_Direction", "Direction" },
        { "UI_DirVertical", "Vertical" },
        { "UI_DirHorizontal", "Horizontal" },
        { "UI_Queue_Spacing", "Spacing (Margin)" },
        { "UI_ShowGhostCards", "Show ghost cards" },
        { "UI_Notif_Settings", "NOTIFICATION SETTINGS" },
        { "UI_Notif_MaxCards", "Maximum cards:" },
        { "UI_Notif_ListDirection", "List direction:" },
        { "UI_Notif_CardSpacing", "Spacing between cards:" },
        { "UI_Notif_TimerMode", "Timer Mode:" },
        { "UI_Notif_Simultaneous", "Simultaneous (All at once)" },
        { "UI_Notif_Cascade", "Cascade (One by one)" },
        { "UI_Tip_Undo", "Undo (Ctrl+Z)" },
        { "UI_Tip_Redo", "Redo (Ctrl+Y)" },
        { "UI_Tip_ZoomOut", "Zoom out" },
        { "UI_Tip_ZoomIn", "Zoom in" },
        { "UI_BtnPreview", "Preview ↗" },
        { "UI_Tip_Preview", "Opens the whole widget in the browser, with sample data. It is the theme's real index.html." },
        { "UI_AuthorLabel", "Author:" },
        { "UI_ThemeLabel", "Theme:" },
        { "UI_Menu_PasteObjects", "Paste Objects (Ctrl+V)" },
        { "UI_Menu_LayerUp", "Bring forward (Ctrl+Up)" },
        { "UI_Menu_LayerDown", "Send backward (Ctrl+Down)" },
        { "UI_Menu_Link", "Link (Ctrl+L)" },
        { "UI_Menu_Unlink", "Unlink (Ctrl+Alt+L)" },
        { "UI_Menu_Duplicate", "Duplicate (Ctrl+D)" },
        { "UI_Menu_Copy", "Copy (Ctrl+C)" },
        { "UI_Menu_Paste", "Paste (Ctrl+V)" },
        { "UI_Menu_PasteProperties", "Paste Properties (Ctrl+Alt+V)" },
        { "UI_PlaysInObs", "Plays in OBS" },
        { "UI_Tip_CropTop", "Crop Top" },
        { "UI_Tip_CropBottom", "Crop Bottom" },
        { "UI_Tip_CropLeft", "Crop Left" },
        { "UI_Tip_CropRight", "Crop Right" },
        { "UI_Tip_Scale", "Scale" },
        { "UI_Tip_Rotate", "Rotate Freely" },
    };

    // ==========================================
    // PORTUGUÉS
    // ==========================================
    private static readonly Dictionary<string, string> _pt = new()
    {
        // --- Registro ---
        { "Log_I18nLoaded", "[Idioma] Português, lido de {0}" },
        { "Log_I18nBadFormat", "[Idioma] Tradução com o formato quebrado, verifique a chave:" },

        // --- Valores por defecto del tema ---
        { "Default_ThemeName", "Meu tema" },
        { "Default_ThemeAuthor", "Desconhecido" },

        // --- Vista previa en el navegador (PreviewServerService) ---
        { "UI_PreviewNoFreePort", "Não há nenhuma porta livre entre {0} e {1}." },

        // --- Fuentes del sistema (SystemFonts.cs) ---
        { "UI_FontReason_NoneChosen", "nenhuma fonte escolhida" },
        { "UI_FontReason_InterMissing", "a Inter não foi encontrada dentro do Avalonia" },
        { "UI_FontReason_NotFound", "o arquivo dela não foi encontrado no sistema" },
        { "UI_FontReason_Substitute", "o sistema só oferece uma substituta ({0})" },
        { "UI_FontReason_Unreadable", "o sistema não permite ler o arquivo dela" },
        { "UI_FontReason_EmptyFile", "o arquivo está vazio ou não pode ser lido" },
        { "UI_FontReason_Collection", "é uma coleção .ttc, que o navegador não garante carregar" },
        { "UI_FontReason_UnknownFormat", "formato de arquivo desconhecido" },
        { "Log_FontExtractError", "[Fontes] Não foi possível extrair '{0}': {1} — {2}" },

        // --- Ventana principal: barra de estado y diálogos (MainWindowViewModel) ---
        { "UI_SaveStatusDirty", "● Não salvo" },
        { "UI_SaveStatusSaved", "Salvo" },
        { "UI_SaveThemeTitle", "Salvar Tema do SheepGD" },
        { "UI_OpenThemeTitle", "Abrir Tema do SheepGD" },
        { "UI_FilterThemeZip", "Tema ZIP" },
        { "UI_PickFontTitle", "Selecionar Arquivo de Fonte" },
        { "UI_FilterFonts", "Fontes" },
        { "UI_PickVideoTitle", "Selecionar Vídeo" },
        { "UI_PickImageTitle", "Selecionar Imagem" },
        { "UI_PickPosterTitle", "Escolher quadro de amostra" },
        { "UI_FilterVideos", "Vídeos" },
        { "UI_FilterImages", "Imagens" },
        { "UI_SaveOk", "Salvo com sucesso! ({0})" },
        { "UI_SaveFontsEmbedded", " — Fontes incorporadas: {0}." },
        { "UI_SaveFontsNotEmbedded", " — Não incorporadas, só aparecerão onde a fonte estiver instalada: {0}." },
        { "UI_SaveError", "NÃO foi possível salvar: {0} — {1}" },
        { "UI_RescueSaved", "Erro interno ({0}). Cópia de resgate em: {1} — salve seu trabalho e reinicie o Editor." },
        { "UI_RescueFailed", "Erro interno ({0}) e NÃO foi possível gravar a cópia de resgate. Salve como um arquivo NOVO e reinicie." },
        { "UI_PreviewOpenError", "Não foi possível abrir a pré-visualização: {0}" },
        { "UI_LoadError", "Não foi possível abrir o tema: {0}" },
        { "UI_LoadErrorNoThemeJson", "o ZIP não contém nenhum theme.json, então não é um tema do SheepGD." },
        { "UI_ThemeLoaded", "Tema carregado ({0})" },
        { "UI_ThemeJsonReadOnly", "Gerado ao vivo · somente leitura" },
        { "UI_MultiSelection", "Seleção Múltipla ({0})" },
        { "UI_NoSelection", "Sem Seleção" },
        { "Log_ThemesFolderError", "[Temas] Não foi possível preparar a pasta de temas: {0}" },

        // --- Desplegable "Tipo de Widget" ---
        { "UI_WidgetType_Playing", "Nível jogando" },
        { "UI_WidgetType_Queue", "Fila de níveis" },
        { "UI_WidgetType_Notification", "Notificações" },
        { "UI_WidgetType_Unified", "Unificado" },

        // --- Datos de demostración del lienzo y la vista previa ---
        { "Preview_RequestsEnabled", "Ativadas" },
        { "Preview_RequestsDisabled", "Desativadas" },
        { "Preview_NotifNotAnId", "❗ <USUARIO> ignorado: <NIVEL> não é um ID." },
        { "Preview_NotifUserLimit", "❗ <USUARIO> atingiu seu limite na fila." },
        { "Preview_NotifQueueFull", "⛔ <USUARIO> ignorado: A fila atingiu o limite máximo de 10 níveis." },
        { "Preview_NotifTooHard", "⛔ Nível <NIVEL> de <USUARIO> bloqueado: Excede o máximo (SuaSograDemon)" },
        { "Preview_NotifTimeExpired", "⏱️ O tempo do nível atual expirou." },
        { "Preview_NotifNotFound", "❓ O nível <NIVEL> de <USUARIO> não existe no GD." },
        { "Preview_NotifAdded", "✅ Nível <NIVEL> de <USUARIO> adicionado com sucesso." },
        { "Preview_NotifSkipping", "⏭️ Nível na tela expirou, pulando..." },
        { "Preview_NotifUnrated", "⛔ Nível <NIVEL> de <USUARIO> bloqueado: Nível sem Rate/Estrelas." },
        { "Preview_NotifNext", "⏭️ Pulando para o próximo nível..." },
        { "Preview_NotifQueueEmpty", "📭 A lista de pedidos está vazia." },
        { "Preview_NotifConnectionLost", "⚠️ Conexão perdida. Tentando novamente..." },
        { "Preview_NotifPriority", "⬆️ Nível <NIVEL> de <USUARIO> movido para prioridade." },
        { "Preview_NotifRequestsOpened", "✅ Sistema: As solicitações de níveis foram ABERTAS." },
        { "Preview_NotifRequestsPaused", "⛔ Sistema: As solicitações de níveis foram PAUSADAS." },
        { "Preview_NotifRequestIgnored", "Pedido de <USUARIO> ignorado: Fila cheia." },

        // --- Objetos y panel de propiedades (WidgetItemViewModel) ---
        { "Default_ObjectName", "Novo Objeto" },
        { "UI_BrowseFontFile", "Procurar arquivo .ttf / .otf ..." },
        { "UI_AddCustomFont", "Adicionar fonte personalizada" },
        { "UI_FontWillEmbed", "Será incorporada ao tema ao salvar, para aparecer igual em qualquer computador." },
        { "UI_FontCannotEmbed", "Não pode ser incorporada: {0}. Só aparecerá em computadores que tenham esta fonte instalada. Para que ela viaje com o tema, use “{1}”." },
        { "UI_NoVideo", "Sem vídeo" },
        { "UI_NoImage", "Sem imagem" },
        { "UI_NoPoster", "Sem quadro" },
        { "UI_DefaultImage", "Imagem padrão" },
        { "UI_SourceFile", "Arquivo de Origem" },
        { "Log_FontRegisterError", "[Fontes] Não foi possível registrar '{0}': {1}" },

        // --- Aviso de cambios sin guardar, al cerrar o abrir otro tema (MainWindow.axaml.cs) ---
        { "UI_UnsavedTitle", "Alterações não salvas" },
        { "UI_UnsavedMessage", "O tema tem alterações não salvas.\nDeseja salvá-las antes de fechar?" },
        { "UI_BtnCancel", "Cancelar" },
        { "UI_BtnExitWithoutSaving", "Sair sem salvar" },
        { "UI_UnsavedOpenMessage", "O tema tem alterações não salvas.\nDeseja salvá-las antes de abrir outro?" },
        { "UI_BtnOpenWithoutSaving", "Abrir sem salvar" },
        { "UI_BtnSave", "Salvar" },

        // --- Editor de código, lista de archivos (CodeEditorSideView.axaml) ---
        { "UI_Code_BaseFiles", "ARQUIVOS BASE" },
        { "UI_Code_ThemeJson", "theme.json (Leitura)" },
        { "UI_Code_HtmlCanvases", "TELAS HTML" },
        { "UI_Code_IndexUnified", "index.html (Unificado)" },

        // --- Caja de herramientas (ToolboxView.axaml) ---
        { "UI_CanvasSize", "Tamanho da tela" },
        { "UI_Tb_Basics", "BÁSICOS" },
        { "UI_Tb_PredefinedImages", "Imagens Predefinidas" },
        { "UI_Tb_Notifications", "NOTIFICAÇÕES" },
        { "UI_Tb_LevelData", "DADOS DO NÍVEL" },
        { "UI_Tb_StreamMeta", "METADADOS DA LIVE" },
        { "UI_Tb_Stats", "ESTATÍSTICAS E DIFICULDADE" },
        { "UI_Tb_ExternalLists", "LISTAS EXTERNAS" },

        // --- Tipos de objeto ---
        // La llave es UI_Obj_ + el nombre EXACTO del enum WidgetObjectType. Las usan la caja de
        // herramientas y AddWidgetObject, que pone este texto como nombre del objeto nuevo: no
        // renombrar una llave sin renombrar el valor del enum.
        { "UI_Obj_Text", "Texto" },
        { "UI_Obj_Image", "Imagem" },
        { "UI_Obj_Video", "Vídeo" },
        { "UI_Obj_NotificationBox", "Caixa de Notificação" },
        { "UI_Obj_LevelBackground", "Fundo do Nível" },
        { "UI_Obj_LevelName", "Nome do Nível" },
        { "UI_Obj_CreatorName", "Criador" },
        { "UI_Obj_LevelId", "ID do Nível" },
        { "UI_Obj_SentBy", "Enviado por" },
        { "UI_Obj_PlatformIcon", "Ícone da Plataforma" },
        { "UI_Obj_PlatformName", "Nome da Plataforma" },
        { "UI_Obj_RequestStatus", "Status (Request)" },
        { "UI_Obj_Timer", "Temporizador" },
        { "UI_Obj_RateFace", "Rate Face" },
        { "UI_Obj_RateText", "Rate Text" },
        { "UI_Obj_Stars", "Estrelas/Luas" },
        { "UI_Obj_Downloads", "Downloads" },
        { "UI_Obj_LikeIcon", "Ícone de Like/Dislike" },
        { "UI_Obj_StarIcon", "Ícone de Estrela/Lua" },
        { "UI_Obj_LikesAndDislikes", "Likes / Dislikes" },
        { "UI_Obj_Orbs", "Orbes" },
        { "UI_Obj_Duration", "Duração" },
        { "UI_Obj_AredlPos", "Posição AREDL" },
        { "UI_Obj_GddlRate", "GDDL Rate" },

        // --- Panel de propiedades (PropertiesView.axaml) ---
        { "UI_Prop_CurrentLayer", "Camada atual: " },
        { "UI_Prop_PreviewOn", "Pré-visualização: ON" },
        { "UI_Prop_PreviewOff", "Pré-visualização: OFF" },
        { "UI_Prop_TextContent", "Conteúdo do texto" },
        { "UI_Prop_SystemFonts", "Fontes do sistema" },
        { "UI_Prop_Alignment", "Alinhamento" },
        { "UI_AlignLeft", "Esquerda" },
        { "UI_AlignCenter", "Centro" },
        { "UI_AlignRight", "Direita" },
        { "UI_AlignJustify", "Justificar" },
        { "UI_Prop_VerticalAlignment", "Alinhamento Vertical" },
        { "UI_AlignTop", "Superior" },
        { "UI_AlignBottom", "Inferior" },
        { "UI_Prop_TextPadding", "Margem Interna da Caixa" },
        { "UI_Prop_WordWrap", "Quebra de linha automática" },
        { "UI_Prop_Outline", "Contorno" },
        { "UI_Prop_OutlineHint", "A espessura é em pixels do widget, igual ao tamanho da fonte." },
        { "UI_Prop_RequesterColor", "Cor do Espectador" },
        { "UI_Prop_LevelDataColor", "Cor do Nível/ID" },
        { "UI_Prop_BackgroundType", "Tipo de fundo" },
        { "UI_BgColor", "Cor" },
        { "UI_BgTransparent", "Transparente" },
        { "UI_Prop_Blur", "Desfoque" },
        { "UI_Prop_SelectFile", "Selecionar Arquivo..." },
        { "UI_Prop_LevelBgHint", "Aparece quando o nível não tem miniatura, e entre um pedido e outro." },
        { "UI_Prop_LevelBgHintEmpty", "Sem imagem padrão: se o nível não tiver miniatura, e entre um pedido e outro, nada aparece." },
        { "UI_Prop_RemoveDefaultImage", "Remover imagem padrão" },
        { "UI_StretchKeep", "Manter Proporção" },
        { "UI_StretchFill", "Preencher (Esticar)" },
        { "UI_StretchZoom", "Preencher (Zoom)" },
        { "UI_Prop_VideoLoop", "Reproduzir em loop" },
        { "UI_Prop_VideoMute", "Silenciar" },
        { "UI_Prop_Poster", "Quadro de amostra" },
        { "UI_Prop_PickPoster", "Escolher quadro..." },
        { "UI_Prop_RemovePoster", "Remover quadro" },
        { "UI_Prop_PosterHint", "A tela desenha este quadro, ou o nome do arquivo se você não escolher nenhum. No OBS e na pré-visualização você pode ver o vídeo tocando." },
        { "UI_Prop_Halo", "Halo de rate" },
        { "UI_Prop_ShowHalo", "Mostrar halo do nível" },
        { "UI_HaloAuto", "Automático (alterna na pré-visualização)" },
        { "UI_HaloNone", "Nenhum" },
        { "UI_Prop_HaloSize", "Tamanho do halo" },
        { "UI_Prop_HaloHint", "Na live, o halo é decidido pelo nível real; aqui você escolhe qual pré-visualizar." },
        { "UI_Prop_Overlay", "Cor de Sobreposição (Filtro)" },
        { "UI_Prop_Opacity", "Opacidade" },
        { "UI_Prop_Roundness", "Arredondamento da borda" },
        { "UI_Prop_NotifDuration", "Duração da notificação (Segundos)" },
        { "UI_Prop_Size", "Tamanho" },
        { "UI_Prop_Position", "Posição" },
        { "UI_Prop_LayerNumber", "Número da camada" },

        // --- Ventana principal (MainWindow.axaml) ---
        { "UI_Tip_AddObject", "Adicionar Objeto" },
        { "UI_Tip_DeleteObject", "Excluir Objeto" },
        { "UI_Tip_EditProperties", "Editar Propriedades" },
        { "UI_Tip_CodeEditor", "Editor de Código" },
        { "UI_Tip_SaveAs", "Salvar como... (.zip)\nCtrl+S salva direto no último arquivo" },
        { "UI_Tip_OpenTheme", "Abrir Tema" },
        { "UI_WidgetTypeLabel", "Tipo de Widget" },
        { "UI_Queue_CardSettings", "Configuração dos Cartões" },
        { "UI_Queue_MaxItems", "Máximo a exibir" },
        { "UI_Queue_Direction", "Direção" },
        { "UI_DirVertical", "Vertical" },
        { "UI_DirHorizontal", "Horizontal" },
        { "UI_Queue_Spacing", "Espaçamento (Margem)" },
        { "UI_ShowGhostCards", "Mostrar cartões fantasma" },
        { "UI_Notif_Settings", "AJUSTES DE NOTIFICAÇÕES" },
        { "UI_Notif_MaxCards", "Máximo de cartões:" },
        { "UI_Notif_ListDirection", "Direção da lista:" },
        { "UI_Notif_CardSpacing", "Espaçamento entre cartões:" },
        { "UI_Notif_TimerMode", "Modo do Temporizador:" },
        { "UI_Notif_Simultaneous", "Simultâneo (Todas de uma vez)" },
        { "UI_Notif_Cascade", "Em Cascata (Uma por uma)" },
        { "UI_Tip_Undo", "Desfazer (Ctrl+Z)" },
        { "UI_Tip_Redo", "Refazer (Ctrl+Y)" },
        { "UI_Tip_ZoomOut", "Diminuir zoom" },
        { "UI_Tip_ZoomIn", "Aumentar zoom" },
        { "UI_BtnPreview", "Pré-visualizar ↗" },
        { "UI_Tip_Preview", "Abre o widget inteiro no navegador, com dados de exemplo. É o index.html real do tema." },
        { "UI_AuthorLabel", "Autor:" },
        { "UI_ThemeLabel", "Tema:" },
        { "UI_Menu_PasteObjects", "Colar Objetos (Ctrl+V)" },
        { "UI_Menu_LayerUp", "Subir uma camada (Ctrl+Up)" },
        { "UI_Menu_LayerDown", "Descer uma camada (Ctrl+Down)" },
        { "UI_Menu_Link", "Vincular (Ctrl+L)" },
        { "UI_Menu_Unlink", "Desvincular (Ctrl+Alt+L)" },
        { "UI_Menu_Duplicate", "Duplicar (Ctrl+D)" },
        { "UI_Menu_Copy", "Copiar (Ctrl+C)" },
        { "UI_Menu_Paste", "Colar (Ctrl+V)" },
        { "UI_Menu_PasteProperties", "Colar Propriedades (Ctrl+Alt+V)" },
        { "UI_PlaysInObs", "Reproduz no OBS" },
        { "UI_Tip_CropTop", "Cortar em Cima" },
        { "UI_Tip_CropBottom", "Cortar Embaixo" },
        { "UI_Tip_CropLeft", "Cortar à Esquerda" },
        { "UI_Tip_CropRight", "Cortar à Direita" },
        { "UI_Tip_Scale", "Redimensionar" },
        { "UI_Tip_Rotate", "Girar Livremente" },
    };
}

/// <summary>
/// {i18n:Tr Llave} en XAML, con xmlns:i18n="using:SheepGDEditor.Services". Es la misma
/// forma de escribirlo que en SheepGDRequest.
///
/// A diferencia de la de Request, devuelve el texto directamente y no un Binding: aquí el
/// idioma no cambia mientras el Editor está abierto, así que no hay nada que vigilar.
/// </summary>
public class TrExtension : MarkupExtension
{
    /// <summary>La llave de la traducción: lo que va detrás de "Tr" en el XAML.</summary>
    public string Key { get; set; }

    /// <summary>El XAML llama a este constructor con la llave: {i18n:Tr UI_Tip_Undo}.</summary>
    public TrExtension(string key)
    {
        Key = key;
    }

    /// <summary>El texto traducido de la llave.</summary>
    public override object ProvideValue(IServiceProvider serviceProvider) => EditorI18n.Tr(Key);
}
