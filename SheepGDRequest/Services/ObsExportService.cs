using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using SheepGDRequest.Models;

namespace SheepGDRequest.Services;

/// <summary>
/// Exporta el estado de la cola a archivos de texto plano (.txt) pensados para las fuentes de
/// texto de OBS Studio, que los leen en tiempo real.
///
/// Lo llama MainWindowViewModel cada vez que cambia la cola, si la exportación para OBS está
/// activa en Ajustes (ExportObsTexts). Los nombres de archivo (NivelActual, SiguienteNivel,
/// ColaCompleta) son fijos y no se traducen a propósito: las fuentes de OBS apuntan a esos
/// nombres, y cambiarlos con el idioma dejaría la escena sin datos.
/// </summary>
public class ObsExportService
{
    // Carpeta donde se guardan los archivos de texto.
    private readonly string _exportPath = AppPaths.ObsExportsDir;

    /// <summary>
    /// Genera o actualiza los archivos de texto para OBS a partir del estado actual de la cola.
    /// </summary>
    /// <param name="currentLevel">El nivel que se está jugando.</param>
    /// <param name="queue">La lista de niveles en espera.</param>
    /// <param name="isEnabled">Si es false, el método no hace nada.</param>
    public void UpdateObsFiles(LevelRequestModel? currentLevel, ObservableCollection<LevelRequestModel> queue, bool isEnabled)
    {
        if (!isEnabled) return;

        try
        {
            // Crea la carpeta de exportación si no existe.
            if (!Directory.Exists(_exportPath))
                Directory.CreateDirectory(_exportPath);

            // ==========================================
            // 1. TEXTO DEL NIVEL ACTUAL
            // ==========================================
            // "WAITING..." indica que no hay nivel; no es el nombre de un nivel (ver
            // QueueFileService).
            string currentText = currentLevel == null || currentLevel.Name == "WAITING..." || string.IsNullOrEmpty(currentLevel.Name)
                ? I18n.Tr("Obs_WaitingLevels")
                : string.Format(I18n.Tr("Obs_PlayingFormat"), currentLevel.Name, currentLevel.DifficultyName, currentLevel.LevelId, currentLevel.RequesterName);

            File.WriteAllText(Path.Combine(_exportPath, "NivelActual.txt"), currentText);

            // ==========================================
            // 2. TEXTO DEL SIGUIENTE NIVEL
            // ==========================================
            string nextText = queue.Count > 0
                ? string.Format(I18n.Tr("Obs_NextFormat"), queue[0].Name, queue[0].RequesterName)
                : I18n.Tr("Obs_NextEmpty");

            File.WriteAllText(Path.Combine(_exportPath, "SiguienteNivel.txt"), nextText);

            // ==========================================
            // 3. TEXTO DE LA COLA COMPLETA
            // ==========================================
            StringBuilder fullQueueBuilder = new StringBuilder();

            if (queue.Count > 0)
            {
                // Cada nivel de la cola, numerado.
                for (int i = 0; i < queue.Count; i++)
                {
                    fullQueueBuilder.AppendLine(string.Format(I18n.Tr("Obs_QueueItemFormat"), i + 1, queue[i].Name, queue[i].RequesterName));
                }
            }
            else
            {
                fullQueueBuilder.AppendLine(I18n.Tr("Obs_QueueEmpty"));
            }

            File.WriteAllText(Path.Combine(_exportPath, "ColaCompleta.txt"), fullQueueBuilder.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(I18n.Tr("Log_ObsExportError"), ex.Message));
        }
    }
}
