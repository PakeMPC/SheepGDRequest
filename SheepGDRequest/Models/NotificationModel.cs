using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SheepGDRequest.Models;

/// <summary>
/// Una notificación del panel de la ventana principal. Guarda la clave de traducción y los datos
/// (usuario, nivel y razón) en lugar del texto final, para que el panel la traduzca al mostrarla.
/// Se guarda en notifications.json, que se recarga al arrancar, y se envía a los widgets por
/// WebSocket.
/// </summary>
public partial class NotificationModel : ObservableObject
{
    // Hora local de creación.
    [ObservableProperty]
    private DateTime _timestamp = DateTime.Now;

    // Tipo: decide el color del borde en el panel y si se lee en voz alta.
    [ObservableProperty]
    private string _type = "Info"; // "Success", "Warning", "Error" o "Info"

    // Clave del texto en I18n. Sus huecos {0}, {1} y {2} se rellenan con Username, LevelData y la
    // traducción de ReasonKey.
    [ObservableProperty]
    private string _translationKey = "";

    // Usuario y nivel, ya recortados por NotificationService para que quepan en el panel.
    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _levelData = "";

    // Clave de la razón; vacía si no hay. Admite "Clave|parámetro", que separa I18n.Tr.
    [ObservableProperty]
    private string _reasonKey = "";

    // Pasa a false cuando NotificationService envía la notificación a la voz. En C# no lo lee
    // nadie; solo viaja en el JSON.
    [ObservableProperty]
    private bool _isTtsPending = true;
    // Texto ya traducido al idioma activo cuando se creó. El panel no lo usa; viaja en el JSON para
    // los widgets.
    public string Message { get; set; } = string.Empty;
}
