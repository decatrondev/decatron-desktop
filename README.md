# Decatron Desktop

App de escritorio de [Decatron](https://decatron.net): el compañero local del bot para lo que
solo puede hacerse en la PC del streamer. Se vincula una vez con el canal y desde ahí carga
módulos.

| Módulo | Estado | Qué hace |
|---|---|---|
| **Traducción en vivo** | ✅ v0.1 | Captura el micrófono y lo manda al servidor, que transcribe, traduce y sintetiza. Cada espectador elige en qué idioma escuchar el stream desde la extensión de Decatron, sin afectar a los demás. |
| **Coach de LoL** | 🟡 fase 1 | Lee el cliente de LoL (solo lectura: lobby, selección de campeón, partida, resultado) y lo manda al overlay de Game Overlays al instante. Sin IA todavía; las sugerencias, voz y comandos llegan en las fases siguientes (`LOL_COACH_PLAN.md` en el repo del bot). |
| Asistente de LoL | 🔜 | Lee el cliente de League of Legends en local (selección de campeón, partida) y muestra recomendaciones del backend, con voz opcional. |

## Cómo funciona

```
┌─────────────────────┐   wss://decatron.net/api/desktop/ws   ┌──────────────────────┐
│ Decatron Desktop    │ ────── un solo WebSocket ───────────► │ Backend Decatron     │
│  shell (Avalonia)   │   texto JSON {ch, type, …}            │  IDesktopChannel por │
│  ├─ módulo A        │   binario [canal][carga]              │  módulo              │
│  └─ módulo B        │ ◄──────────────────────────────────── │                      │
└─────────────────────┘                                       └──────────────────────┘
```

- **Vinculación:** el dashboard genera un código de 8 caracteres (vale 10 min, un solo uso); la
  app lo canjea por un token propio de bajo privilegio que solo abre este WebSocket. En Windows
  el token se guarda cifrado con DPAPI.
- **Conexión:** reconecta sola con backoff (1 s → 30 s), mide latencia con ping y reparte los
  mensajes a cada módulo por su canal.
- **Audio (traducción):** WASAPI en modo compartido (no le quita el mic a OBS), normalizado a
  PCM 16 kHz mono en frames de 20 ms, con puerta de voz para no mandar silencio.

## Estructura

```
src/
  Decatron.Desktop.Sdk/                  contratos: IModule, IDesktopConnection, IAudioCapture
  Decatron.Desktop.Core/                 conexión WS, vinculación, ajustes/secretos, captura de audio
  Decatron.Desktop.Modules.Translation/  primer módulo
  Decatron.Desktop.Modules.LolCoach/     coach de LoL: LcuLocator (lockfile) + LcuClient (GET al LCU) + LolClientWatcher (fases)
  Decatron.Desktop/                      shell Avalonia: chrome propio, barra de módulos, Velopack
tests/
  Decatron.Desktop.Tests/                servidor falso + tests de conexión, canal, audio y vinculación
```

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

El servidor vive en el repo del bot (`decatrondev/decatron`): `DesktopWsMiddleware`,
`DesktopController` y un `IDesktopChannel` por módulo. El plan completo de la traducción en
vivo está en el panel de admin del dashboard (`dev-docs/plans/REALTIME_TRANSLATION_PLAN.md`).
