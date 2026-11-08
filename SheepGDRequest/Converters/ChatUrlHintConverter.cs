using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using SheepGDRequest.Models;
using SheepGDRequest.Services;

namespace SheepGDRequest.Converters;

/// <summary>
/// Texto de ejemplo de la caja de URL de cada enlace de chat, según su plataforma (clave
/// UI_ChatUrlHint_&lt;Plataforma&gt;): le dice al usuario qué tiene que pegar en cada caso, que
/// es la duda más habitual al configurar las fuentes.
///
/// Se usa en un MultiBinding con la plataforma y con <c>I18nNotifier.LanguageTrigger</c>, como
/// EnumTranslatorConverter: el segundo valor no se usa, pero hace que el texto cambie de idioma
/// sin recrear la vista.
/// </summary>
public class ChatUrlHintConverter : IMultiValueConverter
{
    // Instancia única para usar desde XAML con x:Static.
    public static readonly ChatUrlHintConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        return values.Count > 0 && values[0] is ChatPlatform plataforma
            ? I18n.Tr($"UI_ChatUrlHint_{plataforma}")
            : "";
    }
}
