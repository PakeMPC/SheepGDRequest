using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ManagedBass; // Reproducción de audio (BASS)

namespace SheepGDRequest.Services;

/// <summary>
/// Lectura en voz alta (TTS) de las notificaciones. Obtiene el audio del servicio de voz de
/// Google Translate y lo reproduce con BASS (ManagedBass), cuya biblioteca nativa se distribuye
/// con la aplicación, así que el usuario no tiene que instalar nada.
///
/// Depende de Internet y de un endpoint no oficial de Google Translate (client=tw-ob), que
/// Google puede cambiar o retirar sin aviso. Lo crea MainWindowViewModel y lo usa
/// NotificationService.
///
/// Si algo falla (BASS sin dispositivo de audio, Google responde con un error, el MP3 no se puede
/// abrir), ese mensaje no suena y queda una línea en la consola que dice por qué.
/// </summary>
public class TtsService
{
    // Cola de textos: se leen de uno en uno y en orden, sin solaparse.
    private readonly BlockingCollection<string> _speechQueue = new();

    // Con tiempo límite propio: con el de por defecto (100 s), una petición colgada retendría toda
    // la cola de voz.
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Si BASS está inicializado. Si no lo está, se reintenta antes de cada mensaje (por ejemplo,
    // por si se conecta un dispositivo de audio), y el fallo solo se escribe en la consola la
    // primera vez.
    private bool _bassListo;
    private bool _falloDeBassAvisado;

    public TtsService()
    {
        IniciarBass();

        // User-Agent de navegador para que Google no rechace la petición como automatizada.
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        // Un único consumidor durante toda la ejecución de la aplicación.
        Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Inicializa BASS con el dispositivo de audio por defecto del sistema (-1). Falla sin
    /// dispositivo de audio, o si falta la biblioteca nativa (libbass en Linux).
    /// </summary>
    private void IniciarBass()
    {
        try
        {
            // "Already" significa que ya estaba inicializado: también sirve.
            _bassListo = Bass.Init(-1, 44100, DeviceInitFlags.Default) || Bass.LastError == Errors.Already;
            if (!_bassListo && !_falloDeBassAvisado)
            {
                _falloDeBassAvisado = true;
                Console.WriteLine(string.Format(I18n.Tr("Log_TtsInitError"), Bass.LastError));
            }
        }
        catch (Exception ex)
        {
            _bassListo = false;
            if (!_falloDeBassAvisado)
            {
                _falloDeBassAvisado = true;
                Console.WriteLine(string.Format(I18n.Tr("Log_TtsInitError"), ex.Message));
            }
        }
    }

    /// <summary>
    /// Añade el texto a la cola y vuelve de inmediato; no espera a que se reproduzca.
    /// </summary>
    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        string cleanText = QuitarEmojis(text);
        if (cleanText.Length == 0) return;
        _speechQueue.Add(cleanText);
    }

    /// <summary>
    /// Quita los emojis para que la voz no los lea: los símbolos (categoría Unicode "So", donde
    /// están casi todos los emojis), los modificadores de tono de piel, los selectores de
    /// variación, el unidor de ancho cero de los emojis compuestos y la marca de tecla. Va por
    /// categoría y no por lista, así que cubre también los emojis de avisos futuros.
    /// </summary>
    public static string QuitarEmojis(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (Rune r in text.EnumerateRunes())
        {
            int v = r.Value;
            bool esEmoji = Rune.GetUnicodeCategory(r) == UnicodeCategory.OtherSymbol
                || (v >= 0x1F3FB && v <= 0x1F3FF)   // tonos de piel
                || v == 0xFE0E || v == 0xFE0F       // selectores de variación
                || v == 0x200D                      // unidor de ancho cero
                || v == 0x20E3;                     // marca de tecla (1️⃣)
            if (!esEmoji) sb.Append(r.ToString());
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private async Task ProcessQueueAsync()
    {
        foreach (var text in _speechQueue.GetConsumingEnumerable())
        {
            try
            {
                // La voz usa el idioma de la aplicación en el momento de leer el mensaje, no el que
                // había cuando se creó.
                string lang = I18n.CurrentLanguage;
                int vol = ViewModels.MainWindowViewModel.Instance.SettingsVM.Settings.TtsVolume;

                // Sin BASS no se puede reproducir: no se descarga nada.
                if (!_bassListo) IniciarBass();
                if (!_bassListo) continue;

                // Descarga el MP3 desde Google TTS.
                string url = $"https://translate.google.com/translate_tts?ie=UTF-8&tl={lang}&client=tw-ob&q={Uri.EscapeDataString(text)}";

                using var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_TtsHttpError"), (int)response.StatusCode));
                    continue;
                }

                // Siempre el mismo archivo temporal: es válido porque la cola procesa los mensajes
                // de uno en uno. Dos instancias de la aplicación a la vez sí lo sobrescribirían.
                string tempFile = Path.Combine(Path.GetTempPath(), "sheep_tts.mp3");
                var audioBytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(tempFile, audioBytes);

                // 1. Abre el archivo como stream de BASS.
                int stream = Bass.CreateStream(tempFile, 0, 0, BassFlags.Default);
                if (stream == 0)
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_TtsStreamError"), Bass.LastError));
                }
                else
                {
                    // 2. Volumen (BASS usa una escala de 0.0f a 1.0f).
                    Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, vol / 100f);

                    // 3. Reproducción.
                    Bass.ChannelPlay(stream);

                    // 4. Espera, sin bloquear la UI, hasta que termine el audio.
                    while (Bass.ChannelIsActive(stream) == PlaybackState.Playing)
                    {
                        await Task.Delay(100);
                    }

                    // 5. Libera el stream.
                    Bass.StreamFree(stream);

                    // Pausa breve antes del siguiente mensaje.
                    await Task.Delay(500);
                }
            }
            // Incluye el tiempo límite de la descarga (TaskCanceledException).
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_TtsError"), ex.Message));
            }
        }
    }
}
