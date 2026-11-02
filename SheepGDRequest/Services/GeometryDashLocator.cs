using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SheepGDRequest.Services;

/// <summary>
/// Intenta localizar la instalación de Geometry Dash leyendo el libraryfolders.vdf de Steam.
/// Es solo una sugerencia para el selector de carpeta: si no la encuentra, el usuario la elige
/// a mano. La usa SettingsViewModel.InstallGeodeMod cuando no hay una carpeta guardada.
/// </summary>
public static class GeometryDashLocator
{
    /// <summary>
    /// Devuelve la carpeta de Geometry Dash, o null si no la encuentra.
    /// </summary>
    public static string? Detectar()
    {
        foreach (string vdf in RutasDeLibraryFolders())
        {
            if (!File.Exists(vdf)) continue;

            try
            {
                string texto = File.ReadAllText(vdf);

                // En lugar de analizar el bloque "apps" buscando el App ID 322170, se extraen las
                // rutas de las bibliotecas y se comprueba en disco. Requiere menos análisis y,
                // además, descarta las bibliotecas que el .vdf todavía lista pero que ya no
                // contienen el juego.
                foreach (Match m in Regex.Matches(texto, "\"path\"\\s+\"([^\"]+)\""))
                {
                    // El .vdf de Windows escapa las barras invertidas.
                    string raiz = m.Groups[1].Value.Replace(@"\\", @"\");
                    string candidata = Path.Combine(raiz, "steamapps", "common", "Geometry Dash");

                    if (Directory.Exists(candidata)) return candidata;
                }
            }
            catch { /* .vdf ilegible: seguimos con el siguiente */ }
        }

        return null;
    }

    /// <summary>
    /// Ubicaciones posibles del libraryfolders.vdf. Basta con encontrar uno: el archivo lista todas
    /// las bibliotecas, incluidas las de otros discos.
    /// </summary>
    private static IEnumerable<string> RutasDeLibraryFolders()
    {
        const string archivo = "libraryfolders.vdf";

        if (OperatingSystem.IsWindows())
        {
            // Primero, donde está instalado Steam según el propio Steam (puede estar en otro disco,
            // como D:\Steam); después, las dos carpetas de Program Files.
            var instalaciones = new List<string>();
            string? registrado = LeerSteamPathDelRegistro();
            if (!string.IsNullOrWhiteSpace(registrado)) instalaciones.Add(registrado);
            instalaciones.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            instalaciones.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

            foreach (string steam in instalaciones)
            {
                yield return Path.Combine(steam, "steamapps", archivo);
                yield return Path.Combine(steam, "config", archivo);
            }

            yield break;
        }

        // Linux: el Steam de los paquetes de la distribución (.steam y .local/share), el de
        // Flatpak y el de Snap (el que instala la tienda de Ubuntu). En Linux GD se ejecuta con
        // Proton, pero la carpeta del juego sigue estando en steamapps/common. En macOS ninguna de
        // estas rutas existe, así que se abre el selector.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] carpetas =
        {
            ".steam/steam/steamapps",
            ".steam/steam/config",
            ".local/share/Steam/steamapps",
            ".local/share/Steam/config",
            ".var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps",  // Flatpak
            ".var/app/com.valvesoftware.Steam/.local/share/Steam/config",     // Flatpak
            "snap/steam/common/.local/share/Steam/steamapps",                 // Snap
            "snap/steam/common/.local/share/Steam/config",                    // Snap
        };

        foreach (string carpeta in carpetas)
        {
            yield return Path.Combine(home, carpeta.Replace('/', Path.DirectorySeparatorChar), archivo);
        }
    }

    /// <summary>
    /// Carpeta de Steam que el propio Steam guarda en el registro de Windows
    /// (HKEY_CURRENT_USER\Software\Valve\Steam, valor SteamPath), o null si no está.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? LeerSteamPathDelRegistro()
    {
        try
        {
            string? ruta = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            // Steam la guarda con barras normales ("c:/program files (x86)/steam").
            return string.IsNullOrWhiteSpace(ruta) ? null : Path.GetFullPath(ruta);
        }
        catch
        {
            return null;
        }
    }
}
