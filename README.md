# Decatron Desktop

[Español](README.es.md)

Desktop companion app for [Decatron](https://decatron.net): the local side of the bot, for everything
that can only be done on the streamer's PC. You link it once to your channel and it loads modules from
there.

| Module | Status | What it does |
|---|---|---|
| **Live translation** | ✅ | Captures the microphone and sends it to the server, which transcribes, translates and synthesizes it. Each viewer picks the language to hear the stream in from the Decatron browser extension, without affecting anyone else. |
| **LoL coach** | ✅ | Reads the League of Legends client (read-only: lobby, champion select, game, result), sends it to the overlay instantly and shows or reads aloud what the AI coach says. |
| **Downloads** | ✅ | Downloads video or audio from YouTube, Spotify (the same song is searched on YouTube) and hundreds of other sites, requested from the dashboard (Song Request → Downloads). It runs yt-dlp on this PC, with the streamer's IP, because YouTube blocks servers. yt-dlp and ffmpeg are downloaded the first time, verified with SHA-256, and yt-dlp is updated every day. Qualities, MP4/WebM/MP3/M4A/Opus/WAV, trimming, thumbnail and subtitles. |
| **Playlist import** | ✅ | Part of the Downloads module. To import a playlist into Song Request, the server sends the songs of a Spotify, Deezer or Apple Music playlist and the app searches each one on YouTube with yt-dlp (YouTube Music checking the duration, no covers or live versions, preferring the official audio); YouTube playlists are read by the app itself (no private, deleted or live videos). All YouTube queries use the streamer's IP, so the server never queries YouTube. |

User guides (what each module does and how to use it): <https://decatron.net/docs/translation> and
the Song Request section of the manual at <https://decatron.net/docs>.

## How it works

```
┌─────────────────────┐   wss://decatron.net/api/desktop/ws   ┌──────────────────────┐
│ Decatron Desktop    │ ────── a single WebSocket ──────────► │ Decatron backend     │
│  shell (Avalonia)   │   JSON text {ch, type, …}             │  one IDesktopChannel │
│  ├─ module A        │   binary [channel][payload]           │  per module          │
│  └─ module B        │ ◄──────────────────────────────────── │                      │
└─────────────────────┘                                       └──────────────────────┘
```

- **Linking:** the dashboard (Settings → Integrations) generates an 8-character code (valid for
  10 minutes, single use); the app exchanges it for its own low-privilege token that can only open this
  WebSocket. On Windows the token is stored encrypted with DPAPI. Linked PCs are listed in the dashboard
  and can be unlinked from there.
- **Connection:** it reconnects on its own with backoff (1 s → 30 s), measures latency with a ping and
  routes each message to its module by channel. If the connection drops during a translation session, the
  session is cut and has to be started again.
- **Audio (translation):** WASAPI in shared mode (it does not take the microphone away from OBS),
  normalized to 16 kHz mono PCM in 20 ms frames. Audio is sent continuously while the session is active so
  the server can close each phrase properly; the voice gate only drives the level meter on screen.

## Structure

```
src/
  Decatron.Desktop.Sdk/                  contracts: IModule, IDesktopConnection, IAudioCapture
  Decatron.Desktop.Core/                 WS connection, linking, settings/secrets, audio capture
  Decatron.Desktop.Modules.Translation/  live translation
  Decatron.Desktop.Modules.LolCoach/     LoL coach: LcuLocator (lockfile) + LcuClient (GET to the LCU) + LolClientWatcher (phases)
  Decatron.Desktop.Modules.Downloads/    downloads and playlist import: ToolManager (verified yt-dlp/ffmpeg) + DownloadRunner + DownloadsClient + SongImportClient
  Decatron.Desktop/                      Avalonia shell: custom chrome, module bar, Velopack
  Decatron.Desktop.Installer/            Windows bootstrap installer (see Publishing)
tests/
  Decatron.Desktop.Tests/                fake server + connection, channel, audio and linking tests
```

The module rules (a module never references another module, one WebSocket, no native dialogs) are in
[CONVENTIONS.md](CONVENTIONS.md).

## Development

```bash
dotnet build
dotnet test
dotnet run --project src/Decatron.Desktop      # points to https://decatron.net
DECATRON_API=https://staging.decatron.net dotnet run --project src/Decatron.Desktop
```

Requires .NET 10. Microphone capture is implemented for Windows; on macOS and Linux the app builds and
links, but the translation module warns that the microphone is not supported yet.

## Publishing

A `vX.Y.Z` tag triggers `release.yml`: it publishes self-contained per platform, packages with Velopack
per channel (`win`, `linux`, `osx`) and uploads everything to a GitHub Release. The installed app checks
that release at startup and every 6 h, downloads in the background and applies on close.

On Windows the published `DecatronDesktop-Setup.exe` is `src/Decatron.Desktop.Installer`: a bootstrap
with our own window that embeds the real Velopack Setup and runs it silently, to avoid showing the
generic installer. It lives outside the `.slnx` because it only makes sense in the pipeline (it needs the
`SetupInner.exe` produced by `vpk pack`).

## Backend

The server lives in the bot repository ([`decatrondev/decatron`](https://github.com/decatrondev/decatron)):
`DesktopWsMiddleware`, `DesktopController` and one `IDesktopChannel` per module. The architecture notes
are in `docs/ARCHITECTURE.md` there (sections *Live translation pipeline* and *Real-time
Communication*).
