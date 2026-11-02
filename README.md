# SheepGD

Un ecosistema para creadores de contenido de **Geometry Dash** que recibe las peticiones de
niveles del chat en directo, las filtra, las muestra en OBS y las lleva al propio juego. Funciona
entero en tu ordenador: no hay servidores propios ni cuentas.

```
Un espectador escribe "!add 128" en Twitch, YouTube, Kick, TikTok o Discord
        │
        ▼
[SheepGDRequest]  lee el chat → antispam → busca el nivel en GD, GDDL y AREDL
        │         → aplica tus filtros → lo añade a la cola
        ▼
   queue_data.json  ◄──────── el archivo que comparten las tres piezas ────────┐
        │                                                                      │
        ├──► widgets de OBS (servidor local, puerto 6969)                      │
        │                                                                      │
        └──► [SheepGDintegration] muestra la cola dentro de GD y, al saltar o  ┘
             borrar un nivel desde el juego, reescribe el archivo
```

La cola vive en un solo archivo, `queue_data.json`, que leen y escriben el programa, los widgets y
el mod. Por eso da igual desde dónde la controles: lo que hagas en el juego aparece al instante en
el programa y en OBS, y al revés.

---

## Las tres piezas

| Pieza | Qué es | Tecnología |
|---|---|---|
| **SheepGDRequest** | El programa principal: escucha los chats, filtra las peticiones, gestiona la cola y sirve los widgets de OBS | C# · .NET 8 · Avalonia 12 |
| **SheepGDEditor** | Editor visual para diseñar los temas de los widgets sin escribir HTML | C# · .NET 8 · Avalonia 12 |
| **SheepGDintegration** | Mod de Geode que lleva la cola dentro de Geometry Dash | C++23 · Geode 5.9.0 · GD 2.2081 |

---

## SheepGDRequest

### Fuentes de chat

Puedes conectar hasta diez enlaces a la vez, de cualquier mezcla de fuentes:

| Fuente | Qué pegar | Cómo lee el chat |
|---|---|---|
| **SheepChat** | La URL del chat | Navegador interno invisible, con lector propio |
| **SocialStream.Ninja** | La URL de la página de SSN | Navegador interno; reúne Twitch, YouTube, Kick… en una sola fuente |
| **StreamElements** | La URL del overlay de chat | Navegador interno; ignora solo el historial que aparece al cargar |
| **TikTok Live** | `@usuario` o el enlace del directo | API de TikTok, sin navegador |
| **Discord** | El enlace del canal | Un bot tuyo (el token se pega en Ajustes) |
| **Otro** | Cualquier web de chat | Navegador interno; la primera vez abre una ventana para iniciar sesión |

- **Se recuperan solas.** Si una fuente se cae (la web se recarga, el navegador se cierra, se va
  la red), vuelve a conectar con una espera creciente y te avisa de la caída y de la vuelta. TikTok
  hace lo mismo con su propio bucle.
- **Discord con moderación.** Con "Listar solo niveles reaccionados", un nivel solo entra en la
  cola cuando alguien reacciona al mensaje con ✅ ☑️ ✔️ ♥️ ❤️ o 👍; quién puede moderar se controla con
  los permisos del canal. Si se borra el mensaje, su nivel sale de la cola.
- **Sesiones de "Otro".** Cada enlace guarda su sesión. Si caduca, el botón **Sesión** de ese
  enlace la borra y vuelve a abrir el inicio de sesión, sin tocar las demás. **Borrar caché** borra
  todas y vuelve a conectar.

### Peticiones y filtros

- **Comandos propios** (hasta tres, por defecto `!add`). Con comillas se pueden pedir nombres con
  espacios: `!add "Bloodbath"`.
- **Detección automática de IDs:** un número de 5 a 10 cifras en el chat se toma como petición,
  sin comando.
- **Antispam:** ignora las peticiones repetidas del mismo usuario en 3 segundos (por ejemplo, el
  mismo mensaje llegando por dos fuentes).
- **Reglas de la cola:**
  - límite de niveles por usuario y límite total de la cola;
  - solo niveles con rate, permitir o no los repetidos, modo estricto solo por ID;
  - **filtro de dificultad**: *Solo*, *Todas excepto*, *Máximo hasta* o *Mínimo a partir de* una
    dificultad, de Auto a Extreme Demon (o "Cualquier Demon");
  - **lista negra** de IDs.
- **Pausar las peticiones** con un botón, sin desconectar nada.

### Datos de cada nivel

Cada petición se busca en los servidores de GD y se completa con:

- nombre, creador, dificultad (con su brillo de featured, epic…), estrellas o lunas, duración,
  descargas, likes, orbes, monedas y canción;
- **tier de GDDL** y **posición en AREDL** para los demons;
- la **miniatura del nivel**, de fondo en la ventana (con opacidad ajustable).

Si los servidores de GD no responden, se reintenta y se avisa de que no es culpa del espectador,
en vez de decir que el nivel no existe.

### Control de la cola

- **Siguiente, Anterior, Aleatorio, Prioridad** (subir un nivel al principio) y **Eliminar**.
- Copiar el ID del nivel o el de su canción con un clic.
- **Auto-skip:** un tiempo por nivel en segundos, minutos u horas. Al acabarse, pasa al siguiente;
  si la cola está vacía, espera y salta en cuanto llega un nivel nuevo. El mod y los widgets
  muestran la misma cuenta atrás.
- El historial guarda los últimos 50 niveles jugados para el botón **Anterior**.

### Notificaciones y voz

- Un panel con los últimos avisos: niveles añadidos, rechazos (con el motivo), eventos del
  auto-skip y del sistema (caídas de conexión, peticiones de Discord por aprobar…).
- Cada categoría se puede activar o desactivar por separado.
- **Lectura en voz alta** (voz de Google, en el idioma de la app) por categorías, con volumen
  propio.

### OBS

- **Widgets** servidos desde un servidor local (por defecto en el puerto 6969). Se añaden en OBS
  como *fuente de navegador* con la URL que da Ajustes:

  | Widget | URL |
  |---|---|
  | Notificaciones | `http://localhost:6969/widget/notification/` |
  | Nivel jugando | `http://localhost:6969/widget/playing/` |
  | Cola de niveles | `http://localhost:6969/widget/queue/` |
  | Unificado (todo en uno) | `http://localhost:6969/widget/unified/` |

  Cada widget usa el tema que elijas. Los temas son archivos `.zip` en la carpeta de temas; se
  pueden cambiar en directo sin reiniciar nada.
- **Textos en vivo** para fuentes de texto de OBS: `NivelActual.txt`, `SiguienteNivel.txt`,
  `ColaCompleta.txt` y `obs_notifications.txt`.

### Herramientas

- **Instalar el mod de Geode** con un botón: busca la carpeta de Geometry Dash en tus bibliotecas
  de Steam (en Windows y en Linux, también con el Steam de Flatpak o de Snap) y te la pide si no la
  encuentra. Descarga la última versión del mod desde
  [GitHub](https://github.com/PakeMPC/SheepGDintegration/releases); sin conexión, instala la copia
  que viene con el programa.
- **Aviso de actualización:** al abrir el programa, si el mod instalado es más antiguo que la
  última versión publicada, te ofrece actualizarlo con un clic.
- Abrir la carpeta de temas y el editor de temas.
- **Consola** integrada para ver qué está pasando.
- Ventana siempre encima, opcional.

### Idiomas

Español, inglés y portugués. Se elige al primer arranque y se puede cambiar en Ajustes; la
interfaz, los avisos y los widgets cambian sin reiniciar. El editor de temas y el mod usan el mismo
idioma.

---

## SheepGDEditor

Un editor tipo lienzo para crear y modificar los temas de los widgets:

- Arrastrar y colocar **objetos**: textos, imágenes, vídeos, el fondo del nivel, la cara de
  dificultad (con su halo), iconos de like, cajas de notificación, y los **datos del nivel**
  (nombre, creador, ID, quién lo pidió, plataforma, estrellas, descargas, likes, orbes, duración,
  GDDL, AREDL, reloj del auto-skip…).
- Pestañas para cada tipo de widget (cola, nivel jugando, notificaciones) y modo **unificado**.
- Panel de propiedades, selección múltiple, objetos vinculados, copiar y pegar propiedades, deshacer
  con Ctrl+Z, zoom.
- Fuentes del sistema y recursos propios, que viajan dentro del `.zip`.
- Editor de código para `engine.js` y `engine.css` del tema.
- **Vista previa en el navegador** con datos de ejemplo, notificaciones incluidas.
- Mismo idioma que SheepGDRequest.

---

## SheepGDintegration (mod de Geode)

La mitad del ecosistema que vive dentro del juego. Un botón con una oveja en el menú principal abre
la cola: el nivel actual arriba y los siguientes debajo.

- **Jugar, ver, saltar adelante o atrás, eliminar, elegir uno al azar** o jugar cualquier nivel de
  la lista. Todo se escribe en el mismo archivo, así que el programa y OBS se enteran al instante.
- **Auto-skip dentro de la partida:** cambia de nivel sin salir, con un fundido. Reloj en pantalla
  opcional (toda la cuenta atrás o solo los últimos 10 s).
- **Cambiar al morir:** opcionalmente espera a que mueras en vez de cortar el intento.
- **Q / E** para ir al nivel anterior / siguiente mientras juegas (como mucho un cambio por segundo).
- **Al acabarse la cola**, eliges: quedarte en el nivel, volver a la lista, seguir hasta morir o
  cargar un nivel de espera.
- **Precarga** del siguiente nivel con todo su audio (varias canciones y efectos incluidos), para
  que el salto sea inmediato y ningún nivel empiece sin sonido.
- **Sheepy**, un cubo opcional que sustituye al tuyo (o solo en los niveles pedidos), con tus colores.
- Encuentra `queue_data.json` solo; si no puede, te pide el archivo una vez y lo recuerda.
- Todos los ajustes están en el engranaje de su escena, para cambiarlos en directo sin abrir la
  lista de mods de Geode.

Más detalles en su repositorio: [PakeMPC/SheepGDintegration](https://github.com/PakeMPC/SheepGDintegration).

---

## Primeros pasos

1. **Abre SheepGDRequest** y elige el idioma. La primera vez descarga el navegador interno
   (unos 150 MB), así que tarda un poco.
2. En **Configuración → Red y comandos**, añade tus enlaces de chat, elige la plataforma de cada
   uno y pulsa **Guardar cambios**. Para Discord, pega también el token de tu bot ("Obtener token"
   abre el portal de desarrolladores).
3. Ajusta las **reglas de la cola**, el **auto-skip** y las **notificaciones** a tu gusto.
4. Para OBS: activa el **servidor de widgets**, copia la URL de cada widget y añádela como fuente
   de navegador.
5. Para jugar desde GD: **Herramientas → Instalar Mod de Geode** (necesitas tener
   [Geode](https://geode-sdk.org) instalado) y reinicia el juego. Pulsa la oveja del menú principal.

---

## Dónde se guardan los datos

| Sistema | Carpeta |
|---|---|
| Windows | `%LOCALAPPDATA%\SheepGDRequest` |
| Linux | `~/.local/share/SheepGDRequest` (o `$XDG_DATA_HOME/SheepGDRequest`) |
| macOS | `~/Library/Application Support/SheepGDRequest` |

Ahí están `settings.json`, `queue_data.json`, `notifications.json`, los textos para OBS
(`OBS_Exports/`), los temas de los widgets (`WidgetThemes/`) y las sesiones del navegador interno
(`BrowserProfiles/`). La carpeta del programa no se modifica, así que funciona también instalado en
una carpeta de solo lectura.

Si `settings.json` o `queue_data.json` se dañan, el programa guarda una copia aparte con la fecha
y avisa, en vez de sobrescribirlos.

---

## Privacidad y conexiones

No hay servidores de SheepGD ni telemetría. El programa solo se conecta a:

- las webs de chat que tú le das, y la API de TikTok o de Discord si las usas;
- los servidores de Geometry Dash, **GDDL** (gdladder.com) y **AREDL** (aredl.net) para los datos
  de los niveles, y **levelthumbs.prevter.me** para las miniaturas;
- el servicio de voz de Google Translate, si activas la lectura en voz alta;
- GitHub, para saber cuál es la última versión del mod de Geode y descargarla;
- la descarga del navegador interno de Playwright, la primera vez.

El servidor de widgets solo escucha en `localhost`.

---

## Para desarrolladores

### Estructura

```
SheepGDRequest/               este repositorio
├── SheepGDWorkspace.slnx     solución de Request y Editor
├── Shared/                   estilos compartidos (EstilosSheepGD.axaml)
├── SheepGDRequest/           el programa principal
├── SheepGDEditor/            el editor de temas
├── build/                    iconos y lanzadores para el paquete de Windows
└── screenshots/              capturas para este README
```

El mod de Geode está en su propio repositorio:
[PakeMPC/SheepGDintegration](https://github.com/PakeMPC/SheepGDintegration).

### Compilar

- **Request y Editor:** abrir `SheepGDWorkspace.slnx` en Rider o Visual Studio, o
  `dotnet build`. Los proyectos apuntan a `net8.0`, pero hace falta un **SDK de .NET 9 o
  superior** para compilar: los generadores de Avalonia 12 necesitan Roslyn 4.14.
- **Publicar:** `dotnet publish -c Release -r win-x64 --self-contained true` (o `linux-x64`).
  Sale una carpeta autocontenida; no se puede publicar como archivo único porque Playwright
  necesita su carpeta `.playwright` al lado. En Linux, Chromium necesita algunas bibliotecas del
  sistema (`libnss3`, `libgbm`…).
- **Mod:** `geode build`, con el SDK de Geode fijado en la 5.9.0. Las instrucciones para compilar
  desde Linux están en el README de su repositorio.

### El contrato: `queue_data.json`

```json
{
  "CurrentLevel": { ... },
  "History":      [ ... ],
  "Queue":        [ ... ]
}
```

Cada nivel es un `LevelRequestModel` (C#) / `LevelModel` (C++) con su `RequestId`, los datos del
nivel, quién lo pidió y `AutoSkipEndsAtUnix` (el instante del auto-skip en segundos Unix UTC; `0`
si no hay). Sin nivel actual, `CurrentLevel` lleva un marcador con el nombre `WAITING...`.

**Cambiar el formato obliga a tocar el programa y el mod a la vez.** Los dos escriben el archivo de
forma atómica (un temporal que se renombra), y el programa vigila los cambios del mod con un
`FileSystemWatcher`. La lista completa de campos está en `SheepGDRequest/Models/LevelRequestModel.cs`.

### Convenciones

- Todo texto visible o de consola pasa por las traducciones (`I18n.Tr("Clave")` en C#, `{i18n:Tr
  Clave}` en XAML), con la clave en los tres idiomas.
- Comentarios en español neutro, de 100 caracteres por línea como mucho.

---

## Créditos

Hecho por **PakeMPC** — [pakempc.com](https://pakempc.com) · [Ko-fi](https://ko-fi.com/pakempc)
