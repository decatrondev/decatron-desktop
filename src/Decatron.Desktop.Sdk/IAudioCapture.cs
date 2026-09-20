namespace Decatron.Desktop.Sdk;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Captura de micrófono ya normalizada a PCM 16 kHz mono s16le, que es lo que el
/// servidor espera para transcribir. La implementación (WASAPI hoy) vive en Core.
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    /// <summary>Frames de 20 ms (640 bytes) listos para mandar. Se invoca en un hilo de audio: no bloquear.</summary>
    event Action<ReadOnlyMemory<byte>>? FrameReady;

    /// <summary>Nivel RMS 0..1 del último frame, para el medidor.</summary>
    float Level { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

public interface IAudioCaptureFactory
{
    bool IsSupported { get; }
    string? UnsupportedReason { get; }
    IReadOnlyList<AudioDeviceInfo> ListInputDevices();
    IAudioCapture Create(string? deviceId);
}

/// <summary>
/// Reproducción de clips cortos (MP3) — la voz del coach. Un clip a la vez: si llega
/// otro mientras suena, se encola. La implementación (WASAPI/MediaFoundation en
/// Windows, reproductor del sistema en macOS/Linux) vive en Core.
/// </summary>
public interface IAudioPlayer
{
    bool IsSupported { get; }
    string? UnsupportedReason { get; }
    /// <summary>Dispositivos de salida (vacío donde no se puede elegir).</summary>
    IReadOnlyList<AudioDeviceInfo> ListOutputDevices();
    /// <summary>Reproduce y espera a que termine. deviceId null = el predeterminado.</summary>
    Task PlayAsync(byte[] mp3, string? deviceId, float volume, CancellationToken ct);
}
