using System.Collections.Generic;

namespace SheepGDRequest.Models;

/// <summary>
/// Estado de la cola: el nivel actual, el historial y los niveles en espera. QueueFileService lo
/// guarda en queue_data.json, que conserva la cola entre sesiones y del que lee el mod de Geode.
/// </summary>
public class QueueState
{
    /// <summary>
    /// Nivel que se está jugando. Sin nivel, es un marcador de espera con el nombre "WAITING...",
    /// que no se debe traducir: lo comprueban esta aplicación y el mod.
    /// </summary>
    public LevelRequestModel CurrentLevel { get; set; } = null!;

    /// <summary>
    /// Niveles ya jugados, el más reciente al final. Lo llenan NextLevel y RandomLevel, y
    /// PreviousLevel recupera de aquí el nivel anterior. QueueFileService lo recorta a los últimos
    /// <see cref="Services.QueueFileService.MaxHistorial"/> al guardar; el mod solo lo usa para
    /// volver al nivel anterior. También cuenta para el filtro de repetidos (GenericListener), y
    /// "Borrar todos los niveles enviados" lo vacía.
    /// </summary>
    public List<LevelRequestModel> History { get; set; } = new();

    /// <summary>
    /// Niveles en espera, en orden. Solo entran los que pasan los filtros de GenericListener.
    /// </summary>
    public List<LevelRequestModel> Queue { get; set; } = new();
}
