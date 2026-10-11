# Decatron Desktop

[English](README.md)

App de escritorio de [Decatron](https://decatron.net): el compañero local del bot para lo que
solo puede hacerse en la PC del streamer. Se vincula una vez con el canal y desde ahí carga
módulos.

| Módulo | Estado | Qué hace |
|---|---|---|
| **Traducción en vivo** | ✅ | Captura el micrófono y lo manda al servidor, que transcribe, traduce y sintetiza. Cada espectador elige en qué idioma escuchar el stream desde la extensión de Decatron, sin afectar a los demás. |
| **Coach de LoL** | ✅ | Lee el cliente de LoL (solo lectura: lobby, selección de campeón, partida, resultado), lo manda al overlay al instante y muestra o lee en voz alta lo que dice el coach con IA. |
| **Descargas** | ✅ | Descarga video o audio de YouTube, Spotify (se busca la misma canción en YouTube) y cientos de sitios, pedido desde el dashboard (Song Request → Descargas). Corre yt-dlp en esta PC, con la IP del streamer, porque YouTube bloquea a los servidores. yt-dlp y ffmpeg se bajan la primera vez, se verifican con su SHA-256 y yt-dlp se actualiza cada día. Calidades, MP4/WebM/MP3/M4A/Opus/WAV, recorte, miniatura y subtítulos. |
| **Importar playlists** | ✅ | Parte del módulo de Descargas. Para importar una playlist a Song Request, el servidor manda las canciones de una playlist de Spotify, Deezer o Apple Music y la app busca cada una en YouTube con yt-dlp (YouTube Music verificando la duración, sin covers ni versiones en vivo, prefiriendo el audio oficial); las playlists de YouTube las lee la propia app (sin privados, borrados ni en vivo). Toda consulta a YouTube usa la IP del streamer, así el servidor nunca le consulta a YouTube. |

Guías de uso (qué hace cada módulo y cómo usarlo): <https://decatron.net/docs/translation> y la
sección de Song Request del manual en <https://decatron.net/docs>.

## Cómo funciona

```
┌─────────────────────┐   wss://decatron.net/api/desktop/ws   ┌──────────────────────┐
│ Decatron Desktop    │ ────── un solo WebSocket ───────────► │ Backend Decatron     │
│  shell (Avalonia)   │   texto JSON {ch, type, …}            │  IDesktopChannel por │
│  ├─ módulo A        │   binario [canal][carga]              │  módulo              │
│  └─ módulo B        │ ◄──────────────────────────────────── │                      │
└─────────────────────┘                                       └──────────────────────┘
```

- **Vinculación:** el dashboard (Ajustes → Integraciones) genera un código de 8 caracteres (vale
  10 min, un solo uso); la app lo canjea por un token propio de bajo privilegio que solo abre este
  WebSocket. En Windows el token se guarda cifrado con DPAPI. Las PCs vinculadas aparecen en el
  dashboard y se pueden desvincular desde ahí.
- **Conexión:** reconecta sola con backoff (1 s → 30 s), mide latencia con ping y reparte los
  mensajes a cada módulo por su canal. Si la conexión se pierde durante una sesión de traducción, la
  sesión se corta y hay que iniciarla de nuevo.
- **Audio (traducción):** WASAPI en modo compartido (no le quita el mic a OBS), normalizado a
  PCM 16 kHz mono en frames de 20 ms. El audio se envía de forma continua mientras la sesión está
  activa para que el servidor cierre bien cada frase; la puerta de voz solo mueve el medidor de nivel
  de la pantalla.

## Estructura

```
src/
  Decatron.Desktop.Sdk/                  contratos: IModule, IDesktopConnection, IAudioCapture
  Decatron.Desktop.Core/                 conexión WS, vinculación, ajustes/secretos, captura de audio
  Decatron.Desktop.Modules.Translation/  traducción en vivo
  Decatron.Desktop.Modules.LolCoach/     coach de LoL: LcuLocator (lockfile) + LcuClient (GET al LCU) + LolClientWatcher (fases)
  Decatron.Desktop.Modules.Downloads/    descargas e importación de playlists: ToolManager (yt-dlp/ffmpeg verificados) + DownloadRunner + DownloadsClient + SongImportClient
  Decatron.Desktop/                      shell Avalonia: chrome propio, barra de módulos, Velopack
  Decatron.Desktop.Installer/            instalador bootstrap de Windows (ver Publicar)
tests/
  Decatron.Desktop.Tests/                servidor falso + tests de conexión, canal, audio y vinculación
```

Las reglas de los módulos (un módulo nunca referencia a otro, un solo WebSocket, sin diálogos
nativos) están en [CONVENTIONS.md](CONVENTIONS.md).

## Desarrollo

```bash
dotnet build
dotnet test
dotnet run --project src/Decatron.Desktop      # apunta a https://decatron.net
DECATRON_API=https://staging.decatron.net dotnet run --project src/Decatron.Desktop
```

Requiere .NET 10. La captura de micrófono está implementada para Windows; en macOS/Linux la app
compila y se vincula, pero el módulo de traducción avisa que el mic aún no está soportado.

## Publicar

Un tag `vX.Y.Z` dispara `release.yml`: publica self-contained por plataforma, empaqueta con
Velopack por canal (`win`, `linux`, `osx`) y sube todo a un GitHub Release. La app instalada
revisa ese release al arrancar y cada 6 h, descarga en segundo plano y aplica al cerrar.

En Windows el `DecatronDesktop-Setup.exe` publicado es `src/Decatron.Desktop.Installer`: un
bootstrap con nuestra ventana que embebe el Setup real de Velopack y lo corre en silencio, para
no mostrar el instalador genérico. Está fuera del `.slnx` porque solo tiene sentido en el
pipeline (necesita el `SetupInner.exe` que genera `vpk pack`).

## Backend

El servidor vive en el repo del bot ([`decatrondev/decatron`](https://github.com/decatrondev/decatron)):
`DesktopWsMiddleware`, `DesktopController` y un `IDesktopChannel` por módulo. Las notas de
arquitectura están en `docs/ARCHITECTURE.md` de ese repo (secciones *Live translation pipeline* y
*Real-time Communication*; la versión en español está en `docs/es/`).
