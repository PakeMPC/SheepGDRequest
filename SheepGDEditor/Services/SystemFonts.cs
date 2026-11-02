using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Platform;
using SkiaSharp;

namespace SheepGDEditor.Services;

/// <summary>
/// Obtiene el ARCHIVO de una fuente instalada en el sistema para poder meterlo dentro del tema.
///
/// Por qué hace falta: una fuente elegida de la lista del sistema viajaría al widget solo como
/// NOMBRE de familia CSS, y el navegador de OBS la buscaría entre las fuentes instaladas del
/// equipo donde corre. En el equipo del autor se vería perfecta y en cualquier otro caería a
/// sans-serif sin ningún aviso. Con el archivo dentro del tema, se ve igual en todas partes.
///
/// Ofrece dos operaciones, separadas a propósito porque cuestan muy distinto:
///  - <see cref="Comprobar"/>: dice si la fuente se puede incrustar, leyendo solo los 4 primeros
///    bytes del archivo. La usa el panel de propiedades cada vez que cambia la fuente, para
///    avisar antes de guardar; recorrer el desplegable con las flechas no puede escribir en
///    disco los 20 MB de una fuente asiática por cada fila.
///  - <see cref="Extraer"/>: copia el archivo entero a una carpeta temporal. Solo se usa al
///    guardar el tema.
/// Las dos guardan su resultado por familia: cada fuente se comprueba una sola vez por sesión, y
/// solo se vuelve a extraer si su archivo temporal ha desaparecido.
///
/// Es seguro llamarla desde varios hilos: todo el estado compartido va protegido por un candado.
/// </summary>
public static class FuentesDelSistema
{
    /// <summary>
    /// Resultado de <see cref="Comprobar"/>.
    /// </summary>
    /// <param name="SePuede">Si la fuente se puede incrustar en el tema.</param>
    /// <param name="Extension">Extensión que tendrá el archivo extraído (".ttf" u ".otf"), o
    /// vacía si no se puede.</param>
    /// <param name="Motivo">Si no se puede, el porqué, ya traducido y listo para mostrarlo al
    /// usuario. Vacío si se puede.</param>
    public sealed record Comprobacion(bool SePuede, string Extension, string Motivo);

    // Resultados ya calculados, por nombre de familia (sin distinguir mayúsculas): el de
    // Comprobar y la ruta del archivo que dejó Extraer.
    private static readonly Dictionary<string, Comprobacion> _comprobadas = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _extraidas = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cerrojo = new();

    // Carpeta temporal donde se dejan las fuentes extraídas hasta que se guardan en el tema.
    private static readonly string _carpeta = Path.Combine(Path.GetTempPath(), "SheepGDEditor_fuentes");

    // Inter NO es una fuente del sistema: la lleva Avalonia dentro (.WithInterFont() en
    // Program.cs), y es la fuente por defecto de todo texto nuevo, así que hay que poder
    // incrustarla igual que las demás. Comprobado en el paquete Avalonia.Fonts.Inter 12.1.1: el
    // archivo es /Assets/Inter-Regular.ttf. Su licencia (SIL Open Font License) permite
    // redistribuirla dentro de los temas.
    private static readonly Uri _inter = new("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf");

    private static bool EsInter(string familia) => familia.Equals("Inter", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dice si la familia indicada se puede incrustar en el tema y, si no, por qué. Es barata:
    /// no lee la fuente entera, y cada familia se comprueba una sola vez.
    /// </summary>
    /// <param name="familia">Nombre de la familia, tal como sale en la lista de fuentes.</param>
    public static Comprobacion Comprobar(string? familia)
    {
        if (string.IsNullOrWhiteSpace(familia)) return new(false, "", EditorI18n.Tr("UI_FontReason_NoneChosen"));

        lock (_cerrojo)
        {
            if (_comprobadas.TryGetValue(familia, out var hecha)) return hecha;
            var r = ComprobarSinCache(familia);
            _comprobadas[familia] = r;
            return r;
        }
    }

    /// <summary>
    /// Copia el archivo de la fuente a la carpeta temporal y devuelve su ruta, o null si la
    /// fuente no se puede incrustar o algo falla al copiarla. Si ya se extrajo antes y el
    /// archivo sigue ahí, devuelve el mismo sin volver a copiarlo.
    /// </summary>
    /// <param name="familia">Nombre de la familia, tal como sale en la lista de fuentes.</param>
    public static string? Extraer(string? familia)
    {
        var c = Comprobar(familia);
        if (!c.SePuede) return null;

        lock (_cerrojo)
        {
            if (_extraidas.TryGetValue(familia!, out var ruta) && File.Exists(ruta)) return ruta;

            try
            {
                byte[]? datos = LeerEntera(familia!);
                if (datos == null) return null;

                Directory.CreateDirectory(_carpeta);
                // El nombre del archivo va sin espacios ni símbolos porque acaba dentro de una
                // clave de URI ("fonts:sheepgd/<nombre>", ver OnCustomFontPathChanged en
                // WidgetItemViewModel) y en una ruta dentro del ZIP. Ver NombreDeArchivo.
                string nombre = "sistema_" + NombreDeArchivo(familia!) + c.Extension;
                ruta = Path.Combine(_carpeta, nombre);
                File.WriteAllBytes(ruta, datos);

                _extraidas[familia!] = ruta;
                return ruta;
            }
            catch (Exception ex)
            {
                // No se interrumpe el guardado: el texto se queda con su fuente por nombre, y el
                // motivo queda en la consola.
                Console.WriteLine(EditorI18n.Trf("Log_FontExtractError", familia, ex.GetType().Name, ex.Message));
                return null;
            }
        }
    }

    /// <summary>
    /// La comprobación de verdad, sin mirar los resultados guardados. Busca la fuente, confirma
    /// que es exactamente la pedida y mira el formato del archivo por su firma.
    /// </summary>
    private static Comprobacion ComprobarSinCache(string familia)
    {
        try
        {
            if (EsInter(familia))
            {
                return AssetLoader.Exists(_inter, null)
                    ? new(true, ".ttf", "")
                    : new(false, "", EditorI18n.Tr("UI_FontReason_InterMissing"));
            }

            using var tf = SKFontManager.Default.MatchFamily(familia);
            if (tf == null) return new(false, "", EditorI18n.Tr("UI_FontReason_NotFound"));

            // La documentación de SkiaSharp promete null cuando la familia no existe, pero quien
            // responde de verdad es el gestor de fuentes de cada sistema operativo, y no se ha
            // podido comprobar en los tres. Si alguno devolviera una fuente SUSTITUTA, se
            // incrustaría otra fuente con el nombre de ésta: el peor fallo posible, porque no se
            // nota hasta verlo en el directo. Esta comparación lo impide.
            if (!string.Equals(tf.FamilyName, familia, StringComparison.OrdinalIgnoreCase))
                return new(false, "", EditorI18n.Trf("UI_FontReason_Substitute", tf.FamilyName));

            using var stream = tf.OpenStream(out _);
            if (stream == null) return new(false, "", EditorI18n.Tr("UI_FontReason_Unreadable"));

            var firma = new byte[4];
            int leidos = stream.Read(firma, 4);
            if (leidos < 4) return new(false, "", EditorI18n.Tr("UI_FontReason_EmptyFile"));

            return PorFirma(firma);
        }
        catch (Exception ex)
        {
            // Cualquier fallo inesperado se trata como "no se puede", con el error como motivo.
            return new(false, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Decide el formato de la fuente por sus 4 primeros bytes (la "firma" del archivo).
    ///
    /// La extensión sale del CONTENIDO y no del nombre del archivo. Al navegador le daría igual
    /// —el @font-face de engine.js no indica el formato y Chromium lo deduce de los bytes—, pero
    /// el servidor de widgets de SheepGDRequest decide el tipo MIME por la extensión, y conviene
    /// que diga la verdad.
    /// </summary>
    private static Comprobacion PorFirma(byte[] f)
    {
        // TrueType: 00 01 00 00, o "true" en las fuentes antiguas de Apple.
        if (f[0] == 0x00 && f[1] == 0x01 && f[2] == 0x00 && f[3] == 0x00) return new(true, ".ttf", "");
        if (f[0] == 't' && f[1] == 'r' && f[2] == 'u' && f[3] == 'e') return new(true, ".ttf", "");
        // OpenType con contornos CFF.
        if (f[0] == 'O' && f[1] == 'T' && f[2] == 'T' && f[3] == 'O') return new(true, ".otf", "");

        // Colección de fuentes (.ttc): varias fuentes en un solo archivo. Así vienen muchas de
        // Windows y las asiáticas de Linux. No hay garantía de que Chromium cargue una colección
        // desde un @font-face, así que se rechaza explicando el motivo.
        if (f[0] == 't' && f[1] == 't' && f[2] == 'c' && f[3] == 'f')
            return new(false, "", EditorI18n.Tr("UI_FontReason_Collection"));

        return new(false, "", EditorI18n.Tr("UI_FontReason_UnknownFormat"));
    }

    /// <summary>
    /// Lee el archivo completo de la fuente a memoria, o null si el sistema no lo da.
    /// </summary>
    private static byte[]? LeerEntera(string familia)
    {
        if (EsInter(familia))
        {
            using var s = AssetLoader.Open(_inter, null);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        using var tf = SKFontManager.Default.MatchFamily(familia);
        if (tf == null) return null;
        using var stream = tf.OpenStream(out _);
        if (stream == null) return null;

        // Se lee por trozos hasta el final, sin fiarse de stream.Length: la documentación de
        // SkiaSharp avisa de que vale 0 cuando el flujo no conoce su tamaño.
        using var salida = new MemoryStream();
        var trozo = new byte[64 * 1024];
        int n;
        while ((n = stream.Read(trozo, trozo.Length)) > 0) salida.Write(trozo, 0, n);
        return salida.ToArray();
    }

    /// <summary>
    /// El nombre de archivo de una familia, distinto para cada familia. Si el nombre ya es seguro
    /// (solo letras, números y "_"), se usa tal cual: "Arial" → "Arial". Si no, se pasa por
    /// NombreSeguro y se le añade una huella del nombre EXACTO: "Open Sans" →
    /// "Open_Sans_1a2b3c4d".
    ///
    /// La huella hace falta porque NombreSeguro junta nombres distintos: "Foo Bar" y "Foo-Bar"
    /// daban los dos "Foo_Bar", la segunda extracción sobrescribía el archivo de la primera, y al
    /// guardar los textos de una fuente se incrustaban con la otra sin ningún aviso. Es SHA-256 y
    /// no GetHashCode a propósito: GetHashCode cambia en cada ejecución, y la misma fuente
    /// acabaría con un nombre distinto dentro del ZIP cada vez que se guardara el tema.
    /// </summary>
    private static string NombreDeArchivo(string familia)
    {
        string seguro = NombreSeguro(familia);
        if (seguro == familia) return seguro;

        byte[] sha = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(familia));
        return seguro + "_" + Convert.ToHexString(sha, 0, 4).ToLowerInvariant();
    }

    /// <summary>
    /// Convierte el nombre de la familia en algo usable como nombre de archivo: las letras y los
    /// números se conservan y todo lo demás pasa a "_" ("Open Sans" → "Open_Sans"). Nombres
    /// distintos pueden dar el mismo resultado: por eso Extraer usa NombreDeArchivo.
    /// </summary>
    private static string NombreSeguro(string familia)
    {
        var sb = new System.Text.StringBuilder(familia.Length);
        foreach (char c in familia) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
