# SheepGDRequest

<img src="screenshots/logo-request.png" width="150" alt="Logo de SheepGDRequest: una oveja blanca de dibujo animado" />

**Las peticiones de niveles de tu directo de Geometry Dash, en un solo sitio.** SheepGDRequest lee
el chat de Twitch, YouTube, Kick, TikTok y Discord a la vez, filtra lo que piden tus espectadores,
busca cada nivel en GD y lo pone en una cola que puedes manejar desde la ventana, desde OBS o desde
dentro del propio juego.

Todo funciona en tu ordenador. No hay servidores, cuentas ni suscripciones: el programa, los
widgets de OBS y el mod de Geode comparten un único archivo, `queue_data.json`, y cualquiera de los
tres puede mover la cola. Lo que hagas en uno aparece al instante en los otros dos.

<p align="center">
  <img src="screenshots/request/mainlevellist.png" width="800" alt="Ventana principal de SheepGDRequest con un Extreme Demon en juego, la cola de niveles y el panel de notificaciones" />
</p>

> **Juega sin salir de GD.** Con el mod [SheepGDintegration](https://github.com/PakeMPC/SheepGDintegration)
> la cola aparece dentro de Geometry Dash: saltas, borras o eliges niveles desde el juego, y el
> auto-skip cambia de nivel sin que salgas de la partida. El programa lo instala con un botón y te
> avisa cuando hay una versión nueva.

## Qué hace

**Todos tus chats a la vez**

- **SheepChat, SocialStream.Ninja, StreamElements, TikTok Live, Discord** y **cualquier otra web
  de chat**: hasta diez enlaces al mismo tiempo, de cualquier mezcla.
- Si una fuente se cae (la web se recarga, se va la red), **se reconecta sola** y te avisa de la
  caída y de la vuelta.
- **Discord con moderación:** si quieres, un nivel solo entra cuando un moderador reacciona al
  mensaje con un visto bueno (✅, 👍, ❤️…). Si borran el mensaje, el nivel sale de la cola.

**Tú pones las reglas**

- Tus propios comandos (`!add`, `!request`…) y detección automática de IDs sueltos en el chat.
- **Filtro de dificultad:** solo Demons, nada por encima de Insane, todo menos Auto… lo que quieras.
- Solo niveles con rate, sin repetidos, solo por ID, lista negra, límite por espectador y límite
  total de la cola.
- Antispam integrado y un botón para **pausar las peticiones** sin desconectar nada.

**Cada nivel, con toda su información**

- Nombre, creador, dificultad, estrellas, duración, descargas, likes, orbes y canción, directamente
  de los servidores de GD.
- **Tier de GDDL** y **posición en AREDL** para los demons, y la miniatura del nivel de fondo.

**La cola, a un clic**

- Siguiente, anterior, aleatorio, subir de prioridad y eliminar. Copia el ID del nivel o de su
  canción para pegarlo en GD.
- **Auto-skip:** dale a cada nivel un tiempo; al acabarse, pasa al siguiente. El mod y los widgets
  muestran la misma cuenta atrás.

**Hecho para el directo**

- **Widgets para OBS** servidos desde tu propio PC: nivel en juego, cola, notificaciones o todo en
  uno. Se añaden como fuente de navegador y cambian de tema en directo.
- **Textos en vivo** (`.txt`) para fuentes de texto de OBS.
- **Panel de notificaciones** y **lectura en voz alta** de los avisos que elijas.
- Español, inglés y portugués, y cambia de idioma sin reiniciar.

<table>
  <tr>
    <td><img src="screenshots/request/mainlevellistcollapsed.png" alt="Ventana principal con el menú lateral recogido" /></td>
    <td><img src="screenshots/request/settings.png" alt="Ajustes: enlaces de chat, comandos y reglas de la cola" /></td>
  </tr>
  <tr>
    <td align="center"><sub>Menú recogido, para dejar sitio a la cola</sub></td>
    <td align="center"><sub>Enlaces de chat, comandos y reglas de la cola</sub></td>
  </tr>
</table>

## SheepGDEditor: diseña tus propios widgets

<img src="screenshots/logo-editor.png" width="90" alt="Logo de SheepGDEditor: la oveja con un pincel" />

Los widgets de OBS usan temas, y con **SheepGDEditor** creas los tuyos sin escribir una línea de
HTML: arrastra objetos al lienzo y ajusta cada detalle.

- Textos, imágenes, vídeos y **todos los datos del nivel**: nombre, creador, cara de dificultad con
  su halo, estadísticas, GDDL, AREDL, quién lo pidió, desde qué plataforma, el reloj del auto-skip…
- Un lienzo para cada widget (nivel en juego, cola y notificaciones) y un modo **unificado** a
  pantalla completa.
- Tus fuentes y recursos viajan dentro del tema, así que se ve igual en cualquier PC.
- **Vista previa en el navegador** con datos de ejemplo, y un editor de código para quien quiera ir
  más allá.

<table>
  <tr>
    <td><img src="screenshots/editor/playingedit.png" alt="Editor: personalizando el nombre del nivel en el widget de nivel en juego" /></td>
    <td><img src="screenshots/editor/queueedit.png" alt="Editor: widget de cola en horizontal con tarjetas de ejemplo" /></td>
  </tr>
  <tr>
    <td align="center"><sub>Widget de nivel en juego, con propiedades de texto</sub></td>
    <td align="center"><sub>Widget de cola, con tarjetas de ejemplo</sub></td>
  </tr>
  <tr>
    <td><img src="screenshots/editor/unifiededit.png" alt="Editor: tema unificado a 1920×1080 con notificación, nivel en juego y cola" /></td>
    <td><img src="screenshots/editor/codeeditor.png" alt="Editor de código con engine.js del tema" /></td>
  </tr>
  <tr>
    <td align="center"><sub>Modo unificado, a 1920×1080</sub></td>
    <td align="center"><sub>Editor de código del tema</sub></td>
  </tr>
</table>

## Requisitos

| | |
|---|---|
| Sistema | Windows 10/11 o Linux, de 64 bits |
| .NET | No hace falta instalarlo: viaja con el programa |
| Conexión | La primera vez descarga un navegador interno (unos 150 MB) para leer los chats web |
| Opcional | OBS para los widgets; Geometry Dash 2.2081 con [Geode](https://geode-sdk.org) 5.9.0 para el mod |

En Linux, el navegador interno necesita algunas bibliotecas del sistema (`libnss3`, `libgbm`…), que
casi todas las distribuciones de escritorio ya traen.

## Empezar

1. **Abre SheepGDRequest** y elige tu idioma.
2. En **Configuración**, pega los enlaces de tus chats, elige la plataforma de cada uno y pulsa
   **Guardar cambios**. Para Discord, pega también el token de tu bot ("Obtener token" te lleva al
   portal de desarrolladores).
3. Ajusta las **reglas de la cola**, el **auto-skip** y las **notificaciones**.
4. **OBS:** activa el servidor de widgets, copia la URL de cada widget y añádela como fuente de
   navegador (por ejemplo, `http://localhost:6969/widget/playing/`).
5. **Geometry Dash:** pulsa **Instalar Mod de Geode**. El programa encuentra tu carpeta de GD,
   descarga la última versión del mod y la instala. Reinicia el juego y pulsa la oveja del menú
   principal.

Cuando salga una versión nueva del mod, el programa te lo dirá al abrirse y la instalará con un clic.

## Tus datos

Todo se guarda en una carpeta tuya, nunca en la del programa:

| Sistema | Carpeta |
|---|---|
| Windows | `%LOCALAPPDATA%\SheepGDRequest` |
| Linux | `~/.local/share/SheepGDRequest` |

Ahí están tus ajustes, la cola, las notificaciones, los textos para OBS, los temas de los widgets y
las sesiones de los chats web. Si un archivo se daña, el programa guarda una copia aparte y te
avisa, en vez de sobrescribirlo.

## A qué se conecta

No hay servidores de SheepGD ni telemetría. El programa solo se conecta a:

- las webs de chat que le des, y a la API de TikTok o de Discord si las usas;
- los servidores de **Geometry Dash**, **GDDL** (gdladder.com) y **AREDL** (aredl.net) para los
  datos de los niveles, y **levelthumbs.prevter.me** para las miniaturas;
- **GitHub**, para saber cuál es la última versión del mod y descargarla;
- el servicio de voz de **Google Translate**, solo si activas la lectura en voz alta;
- la descarga del navegador interno de Playwright, la primera vez.

El servidor de widgets solo escucha en tu propio equipo (`localhost`).

<details>
<summary><b>Para desarrolladores</b></summary>

### Estructura

```
SheepGDRequest/               este repositorio
├── SheepGDWorkspace.slnx     solución de Request y Editor
├── Shared/                   estilos compartidos (EstilosSheepGD.axaml)
├── SheepGDRequest/           el programa principal (C# · .NET 8 · Avalonia 12)
├── SheepGDEditor/            el editor de temas
├── build/                    iconos y lanzadores para el paquete de Windows
└── screenshots/              capturas y logos de este README
```

El mod de Geode (C++23 · Geode 5.9.0) está en su propio repositorio:
[PakeMPC/SheepGDintegration](https://github.com/PakeMPC/SheepGDintegration).

### Compilar

- Abre `SheepGDWorkspace.slnx` en Rider o Visual Studio, o usa `dotnet build`. Los proyectos
  apuntan a `net8.0`, pero hace falta un **SDK de .NET 9 o superior**: los generadores de
  Avalonia 12 necesitan Roslyn 4.14.
- Para publicar: `dotnet publish -c Release -r win-x64 --self-contained true` (o `linux-x64`). Sale
  una carpeta autocontenida. No se puede publicar como archivo único, porque Playwright necesita su
  carpeta `.playwright` al lado.

### El archivo compartido: `queue_data.json`

```json
{
  "CurrentLevel": { ... },
  "History":      [ ... ],
  "Queue":        [ ... ]
}
```

Cada nivel lleva su `RequestId`, los datos del nivel, quién lo pidió y `AutoSkipEndsAtUnix` (el
instante del auto-skip en segundos Unix UTC; `0` si no hay). Sin nivel actual, `CurrentLevel` lleva
un marcador llamado `WAITING...`. La lista completa de campos está en
`SheepGDRequest/Models/LevelRequestModel.cs`.

**Cambiar el formato obliga a tocar el programa y el mod a la vez.** Los dos escriben el archivo de
forma atómica (un temporal que se renombra), y el programa vigila los cambios del mod con un
`FileSystemWatcher`.

### Convenciones

- Todo texto visible o de consola pasa por las traducciones (`I18n.Tr("Clave")` en C#,
  `{i18n:Tr Clave}` en XAML), con la clave en los tres idiomas.
- Comentarios en español neutro, de 100 caracteres por línea como mucho.

</details>

## Créditos

Hecho por **PakeMPC** — [pakempc.com](https://pakempc.com).

Si SheepGD te ayuda en tus directos, puedes apoyarlo en [Ko-fi](https://ko-fi.com/pakempc).
