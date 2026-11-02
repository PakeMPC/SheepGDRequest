using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using SheepGDRequest.Models;
using SheepGDRequest.Services;

namespace SheepGDRequest.Helpers;

/// <summary>
/// Propiedad adjunta que muestra una notificación en un TextBlock con formato. Traduce la clave
/// de la notificación y la divide en trozos: el usuario ({0}) y el nivel ({1}) van en dorado y en
/// negrita, la razón ({2}) va como texto normal y algunos emojis llevan un color según su
/// significado. Se usa en el panel de notificaciones de la ventana principal.
/// </summary>
public class NotificationRichTextHelper
{
    /// <summary>
    /// Notificación que se muestra. Desde XAML: i18n:NotificationRichTextHelper.Data="{Binding}".
    /// </summary>
    public static readonly AttachedProperty<NotificationModel?> DataProperty =
        AvaloniaProperty.RegisterAttached<NotificationRichTextHelper, TextBlock, NotificationModel?>("Data");

    public static void SetData(AvaloniaObject element, NotificationModel? value) => element.SetValue(DataProperty, value);
    public static NotificationModel? GetData(AvaloniaObject element) => element.GetValue(DataProperty);

    /// <summary>
    /// TextBlocks que muestran una notificación, para volver a pintarlos al cambiar de idioma. Las
    /// referencias son débiles: un TextBlock que el panel ya descartó no se retiene en memoria.
    /// Solo se usa desde el hilo de la UI.
    /// </summary>
    private static readonly List<WeakReference<TextBlock>> Vivos = new();

    // Cada vez que cambia Data en un TextBlock, se vacían sus Inlines y se construyen de nuevo. Al
    // cambiar de idioma se repite con todos los que siguen vivos, así que las notificaciones que ya
    // están en el panel también cambian de idioma.
    static NotificationRichTextHelper()
    {
        DataProperty.Changed.AddClassHandler<TextBlock>((tb, e) =>
        {
            if (e.NewValue is NotificationModel notif)
            {
                Registrar(tb);
                Pintar(tb, notif);
            }
        });

        I18nNotifier.Instance.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RepintarTodos);
    }

    private static void Registrar(TextBlock tb)
    {
        foreach (var referencia in Vivos)
        {
            if (referencia.TryGetTarget(out var existente) && ReferenceEquals(existente, tb)) return;
        }
        Vivos.Add(new WeakReference<TextBlock>(tb));
    }

    private static void RepintarTodos()
    {
        // Recorre de atrás adelante para poder quitar las referencias que ya no apuntan a nada.
        for (int i = Vivos.Count - 1; i >= 0; i--)
        {
            if (!Vivos[i].TryGetTarget(out var tb))
            {
                Vivos.RemoveAt(i);
                continue;
            }
            if (GetData(tb) is NotificationModel notif) Pintar(tb, notif);
        }
    }

    /// <summary>
    /// Traduce la notificación con el idioma activo y la pinta en el TextBlock.
    /// </summary>
    private static void Pintar(TextBlock tb, NotificationModel notif)
    {
        tb.Inlines ??= new InlineCollection();
        tb.Inlines.Clear();

        string format = I18n.Tr(notif.TranslationKey);
        string reason = string.IsNullOrEmpty(notif.ReasonKey) ? "" : I18n.Tr(notif.ReasonKey);
        string user = notif.Username ?? "";
        string level = notif.LevelData ?? "";

        // Divide el texto en trozos. Los huecos {0}, {1} y {2} y los emojis con color
        // propio quedan como trozos sueltos, porque el paréntesis del patrón hace que
        // Regex.Split los conserve.
        //
        // Nota: ⚠️, ⏭️ y ⬆️ llevan el selector de variación U+FE0F, igual que en los textos
        // de I18n. Si un texto los escribe sin él, no se reconocen y se muestran sin color.
        var parts = Regex.Split(format, @"(\{\d\}|❗|⛔|✅|❓|⚠️|⏭️|📭|⬆️)");

        foreach (var part in parts)
        {
            if (part == "{0}")
            {
                tb.Inlines.Add(new Run(user) { Foreground = Brushes.Goldenrod, FontWeight = FontWeight.Bold });
            }
            else if (part == "{1}")
            {
                tb.Inlines.Add(new Run(level) { Foreground = Brushes.Goldenrod, FontWeight = FontWeight.Bold });
            }
            else if (part == "{2}")
            {
                tb.Inlines.Add(new Run(reason));
            }
            // Emojis con color: rojo para errores y bloqueos, verde para éxitos, naranja
            // para avisos y niveles que no existen, azul claro para saltos, subidas de
            // prioridad y cola vacía. Los demás emojis de los textos (⌛, ⏳, ⏱️, 🔌, 🔗 y 🧩)
            // no tienen regla y se muestran como texto normal.
            else if (part == "❗" || part == "⛔")
            {
                tb.Inlines.Add(new Run(part) { Foreground = Brushes.IndianRed, FontWeight = FontWeight.Bold });
            }
            else if (part == "✅")
            {
                tb.Inlines.Add(new Run(part) { Foreground = Brushes.MediumSeaGreen, FontWeight = FontWeight.Bold });
            }
            else if (part == "⚠️" || part == "❓")
            {
                tb.Inlines.Add(new Run(part) { Foreground = Brushes.Orange, FontWeight = FontWeight.Bold });
            }
            else if (part == "⏭️" || part == "⬆️" || part == "📭")
            {
                tb.Inlines.Add(new Run(part) { Foreground = Brushes.LightSkyBlue, FontWeight = FontWeight.Bold });
            }
            else if (!string.IsNullOrEmpty(part))
            {
                // Resto del texto, sin formato. Regex.Split deja cadenas vacías al
                // principio y entre dos separadores seguidos; por eso se descartan.
                tb.Inlines.Add(new Run(part));
            }
        }
    }
}
