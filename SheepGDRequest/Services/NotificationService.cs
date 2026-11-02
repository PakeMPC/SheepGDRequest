using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using SheepGDRequest.Models;

namespace SheepGDRequest.Services;

/// <summary>
/// Gestiona la creación, el almacenamiento y la exportación de notificaciones.
/// Mantiene un máximo de 20 en memoria, guarda un JSON estructurado para los widgets y exporta
/// un TXT con un máximo de 3 mensajes para OBS.
///
/// Cada notificación sale por cuatro vías: el panel de la ventana, notifications.json (el
/// historial, que se recarga al arrancar), obs_notifications.txt y el WebSocket de los widgets;
/// y, según los ajustes, por voz (TtsService).
///
/// Las escrituras a disco no se coordinan entre llamadas. Con 30 notificaciones simultáneas en
/// Linux, ni el JSON ni el TXT se corrompieron; en Windows no se ha comprobado.
/// </summary>
public class NotificationService
{
    private readonly string _jsonFilePath;
    private readonly string _obsFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    // Lista que muestra el panel de notificaciones de la ventana. Solo se modifica desde el hilo de
    // la UI.
    public ObservableCollection<NotificationModel> Notifications { get; private set; }

    public NotificationService()
    {
        _jsonFilePath = AppPaths.NotificationsFile;
        _obsFilePath = AppPaths.ObsNotificationsFile;

        _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        Notifications = new ObservableCollection<NotificationModel>();
    }

    /// <summary>
    /// Carga el historial de notificaciones guardado en disco.
    /// Si el archivo está dañado, se empieza con la lista vacía sin avisar.
    /// </summary>
    public async Task LoadNotificationsAsync()
    {
        if (File.Exists(_jsonFilePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(_jsonFilePath);
                var loaded = JsonSerializer.Deserialize<ObservableCollection<NotificationModel>>(json, _jsonOptions);

                if (loaded != null)
                {
                    // Se usa el hilo de la UI, que es el único que puede modificar la colección
                    // enlazada, y se vacía y rellena la colección existente en lugar de
                    // sustituirla, para no romper el enlace con la vista.
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Notifications.Clear();
                        foreach (var notif in loaded)
                        {
                            Notifications.Add(notif);
                        }
                    });
                }
            }
            catch { /* Si el archivo está corrupto, empezamos con una lista limpia */ }
        }
    }

    /// <summary>
    /// Añade una notificación nueva, recorta los textos y actualiza el JSON, OBS y los widgets.
    /// </summary>
    /// <param name="type">"Success", "Warning", "Error" o "Info": color y voz.</param>
    /// <param name="translationKey">Clave de I18n con los huecos {0} usuario, {1} nivel y
    /// {2} razón.</param>
    /// <param name="username">Quién pidió, o Default_System traducido para los avisos del
    /// programa. Se recorta a 10 caracteres para el panel y OBS; la voz lo lee completo.</param>
    /// <param name="levelData">Nombre o ID del nivel. Se recorta a 17 caracteres con el mismo
    /// criterio.</param>
    /// <param name="reasonKey">Clave de la razón; admite "Clave|parámetro" (lo separa
    /// I18n.Tr).</param>
    public async Task AddNotificationAsync(string type, string translationKey, string username, string levelData, string reasonKey = "")
    {
        string cleanUser = Truncate(username, 10);
        string cleanLevel = Truncate(levelData, 17);

        // 1. Traducción y formato del mensaje. Message se guarda ya traducido para los widgets y
        // OBS. El panel no lo usa: vuelve a traducir con TranslationKey al mostrarlo
        // (NotificationRichTextHelper), así que sigue el idioma activo.
        string format = I18n.Tr(translationKey);
        string reason = string.IsNullOrEmpty(reasonKey) ? "" : I18n.Tr(reasonKey);
        string finalMessage = "";
        try { finalMessage = string.Format(format, cleanUser, cleanLevel, reason); }
        catch { finalMessage = format; }

        var newNotif = new NotificationModel
        {
            Timestamp = DateTime.Now,
            Type = type,
            TranslationKey = translationKey,
            Username = cleanUser,
            LevelData = cleanLevel,
            ReasonKey = reasonKey,
            Message = finalMessage, // Texto final ya traducido
            IsTtsPending = true
        };

        // 2. Actualización de la lista en el hilo de la UI.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // La más reciente va siempre arriba (índice 0).
            Notifications.Insert(0, newNotif);

            // Máximo de 20 notificaciones en el historial.
            while (Notifications.Count > 20)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }
        });

        // 3. Lectura en voz alta (TTS). Va antes del guardado para que el JSON y los widgets
        // reciban IsTtsPending ya actualizado.
        var config = ViewModels.MainWindowViewModel.Instance.SettingsVM.Settings;
        bool shouldSpeak = false;

        // Primero se comprueba si el emisor es el propio programa ("Sistema"). Se compara con el
        // texto traducido, que es lo que usan quienes crean esas notificaciones.
        if (username == I18n.Tr("Default_System"))
        {
            if (config.TtsEnableSystem) shouldSpeak = true;
        }
        else
        {
            // Si es un espectador, se decide según el tipo de notificación.
            if (type == "Success" && config.TtsEnableSuccess) shouldSpeak = true;
            else if ((type == "Error" || type == "Warning") && config.TtsEnableFilters) shouldSpeak = true;
            else if (type == "Info" && config.TtsEnableSystem) shouldSpeak = true;
        }

        if (shouldSpeak)
        {
            // Se reutilizan "format" y "reason", pero con el usuario y el nivel sin recortar: la
            // voz no tiene limitación de espacio.
            string ttsText = "";
            try { ttsText = string.Format(format, username, levelData, reason); }
            catch { ttsText = format; }

            ViewModels.MainWindowViewModel.Instance.TtsService.Speak(ttsText);
            newNotif.IsTtsPending = false;
        }

        // 4. Guardado en disco y envío a los widgets.
        await SaveNotificationsAsync();
        await ExportToObsAsync();
        if (config.IsWidgetsServerEnabled)
        {
            WidgetServerService.Instance.BroadcastNotificationAsync(newNotif);
        }
    }

    private async Task SaveNotificationsAsync()
    {
        try
        {
            var snapshot = Notifications.ToList();
            string json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            await File.WriteAllTextAsync(_jsonFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_NotifSaveError"), ex.Message));
        }
    }

    private async Task ExportToObsAsync()
    {
        try
        {
            var top3 = Notifications.Take(3).ToList();
            using StreamWriter writer = new StreamWriter(_obsFilePath, false);

            foreach (var notif in top3)
            {
                string reason = string.IsNullOrEmpty(notif.ReasonKey) ? "" : I18n.Tr(notif.ReasonKey);
                string format = I18n.Tr(notif.TranslationKey);
                string finalMsg = "";

                try { finalMsg = string.Format(format, notif.Username, notif.LevelData, reason); }
                catch { finalMsg = format; }

                await writer.WriteLineAsync(finalMsg);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_NotifObsError"), ex.Message));
        }
    }

    /// <summary>
    /// Recorta el texto para que quepa en el panel y en OBS. Los "..." se añaden aparte de los N
    /// caracteres.
    /// </summary>
    private string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        value = value.Trim();
        return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "...";
    }

    /// <summary>
    /// Vacía el historial (lista, JSON y TXT de OBS). La usa el botón correspondiente de Ajustes.
    /// </summary>
    public async Task ClearAllAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => Notifications.Clear());
        await SaveNotificationsAsync();
        try { File.WriteAllText(_obsFilePath, ""); } catch { }
    }
}
