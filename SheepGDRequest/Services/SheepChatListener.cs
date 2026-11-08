using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Playwright;
using SheepGDRequest.Models;
using SheepGDRequest.ViewModels;

namespace SheepGDRequest.Services;

/// <summary>
/// Coordina todas las fuentes de chat: decide cómo se conecta cada enlace guardado y lleva el
/// registro de las conexiones activas para poder detenerlas o reiniciarlas.
///
/// YouTube, Kick, SheepChat, SocialStream.Ninja, StreamElements y "Otro" se leen con Playwright:
/// un Chromium, invisible salvo durante el inicio de sesión de "Otro", abre la web del chat y un
/// script inyectado envía los mensajes a C#, que extrae las peticiones de niveles. Twitch se lee
/// sin navegador, por su chat oficial (<see cref="TwitchListener"/>). TikTok y Discord solo pasan
/// por aquí para redirigirse: tienen su propio listener (API de TikTok, bot de Discord).
///
/// Cada fuente de Playwright, y Twitch, vive en un bucle de reconexión
/// (<see cref="MantenerConexionAsync"/>): si la sesión se cae o falla al arrancar, se vuelve a
/// intentar con una espera creciente, hasta que se detiene el enlace.
/// </summary>
public class SheepChatListener
{
    /// <summary>
    /// Una fuente con bucle de reconexión en marcha: el cancelador que la detiene, la tarea de su
    /// bucle, que termina cuando la sesión ya está cerrada (y Chromium, si lo usa), y su
    /// plataforma, para distinguir las que usan navegador (<see cref="UsaNavegador"/>).
    /// </summary>
    private sealed class Conexion
    {
        public Conexion(ChatPlatform platform) => Platform = platform;

        public ChatPlatform Platform { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task Tarea { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// Fuentes de Playwright y de Twitch, una por enlace, con la clave "{plataforma}_{url}" (ver
    /// <see cref="CalcularClave"/>). La entrada se registra antes de lanzar la conexión, así que
    /// detener un enlace funciona también mientras Chromium se descarga o arranca.
    /// Se modifica siempre dentro de <see cref="ConexionesLock"/>.
    /// </summary>
    private static readonly Dictionary<string, Conexion> Conexiones = new();
    private static readonly object ConexionesLock = new();

    /// <summary>
    /// Si la plataforma se lee con el navegador interno (Playwright) y, por tanto, tiene carpeta
    /// de perfil. Twitch, TikTok y Discord se conectan sin navegador.
    /// </summary>
    public static bool UsaNavegador(ChatPlatform platform) =>
        platform is not (ChatPlatform.Twitch or ChatPlatform.TikTok or ChatPlatform.Discord);

    /// <summary>
    /// Equivalente a <see cref="Conexiones"/> para los bots de Discord, que no usan Playwright.
    /// Guardar aquí el listener es lo que permite detenerlo después; sin esta referencia, el bot
    /// seguiría conectado. Usa la misma clave, "{plataforma}_{url}". TikTok tiene su propio
    /// registro en <see cref="TiktokListener"/>, indexado por nombre de usuario.
    /// </summary>
    public static readonly ConcurrentDictionary<string, DiscordChannelsListener> ActiveDiscordListeners = new();

    private const int EsperaInicialMs = 5_000;
    private const int EsperaMaximaMs = 120_000;

    /// <summary>
    /// Tiempo máximo que se espera a que una fuente termine de cerrarse al detenerla. Lo normal
    /// es menos de un segundo; el límite evita que un Chromium colgado bloquee "Guardar".
    /// </summary>
    private const int EsperaCierreMs = 15_000;

    /// <summary>
    /// Tarea única y compartida de preparación del navegador. Todo lo que necesita Playwright
    /// espera a esta misma tarea, así que la descarga ocurre una sola vez aunque se conecten varias
    /// fuentes a la vez. Si falla, se olvida, y la siguiente conexión lo vuelve a intentar.
    /// </summary>
    private static Task<bool>? _browsersReady;
    private static readonly object BrowsersLock = new();

    /// <summary>
    /// Se asegura de que el navegador de Playwright esté descargado.
    ///
    /// Es necesario porque el paquete NuGet incluye el driver (la carpeta .playwright) pero no los
    /// navegadores, que se guardan en una caché por usuario (~/.cache/ms-playwright en Linux,
    /// %USERPROFILE%\AppData\Local\ms-playwright en Windows). En la máquina de desarrollo existen
    /// porque en algún momento se ejecutó "install"; en la de un usuario que solo descarga la
    /// versión compilada, no.
    ///
    /// No es lógica de primer arranque sino de comprobación: "install" es idempotente, así que
    /// también resuelve que el usuario borre la caché o que una actualización del paquete de
    /// Playwright requiera otra versión de Chromium.
    ///
    /// No lanza excepciones: si falla, lo indica en la consola y devuelve false. TikTok y Discord
    /// no usan Playwright.
    ///
    /// App.axaml.cs la lanza al arrancar, sin esperarla, para adelantar la descarga. El progreso no
    /// se ve en la consola de la aplicación: Playwright lo escribe con Console.Write y retornos de
    /// carro, y ConsoleRedirector solo intercepta WriteLine. Por eso el mensaje avisa de que puede
    /// tardar.
    /// </summary>
    /// <returns>true si Chromium está listo para usarse.</returns>
    public static Task<bool> EnsureBrowsersAsync()
    {
        lock (BrowsersLock)
        {
            if (_browsersReady != null) return _browsersReady;

            Task<bool>? tarea = null;
            tarea = Task.Run(() =>
            {
                bool listo = false;
                try
                {
                    Console.WriteLine(I18n.Tr("Log_PlaywrightPreparing"));

                    // "chromium" explícito: sin él se descargan también Firefox y WebKit, que este
                    // proyecto no usa (cientos de MB adicionales).
                    int exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

                    listo = exitCode == 0;
                    if (listo)
                        Console.WriteLine(I18n.Tr("Log_PlaywrightReady"));
                    else
                        Console.WriteLine(string.Format(I18n.Tr("Log_PlaywrightFailed"), exitCode));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_PlaywrightError"), ex.Message));
                }

                // Un fallo (sin red, por ejemplo) no se queda guardado: la siguiente llamada crea
                // una tarea nueva y vuelve a intentar la descarga.
                if (!listo)
                {
                    lock (BrowsersLock)
                    {
                        if (ReferenceEquals(_browsersReady, tarea)) _browsersReady = null;
                    }
                }
                return listo;
            });

            _browsersReady = tarea;
            return tarea;
        }
    }

    /// <summary>
    /// Arranca el interceptor: espera a que se carguen los ajustes y abre una conexión por cada
    /// enlace de chat guardado.
    ///
    /// Solo se llama una vez, al arrancar, desde App.axaml.cs. Después, conectar y desconectar
    /// enlaces corresponde a <see cref="RestartWidgetAsync"/> y <see cref="StopWidgetAsync"/>.
    /// No lanza excepciones: un error inesperado se escribe en la consola.
    /// </summary>
    public async Task StartListeningAsync()
    {
        try
        {
            Console.WriteLine(I18n.Tr("Log_InterceptorStarting"));

            // La lista de enlaces sale de settings.json, que SettingsViewModel lee en segundo
            // plano. Sin esta espera se vería la lista vacía por defecto.
            await MainWindowViewModel.Instance.SettingsVM.Cargado;

            // Espera hasta que haya al menos un enlace. Settings se vuelve a leer en cada vuelta,
            // y debe ser así: la carga de settings.json sustituye ese objeto. Nota: este aviso solo
            // aparece al arrancar; si después se vacía la lista, no vuelve a mostrarse.
            while (MainWindowViewModel.Instance.SettingsVM.Settings.ChatLinks.Count == 0)
            {
                Console.WriteLine(I18n.Tr("Log_BotPaused"));
                await Task.Delay(5000);
            }

            foreach (var widget in MainWindowViewModel.Instance.SettingsVM.Settings.ChatLinks.ToList())
            {
                if (string.IsNullOrWhiteSpace(widget.Url)) continue;
                Conectar(widget);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_InterceptorStartError"), ex.Message));
        }
    }

    /// <summary>
    /// Calcula la clave con la que un enlace se registra en su diccionario de conexiones activas.
    /// TikTok es distinto: su listener indexa por nombre de usuario, no por URL.
    ///
    /// Nota: solo debe usarse al conectar. Para detener o reiniciar se usa widget.ActiveKey, que es
    /// la clave con la que realmente se conectó.
    /// </summary>
    private static string CalcularClave(ChatLinkModel widget)
    {
        if (widget == null || string.IsNullOrWhiteSpace(widget.Url)) return "";

        return widget.Platform == ChatPlatform.TikTok
            ? TiktokListener.ExtraerUsuario(widget.Url)
            : $"{widget.Platform}_{widget.Url}";
    }

    /// <summary>
    /// Registra la clave del enlace y lanza su conexión en segundo plano, sin esperarla: una
    /// fuente lenta no retrasa a las demás. Las fuentes de Playwright quedan registradas antes de
    /// que empiece la tarea, para que detenerlas funcione desde el primer momento.
    /// </summary>
    private static void Conectar(ChatLinkModel widget)
    {
        string url = widget.Url;
        ChatPlatform platform = widget.Platform;
        string key = CalcularClave(widget);
        widget.ActiveKey = key;
        if (string.IsNullOrWhiteSpace(key)) return;

        if (platform == ChatPlatform.TikTok)
        {
            _ = Task.Run(() => TiktokListener.StartListeningAsync(url));
            return;
        }

        if (platform == ChatPlatform.Discord)
        {
            ConectarDiscord(key, url);
            return;
        }

        var conexion = new Conexion(platform);
        Conexion? anterior;
        lock (ConexionesLock)
        {
            Conexiones.Remove(key, out anterior);
            Conexiones[key] = conexion;

            // La nueva conexión espera a que la anterior termine de cerrarse: dos Chromium no
            // pueden abrir la misma carpeta de perfil a la vez, y en Twitch se leería dos veces
            // cada mensaje.
            Task cierreAnterior = anterior?.Tarea ?? Task.CompletedTask;
            conexion.Tarea = Task.Run(() => MantenerConexionAsync(url, platform, cierreAnterior, conexion.Cts.Token));
        }
        anterior?.Cts.Cancel();
    }

    /// <summary>
    /// Discord: un bot a través de la API de Discord, sin navegador. El token es el mismo para
    /// todos los canales y se configura en Ajustes.
    /// </summary>
    private static void ConectarDiscord(string key, string url)
    {
        string token = MainWindowViewModel.Instance.SettingsVM.Settings.DiscordBotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine(I18n.Tr("Log_DiscordTokenEmpty"));
            return;
        }

        // Si ya hay un bot escuchando en este canal, se detiene antes de crear otro. Desde
        // RestartWidgetAsync ya llega detenido; esto cubre que el mismo canal se arranque dos veces
        // sin pasar por ahí. StopAsync captura sus propios errores.
        if (ActiveDiscordListeners.TryRemove(key, out var anterior))
            _ = anterior.StopAsync();

        // El listener se registra antes de conectar, así que StopWidgetAsync lo encuentra aunque
        // todavía esté conectando.
        var discordListener = new DiscordChannelsListener();
        ActiveDiscordListeners[key] = discordListener;
        _ = Task.Run(() => discordListener.StartAsync(token, url));
    }

    /// <summary>
    /// Cierra la conexión de un enlace y la vuelve a abrir. La usan el botón "Reconectar" y
    /// "Guardar cambios", este último para cada enlace.
    ///
    /// Nota: "Guardar cambios" reinicia todos los enlaces con URL, hayan cambiado o no, y cada
    /// fuente de navegador ignora los mensajes de los primeros segundos tras volver a arrancar (5 s
    /// en SheepChat, StreamElements y Kick, 6 s en YouTube, 15 s en "Otro"), porque la web vuelve
    /// a mostrar el historial. Twitch no recibe el historial al conectar.
    /// </summary>
    public static async Task RestartWidgetAsync(ChatLinkModel widget)
    {
        if (widget == null) return;

        // Se cierra con la clave con la que se conectó, no con la URL actual: si el usuario la
        // había editado, la clave recalculada no encontraría la conexión y quedarían dos abiertas.
        bool habiaConexion = await StopWidgetAsync(widget);

        if (string.IsNullOrWhiteSpace(widget.Url))
        {
            widget.ActiveKey = "";
            return;
        }

        Console.WriteLine(string.Format(
            I18n.Tr(habiaConexion ? "Log_RestartingConnection" : "Log_StartingNewConnection"),
            widget.Platform));

        Conectar(widget);
    }

    /// <summary>
    /// Desconecta un enlace sin volver a abrirlo. Es la contraparte de RestartWidgetAsync y debe
    /// llamarse al borrar un enlace en la interfaz. En las fuentes de Playwright espera a que
    /// Chromium se haya cerrado, y en Twitch, a que se cierre la conexión. Devuelve true si había
    /// una conexión activa.
    /// </summary>
    public static async Task<bool> StopWidgetAsync(ChatLinkModel widget)
    {
        if (widget == null) return false;

        // La clave real de la conexión. El cálculo alternativo cubre los enlaces que todavía no la
        // tienen registrada, por ejemplo justo después de cargar settings.json.
        string key = string.IsNullOrWhiteSpace(widget.ActiveKey)
            ? CalcularClave(widget)
            : widget.ActiveKey;

        if (string.IsNullOrWhiteSpace(key)) return false;

        bool habiaConexion;

        if (widget.Platform == ChatPlatform.TikTok)
        {
            habiaConexion = await TiktokListener.StopByUsernameAsync(key);
        }
        else if (widget.Platform == ChatPlatform.Discord)
        {
            habiaConexion = ActiveDiscordListeners.TryRemove(key, out var oldDiscord);
            if (habiaConexion)
            {
                // StopAsync ya captura sus propios errores; este catch es una protección adicional.
                try { await oldDiscord!.StopAsync(); } catch { }
            }
        }
        else
        {
            Conexion? conexion;
            lock (ConexionesLock)
            {
                Conexiones.Remove(key, out conexion);
            }

            habiaConexion = conexion != null;
            if (conexion != null) await DetenerAsync(conexion);
        }

        widget.ActiveKey = "";
        return habiaConexion;
    }

    /// <summary>
    /// Detiene todas las fuentes de Playwright y espera a que sus Chromium se cierren. La usa
    /// "Borrar caché" antes de borrar los perfiles. Las fuentes quedan desconectadas hasta que se
    /// reconecte cada enlace o se guarden los ajustes. Twitch no usa navegador y sigue conectado.
    /// </summary>
    public static async Task DetenerNavegadoresAsync()
    {
        List<Conexion> navegadores;
        lock (ConexionesLock)
        {
            var claves = Conexiones.Where(c => UsaNavegador(c.Value.Platform)).Select(c => c.Key).ToList();
            navegadores = claves.Select(k => Conexiones[k]).ToList();
            foreach (string clave in claves) Conexiones.Remove(clave);
        }

        await Task.WhenAll(navegadores.Select(DetenerAsync));
    }

    /// <summary>
    /// Cancela una fuente con bucle de reconexión y espera a que su bucle termine, con un límite de
    /// <see cref="EsperaCierreMs"/>.
    /// </summary>
    private static async Task DetenerAsync(Conexion conexion)
    {
        try { conexion.Cts.Cancel(); } catch { }
        try { await conexion.Tarea.WaitAsync(TimeSpan.FromMilliseconds(EsperaCierreMs)); } catch { }
    }

    /// <summary>
    /// Borra la sesión guardada de un enlace "Otro" (solo su carpeta de perfil) y lo vuelve a
    /// conectar, lo que abre de nuevo la ventana de inicio de sesión. Es el remedio cuando la
    /// sesión de la web caduca o el usuario cerró el inicio de sesión sin entrar: sin esto, el
    /// único camino era "Borrar caché", que borra las sesiones de todos los enlaces.
    /// </summary>
    public static async Task VolverAIniciarSesionAsync(ChatLinkModel widget)
    {
        if (widget == null || widget.Platform != ChatPlatform.Otro || string.IsNullOrWhiteSpace(widget.Url))
            return;

        await StopWidgetAsync(widget);

        string profileDir = CarpetaDePerfil(widget.Platform, widget.Url);
        try
        {
            // Chromium ya está cerrado, pero puede tardar un momento en soltar algún archivo.
            for (int intento = 1; Directory.Exists(profileDir); intento++)
            {
                try
                {
                    Directory.Delete(profileDir, true);
                }
                catch (IOException) when (intento < 5)
                {
                    await Task.Delay(500);
                }
                catch (UnauthorizedAccessException) when (intento < 5)
                {
                    await Task.Delay(500);
                }
            }
            Console.WriteLine(string.Format(I18n.Tr("Log_ReLoginRequested"), widget.Platform));
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_ReLoginError"), widget.Platform, ex.Message));
        }

        Conectar(widget);
    }

    // ================================================================
    // CARPETAS DE PERFIL
    // ================================================================
    /// <summary>
    /// Carpeta de perfil (cookies, sesión) de un enlace: una por plataforma y URL. El nombre lleva
    /// los primeros 16 caracteres hexadecimales del SHA-256 de la URL, así que mide lo mismo sea
    /// cual sea la URL.
    /// </summary>
    private static string CarpetaDePerfil(ChatPlatform platform, string url)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        string id = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return Path.Combine(AppPaths.BrowserProfilesDir, $"{platform}_{id}");
    }

    /// <summary>
    /// Renombra la carpeta de perfil del formato anterior ({plataforma}_{URL en Base64}) al
    /// actual, para que los enlaces que ya tenían sesión la conserven. El formato anterior crecía
    /// con la URL y superaba el límite de 255 bytes de un nombre de carpeta con URLs de unos 180
    /// caracteres. Si la carpeta nueva ya existe, no hace nada.
    /// </summary>
    private static void MigrarPerfilAntiguo(ChatPlatform platform, string url, string profileDir)
    {
        try
        {
            if (Directory.Exists(profileDir)) return;

            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
                .Replace("+", "-").Replace("/", "_").Replace("=", "");
            string anterior = Path.Combine(AppPaths.BrowserProfilesDir, $"{platform}_{base64}");

            if (Directory.Exists(anterior))
            {
                Directory.Move(anterior, profileDir);
                Console.WriteLine(string.Format(I18n.Tr("Log_ProfileMigrated"), platform));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_ProfileMigrationError"), platform, ex.Message));
        }
    }

    // ================================================================
    // INICIO DE SESIÓN DE "OTRO"
    // ================================================================
    /// <summary>
    /// Muestra una ventana sobre la aplicación y abre un navegador visible para que el usuario
    /// inicie sesión manualmente. Las cookies de sesión quedan guardadas en
    /// <paramref name="profileDir"/>, la misma carpeta de perfil que después abre el Chromium
    /// invisible.
    ///
    /// Solo la usa la fuente "Otro", la única que requiere inicio de sesión.
    ///
    /// Termina cuando el usuario cierra la ventana superpuesta (con el botón o con la X), o cuando
    /// se detiene el enlace, que la cierra; cerrar solo el Chromium no basta. Al terminar, el
    /// "await using" cierra el Chromium visible.
    /// </summary>
    private static async Task PerformLoginWithUIAsync(string url, string profileDir, CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Avalonia.Controls.Window? loginOverlay = null;

        // La ventana se crea en el hilo de la UI; este método continúa en segundo plano y espera a
        // tcs.
        Dispatcher.UIThread.Post(() =>
        {
            loginOverlay = new Avalonia.Controls.Window
            {
                Title = I18n.Tr("UI_LoginRequiredTitle"),
                Width = 450, Height = 220, Topmost = true,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1e1e2e"))
            };

            var stackPanel = new Avalonia.Controls.StackPanel
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(20)
            };

            var label = new Avalonia.Controls.TextBlock
            {
                Text = string.Format(I18n.Tr("UI_LoginPrompt"), url),
                Foreground = Avalonia.Media.Brushes.White,
                TextAlignment = Avalonia.Media.TextAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 20)
            };

            var btn = new Avalonia.Controls.Button
            {
                Content = I18n.Tr("UI_LoginReadyBtn"),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A6E3A1")),
                Foreground = Avalonia.Media.Brushes.Black, FontWeight = Avalonia.Media.FontWeight.Bold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Avalonia.Thickness(30, 10), CornerRadius = new Avalonia.CornerRadius(5)
            };

            var ventana = loginOverlay;
            btn.Click += (s, e) => ventana.Close();
            ventana.Closed += (s, e) => tcs.TrySetResult();

            stackPanel.Children.Add(label);
            stackPanel.Children.Add(btn);
            ventana.Content = stackPanel;

            ventana.Show();
        });

        // Si se detiene el enlace, se cierra la ventana. Este Post se encola detrás del que la
        // crea.
        using var alCancelar = token.Register(() => Dispatcher.UIThread.Post(() => loginOverlay?.Close()));

        using var playwright = await Playwright.CreateAsync();

        // --disable-blink-features=AutomationControlled y el init script de navigator.webdriver
        // ocultan que el navegador está automatizado: algunas webs impiden iniciar sesión si lo
        // detectan.
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false,
            Args = new[] { "--disable-blink-features=AutomationControlled" }
        };

        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(profileDir, options);
        var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

        await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await tcs.Task;
        token.ThrowIfCancellationRequested();

        Console.WriteLine(I18n.Tr("Log_LoginClosed"));
    }

    // ================================================================
    // BUCLE DE RECONEXIÓN DE LAS FUENTES DE PLAYWRIGHT
    // ================================================================
    /// <summary>
    /// Estado del bucle de reconexión de una fuente. Solo lo usa ese bucle, de forma secuencial.
    /// </summary>
    private sealed class EstadoReconexion
    {
        public int EsperaMs = EsperaInicialMs;

        // Se avisa de una caída solo si la fuente llegó a estar activa alguna vez: una URL que no
        // carga desde el principio no genera un aviso en cada reintento.
        public bool HuboConexion;

        // Una caída se avisa una sola vez, aunque los reintentos fallen durante horas; el aviso de
        // "restablecida" sale al volver a estar activa.
        public bool CaidaAvisada;
    }

    /// <summary>
    /// Bucle de vida de una fuente de Playwright o de Twitch: abre una sesión y, si termina sin que
    /// nadie la haya detenido (Chromium o la conexión se cierran, o fallan al arrancar, por ejemplo
    /// con el PC recién encendido y sin red), espera y vuelve a intentarlo. La espera empieza en
    /// 5 s, se duplica en cada fallo hasta 2 minutos y vuelve a 5 s cuando una sesión llega a estar
    /// activa. Termina al cancelar <paramref name="token"/>, siempre con la sesión cerrada.
    ///
    /// Si el enlace no sirve para su plataforma (un canal de Twitch o Kick sin nombre válido, un
    /// enlace de YouTube que no es de un canal ni de un vídeo), lo avisa y no lo intenta: volver a
    /// probar no lo arreglaría.
    /// </summary>
    /// <param name="cierreAnterior">La conexión anterior del mismo enlace, que se espera a que
    /// termine antes de abrir su carpeta de perfil.</param>
    private static async Task MantenerConexionAsync(string url, ChatPlatform platform, Task cierreAnterior, CancellationToken token)
    {
        try { await cierreAnterior; } catch { }

        string? enlaceInvalido = platform switch
        {
            ChatPlatform.Twitch when TwitchListener.ExtraerCanal(url).Length == 0 => "Log_TwitchInvalidChannel",
            ChatPlatform.Kick when ChatsDeStreaming.UrlDeChatDeKick(url) == null => "Log_KickInvalidChannel",
            ChatPlatform.YouTube when ChatsDeStreaming.IdDeVideoDeYouTube(url) == null
                                      && ChatsDeStreaming.UrlEnDirectoDeYouTube(url) == null => "Log_YouTubeInvalidUrl",
            _ => null
        };
        if (enlaceInvalido != null)
        {
            Console.WriteLine(I18n.Tr(enlaceInvalido));
            return;
        }

        // Twitch no tiene carpeta de perfil: no abre navegador.
        string profileDir = "";
        if (UsaNavegador(platform))
        {
            profileDir = CarpetaDePerfil(platform, url);
            MigrarPerfilAntiguo(platform, url, profileDir);
        }

        var estado = new EstadoReconexion();
        string nombre = NombreParaAvisos(platform, url);

        while (!token.IsCancellationRequested)
        {
            try
            {
                Action alActivarse = () => AlActivarse(estado, platform, nombre);
                if (platform == ChatPlatform.Twitch)
                    await TwitchListener.SesionAsync(url, alActivarse, token);
                else
                    await SesionAsync(url, platform, profileDir, alActivarse, token);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_CriticalWidgetError"), platform, ex.Message));
            }

            if (token.IsCancellationRequested) break;

            if (estado.HuboConexion && !estado.CaidaAvisada)
            {
                estado.CaidaAvisada = true;
                await NotificarConexionAsync("Warning", "Notif_ChatDisconnected", nombre);
            }

            Console.WriteLine(string.Format(I18n.Tr("Log_ChatReconnecting"), platform, estado.EsperaMs / 1000));

            try { await Task.Delay(estado.EsperaMs, token); }
            catch (OperationCanceledException) { break; }

            estado.EsperaMs = Math.Min(estado.EsperaMs * 2, EsperaMaximaMs);
        }
    }

    /// <summary>
    /// Se llama cuando una sesión queda activa: reinicia la espera entre intentos y, si se había
    /// avisado de una caída, avisa de que la conexión volvió.
    /// </summary>
    private static void AlActivarse(EstadoReconexion estado, ChatPlatform platform, string nombre)
    {
        estado.EsperaMs = EsperaInicialMs;
        estado.HuboConexion = true;

        if (estado.CaidaAvisada)
        {
            estado.CaidaAvisada = false;
            Console.WriteLine(string.Format(I18n.Tr("Log_ChatReconnected"), platform));
            _ = NotificarConexionAsync("Success", "Notif_ChatReconnected", nombre);
        }
    }

    /// <summary>
    /// Nombre corto de la fuente para los avisos del panel: la plataforma o, en "Otro", el dominio
    /// de la web.
    /// </summary>
    private static string NombreParaAvisos(ChatPlatform platform, string url)
    {
        if (platform == ChatPlatform.Otro && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host.Substring(4) : uri.Host;
        }
        return platform.ToString();
    }

    /// <summary>
    /// Notificación de un evento de conexión (caída o vuelta de una fuente de chat), si el usuario
    /// tiene activados los avisos de sistema. "Sistema" ocupa el hueco del usuario y el nombre de
    /// la fuente el del nivel; "Sistema" se traduce al crear la notificación. La usan también
    /// TikTok y este archivo. Un fallo al notificar no se propaga: no debe detener un bucle de
    /// reconexión.
    /// </summary>
    internal static async Task NotificarConexionAsync(string type, string transKey, string fuente)
    {
        try
        {
            var vm = MainWindowViewModel.Instance;
            if (vm == null || !vm.SettingsVM.Settings.NotifySystemEvents) return;

            await vm.NotificationService.AddNotificationAsync(type, transKey, I18n.Tr("Default_System"), fuente);
        }
        catch { }
    }

    // ================================================================
    // UNA SESIÓN DE PLAYWRIGHT
    // ================================================================
    /// <summary>
    /// Filtro de tiempo de los puentes: lo que llega antes de que se abra se descarta. Cada carga
    /// del documento (la primera y cualquier recarga) lo vuelve a cerrar durante unos segundos,
    /// porque lo que la web pinta al cargar es el historial del chat.
    /// </summary>
    private sealed class Puerta
    {
        private long _abiertaDesde = long.MaxValue;

        public void AbrirDentroDe(int ms) => Interlocked.Exchange(ref _abiertaDesde, Environment.TickCount64 + ms);

        public bool EstaAbierta => Environment.TickCount64 >= Interlocked.Read(ref _abiertaDesde);
    }

    /// <summary>
    /// Envuelve un script para inyectarlo con AddInitScriptAsync: se ejecuta solo en el documento
    /// principal (no en los iframes, que tienen sus propios init scripts) y espera a que exista
    /// document.body. Al ser un init script, se vuelve a aplicar en cada carga de la página, así
    /// que el observador sobrevive a las recargas.
    /// </summary>
    private static string AlCargarEnDocumentoPrincipal(string cuerpo) => @"
        (() => {
            if (window.top !== window) return;
            const iniciar = () => {
" + cuerpo + @"
            };
            if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', iniciar);
            else iniciar();
        })();";

    /// <summary>
    /// Script de SheepChat. Busca los nodos .message que añade SheepChat: la plataforma sale de la
    /// clase de .channel, el usuario de nick-name y el texto de .text. Depende de ese HTML: si
    /// cambia, deja de detectar mensajes sin dar ningún error. Si no encuentra el nombre, envía
    /// '' y C# pone el nombre por defecto traducido.
    /// </summary>
    private const string SheepChatScript = @"
                const observer = new MutationObserver((mutations) => {
                    for (let mutation of mutations) {
                        for (let node of mutation.addedNodes) {
                            if (node.nodeType === 1) {
                                let msgs = [];
                                if (node.classList && node.classList.contains('message')) {
                                    msgs.push(node);
                                } else {
                                    msgs = Array.from(node.querySelectorAll('.message'));
                                }

                                for (let msgNode of msgs) {
                                    let plat = 'global';
                                    let channelSpan = msgNode.querySelector('.channel');
                                    if (channelSpan) {
                                        if (channelSpan.classList.contains('twitch')) plat = 'twitch';
                                        else if (channelSpan.classList.contains('youtube')) plat = 'youtube';
                                        else if (channelSpan.classList.contains('facebook')) plat = 'facebook';
                                        else if (channelSpan.classList.contains('kick')) plat = 'kick';
                                        else if (channelSpan.classList.contains('tiktok')) plat = 'tiktok';
                                    }

                                    let nickNode = msgNode.querySelector('nick-name');
                                    let user = nickNode ? nickNode.textContent.trim() : '';

                                    let textNode = msgNode.querySelector('.text');
                                    let text = textNode ? textNode.textContent.trim() : '';

                                    if (text.length > 0) {
                                        let payload = JSON.stringify({ platform: plat, user: user, text: text });
                                        window.sendDedicatedData(payload);
                                    }
                                }
                            }
                        }
                    }
                });
                observer.observe(document.body, { childList: true, subtree: true });";

    /// <summary>
    /// Una sesión de una fuente de Playwright: abre Chromium con el perfil del enlace, conecta los
    /// puentes y el script de la plataforma, y vigila la página hasta que se cierra.
    ///
    /// Termina normalmente si Chromium o la página se cierran sin que se haya pedido (el bucle de
    /// reconexión abre otra sesión). Si se cancela <paramref name="token"/>, cierra Chromium y
    /// lanza OperationCanceledException (u otra excepción de Playwright por las llamadas que se
    /// cortan). Al salir, el "using" de Playwright y el cierre del contexto cierran Chromium.
    /// </summary>
    private static async Task SesionAsync(string url, ChatPlatform platform, string profileDir, Action alActivarse, CancellationToken token)
    {
        // Si la descarga de Chromium sigue en marcha (primer arranque), se espera aquí. Si falló,
        // la sesión termina y el bucle lo vuelve a intentar más tarde.
        if (!await EnsureBrowsersAsync()) return;
        token.ThrowIfCancellationRequested();

        // Solo "Otro" requiere inicio de sesión, y solo si su carpeta de perfil todavía no existe.
        // Si la sesión de la web caduca, se repite con el botón de volver a iniciar sesión.
        if (platform == ChatPlatform.Otro && !Directory.Exists(profileDir))
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_FirstTimeLogin"), platform));
            await PerformLoginWithUIAsync(url, profileDir, token);
            Console.WriteLine(string.Format(I18n.Tr("Log_RestartingHidden"), platform));
        }

        using var playwright = await Playwright.CreateAsync();

        // =======================================================================
        // OPCIONES DE CHROMIUM: que la web no se congele en modo headless
        // =======================================================================
        // En modo headless la pestaña se considera oculta, y Chromium ralentiza los temporizadores
        // y el renderizado de las pestañas ocultas: la web del chat dejaría de actualizarse a
        // tiempo. Los cuatro últimos argumentos lo desactivan. El UserAgent es el de Chrome de
        // escritorio porque el de headless incluye "HeadlessChrome" y algunas webs lo bloquean
        // (ver ObtenerUserAgentAsync).
        string userAgent = await ObtenerUserAgentAsync(playwright);
        token.ThrowIfCancellationRequested();

        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = true,
            UserAgent = userAgent,
            Args = new[] {
                "--disable-blink-features=AutomationControlled",
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
                "--disable-features=CalculateNativeWinOcclusion"
            }
        };

        var context = await playwright.Chromium.LaunchPersistentContextAsync(profileDir, options);

        // Al salir, el contexto se cierra sin lanzar: si Chromium ya se había cerrado, el error de
        // cerrarlo otra vez no aporta nada y taparía el motivo real.
        await using var cierre = new CierreSilencioso(context);

        // Detener el enlace cierra Chromium: cualquier llamada de Playwright en curso lanza una
        // excepción y la sesión termina.
        using var alCancelar = token.Register(() => _ = CerrarSinErroresAsync(context));
        token.ThrowIfCancellationRequested();

        var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

        // Se completa si Chromium o la página se cierran, o si la pestaña se cuelga.
        var cerrada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Close += (_, _) => cerrada.TrySetResult();
        page.Close += (_, _) => cerrada.TrySetResult();
        page.Crash += (_, _) => cerrada.TrySetResult();

        // Los init scripts se vuelven a aplicar en cada carga de la página; lo inyectado con
        // EvaluateAsync, no.
        await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

        // Segundos de cada carga en los que se ignora lo que llega por los puentes.
        // SocialStream.Ninja no los necesita: su script descarta el historial por su cuenta.
        // StreamElements tampoco usa estos puentes: tiene los suyos (ver StreamElementsListener).
        // En YouTube y Kick es una segunda protección: sus scripts ya descartan el historial.
        int graciaMs = platform switch
        {
            ChatPlatform.SheepChat => 5_000,
            ChatPlatform.Kick => 5_000,
            ChatPlatform.YouTube => 6_000,
            ChatPlatform.Otro => 15_000,
            _ => 0
        };
        var puerta = new Puerta();
        page.DOMContentLoaded += (_, _) => puerta.AbrirDentroDe(graciaMs);

        // PUENTE 1: UNIVERSAL (para "Otro")
        // Lo llama el script de GenericListener.GetObserverScript con el texto sin procesar de cada
        // mensaje. El usuario y el nivel se obtienen con la heurística de TryParseGenericMessage.
        // Las funciones expuestas se mantienen tras recargar la página. El JavaScript no usa el
        // valor devuelto.
        await page.ExposeFunctionAsync("sendToCSharp", (string chatText) =>
        {
            if (!puerta.EstaAbierta) return false;

            try
            {
                if (GenericListener.TryParseGenericMessage(chatText, MainWindowViewModel.Instance.SettingsVM.Settings.RequestCommands, out string user, out string levelQuery))
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_UniversalCmdDetected"), user, levelQuery));
                    // La búsqueda en GD es lenta: se ejecuta aparte para no retener a Playwright.
                    _ = Task.Run(() => GenericListener.ProcessLevelRequestAsync(levelQuery, user, platform, url));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_UniversalError"), ex.Message));
            }

            return true;
        });

        // PUENTE 2: DEDICADO (SheepChat, SocialStream.Ninja, YouTube y Kick)
        // Recibe un JSON {platform, user, text} ya separado por el script de la web, así que no
        // hace falta deducir el usuario. El texto pasa por el parser común de GenericListener.
        await page.ExposeFunctionAsync("sendDedicatedData", (string jsonPayload) =>
        {
            if (!puerta.EstaAbierta) return false;

            try
            {
                using var doc = JsonDocument.Parse(jsonPayload);
                var root = doc.RootElement;
                string sourcePlatform = LeerTexto(root, "platform");
                if (sourcePlatform.Length == 0) sourcePlatform = "global";
                string user = LeerTexto(root, "user");
                if (user.Length == 0) user = I18n.Tr("Default_Viewer");
                string chatText = LeerTexto(root, "text");

                var tipo = GenericListener.TryParseCommand(chatText, out string query);
                if (tipo == GenericListener.TipoMensaje.Nada) return true;

                Console.WriteLine(string.Format(I18n.Tr("Log_DedicatedMsg"), user, sourcePlatform, GenericListener.LimpiarTexto(chatText)));
                Console.WriteLine(tipo == GenericListener.TipoMensaje.Comando
                    ? string.Format(I18n.Tr("Log_DedicatedCmdValidated"), query)
                    : string.Format(I18n.Tr("Log_DedicatedAutoId"), query));

                // El último parámetro es la plataforma real que indica el agregador (twitch,
                // kick...), para el icono.
                _ = Task.Run(() => GenericListener.ProcessLevelRequestAsync(query, user, platform, sourcePlatform));
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_DedicatedError"), ex.Message));
            }

            return true;
        });

        // =======================================================================
        // CAPTURA SEGÚN LA PLATAFORMA
        // =======================================================================
        // Los scripts se registran como init scripts antes de cargar la página, así que se vuelven
        // a aplicar si la web se recarga. YouTube y Kick no abren el enlace del usuario sino el
        // chat emergente de la plataforma (ver ChatsDeStreaming).
        string destino = url;

        // Solo YouTube con el enlace de un canal: la página "en directo" del canal, que el
        // watchdog vuelve a consultar para seguir al directo cuando termina o cambia.
        string? urlEnDirecto = null;
        string? idVideo = null;

        switch (platform)
        {
            case ChatPlatform.Kick:
                destino = ChatsDeStreaming.UrlDeChatDeKick(url)!;
                Console.WriteLine(string.Format(I18n.Tr("Log_ConnectedInjectingNinja"), platform));
                await page.AddInitScriptAsync(AlCargarEnDocumentoPrincipal(ChatsDeStreaming.ScriptKick));
                break;

            case ChatPlatform.YouTube:
                await context.AddCookiesAsync(new[] { ChatsDeStreaming.CookieDeConsentimiento });
                idVideo = ChatsDeStreaming.IdDeVideoDeYouTube(url);
                if (idVideo == null)
                {
                    urlEnDirecto = ChatsDeStreaming.UrlEnDirectoDeYouTube(url)!;
                    string canal = CanalDeYouTube(urlEnDirecto);
                    Console.WriteLine(string.Format(I18n.Tr("Log_YouTubeSearchingLive"), canal));
                    idVideo = await ChatsDeStreaming.BuscarDirectoDeYouTubeAsync(context, urlEnDirecto, token);

                    // Sin directo, la sesión falla y el bucle vuelve a mirar con su espera
                    // creciente (hasta 2 minutos): el chat se conecta solo al empezar el directo.
                    if (idVideo == null)
                        throw new InvalidOperationException(string.Format(I18n.Tr("Log_YouTubeNotLive"), canal));
                    Console.WriteLine(string.Format(I18n.Tr("Log_YouTubeLiveFound"), idVideo));
                }
                destino = ChatsDeStreaming.UrlDeChatDeYouTube(idVideo);
                Console.WriteLine(string.Format(I18n.Tr("Log_ConnectedInjectingNinja"), platform));
                await page.AddInitScriptAsync(AlCargarEnDocumentoPrincipal(ChatsDeStreaming.ScriptYouTube));
                break;

            case ChatPlatform.SheepChat:
                Console.WriteLine(string.Format(I18n.Tr("Log_ConnectedInjectingEye"), platform));
                await page.AddInitScriptAsync(AlCargarEnDocumentoPrincipal(SheepChatScript));
                break;

            case ChatPlatform.SocialStreamNinja:
                Console.WriteLine(string.Format(I18n.Tr("Log_ConnectedInjectingNinja"), platform));
                await page.AddInitScriptAsync(SocialStreamListener.GetObserverScript());
                break;

            case ChatPlatform.StreamElements:
                Console.WriteLine(string.Format(I18n.Tr("Log_ConnectedInjectingNinja"), platform));
                await StreamElementsListener.AttachToPageAsync(page, platform);
                // El script descarta el historial por su cuenta, en sus primeros 5 s.
                graciaMs = StreamElementsListener.EsperaHistorialMs;
                break;

            default:
                Console.WriteLine(string.Format(I18n.Tr("Log_ConnectedInjectingNinja"), platform));
                await page.AddInitScriptAsync(AlCargarEnDocumentoPrincipal(GenericListener.GetObserverScript()));
                break;
        }

        await page.GotoAsync(destino, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // La espera solo retrasa el mensaje de "activo" hasta que de verdad lo está: la puerta ya
        // se cierra y se abre sola en cada carga.
        if (graciaMs > 0)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_CleaningHistory"), platform, graciaMs / 1000));
            await Task.Delay(graciaMs, token);
        }

        Console.WriteLine(string.Format(I18n.Tr("Log_InterceptorActive"), platform));
        alActivarse();

        // WATCHDOG
        // Además de vigilar, este bucle es lo que mantiene la sesión abierta, y con ella Chromium.
        // Termina cuando Chromium o la página se cierran, o al detener el enlace.
        int vueltas = 0;
        int directoDistinto = 0;
        while (true)
        {
            vueltas++;
            await Task.WhenAny(cerrada.Task, Task.Delay(20000, token));
            token.ThrowIfCancellationRequested();

            if (cerrada.Task.IsCompleted || page.IsClosed)
            {
                Console.WriteLine(string.Format(I18n.Tr("Log_BrowserClosed"), platform));
                return;
            }

            // En "Otro", si la web se queda en blanco, se recarga; el init script vuelve a poner el
            // observador. Un fallo aquí (por ejemplo, porque la web navegaba justo en ese momento)
            // se apunta y la vigilancia sigue.
            if (platform == ChatPlatform.Otro)
            {
                try
                {
                    string bodyText = await page.EvaluateAsync<string>("() => document.body ? document.body.innerText.trim() : ''");

                    if (string.IsNullOrEmpty(bodyText))
                    {
                        Console.WriteLine(I18n.Tr("Log_WatchdogBlank"));
                        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                        Console.WriteLine(I18n.Tr("Log_WatchdogRestored"));
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested && !page.IsClosed)
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_WatchdogError"), platform, ex.Message));
                }
            }

            // En YouTube con el enlace de un canal, cada 3 minutos (9 vueltas de 20 s) se mira si
            // el directo sigue siendo el mismo. Si termina o empieza otro, la sesión se cierra y
            // el bucle de reconexión busca el nuevo. Hacen falta dos resultados distintos seguidos:
            // una sola consulta fallida (YouTube lento, un corte de red) no corta un chat que va
            // bien.
            if (urlEnDirecto != null && vueltas % 9 == 0)
            {
                string? actual;
                try { actual = await ChatsDeStreaming.BuscarDirectoDeYouTubeAsync(context, urlEnDirecto, token); }
                catch (Exception) when (!token.IsCancellationRequested) { actual = idVideo; }
                token.ThrowIfCancellationRequested();

                directoDistinto = actual == idVideo ? 0 : directoDistinto + 1;
                if (directoDistinto >= 2)
                {
                    Console.WriteLine(string.Format(I18n.Tr("Log_YouTubeLiveChanged"), CanalDeYouTube(urlEnDirecto)));
                    return;
                }
            }
        }
    }

    /// <summary>
    /// El canal de una página "en directo" de YouTube, para los mensajes de la consola:
    /// "https://www.youtube.com/@canal/live" da "@canal".
    /// </summary>
    private static string CanalDeYouTube(string urlEnDirecto) =>
        urlEnDirecto.Replace("https://www.youtube.com/", "").Replace("/live", "");

    // ================================================================
    // USERAGENT
    // ================================================================
    /// <summary>
    /// UserAgent de respaldo, si no se puede leer la versión real de Chromium.
    /// </summary>
    private const string UserAgentDeRespaldo =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    // Se calcula una vez por sesión de la aplicación. Un fallo no se guarda: se reintenta en la
    // siguiente conexión.
    private static string? _userAgent;
    private static readonly SemaphoreSlim UserAgentLock = new(1, 1);

    /// <summary>
    /// UserAgent de Chrome de escritorio con la versión real del Chromium instalado y el sistema
    /// real, con el formato reducido que usa Chrome: solo la versión principal
    /// ("Chrome/151.0.0.0") y una plataforma fija por sistema. Así coincide con lo demás que ve
    /// la web (por ejemplo, navigator.platform); un Chrome antiguo o un Windows que en realidad
    /// es Linux son incoherencias que algunas webs usan para detectar bots.
    ///
    /// La versión se lee abriendo una vez un Chromium invisible aparte (alrededor de un segundo):
    /// el contexto persistente que usan las fuentes no da acceso al navegador ni a su versión. Si
    /// falla, se usa <see cref="UserAgentDeRespaldo"/>.
    /// </summary>
    private static async Task<string> ObtenerUserAgentAsync(IPlaywright playwright)
    {
        if (_userAgent != null) return _userAgent;

        await UserAgentLock.WaitAsync();
        try
        {
            if (_userAgent != null) return _userAgent;

            await using var navegador = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            string principal = navegador.Version.Split('.')[0];
            if (!int.TryParse(principal, out _)) throw new FormatException(navegador.Version);

            string sistema =
                OperatingSystem.IsWindows() ? "Windows NT 10.0; Win64; x64" :
                OperatingSystem.IsMacOS() ? "Macintosh; Intel Mac OS X 10_15_7" :
                "X11; Linux x86_64";

            _userAgent = $"Mozilla/5.0 ({sistema}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{principal}.0.0.0 Safari/537.36";
            return _userAgent;
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_UserAgentFallback"), ex.Message));
            return UserAgentDeRespaldo;
        }
        finally
        {
            UserAgentLock.Release();
        }
    }

    /// <summary>
    /// Lee una propiedad de texto de un JSON; vacío si falta o no es texto.
    /// </summary>
    private static string LeerTexto(JsonElement root, string propiedad) =>
        root.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String
            ? (valor.GetString() ?? "").Trim()
            : "";

    /// <summary>
    /// Cierra el contexto al liberarse, ignorando los errores (ver
    /// <see cref="CerrarSinErroresAsync"/>).
    /// </summary>
    private sealed class CierreSilencioso : IAsyncDisposable
    {
        private readonly IBrowserContext _context;
        public CierreSilencioso(IBrowserContext context) => _context = context;
        public ValueTask DisposeAsync() => new(CerrarSinErroresAsync(_context));
    }

    /// <summary>
    /// Cierra un contexto de Playwright ignorando los errores: si ya estaba cerrado, CloseAsync
    /// lanza una excepción, y el objetivo era cerrarlo.
    /// </summary>
    private static async Task CerrarSinErroresAsync(IBrowserContext context)
    {
        try { await context.CloseAsync(); } catch { }
    }
}
