using System.Runtime.Versioning;
using Decatron.Desktop.Sdk;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Decatron.Desktop.Core.Audio;

/// <summary>Captura WASAPI en modo compartido (no le quita el micrófono a OBS) y normaliza a 16 kHz mono.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture : IAudioCapture
{
    private readonly string? _deviceId;
    private WasapiCapture? _capture;
    private PcmConverter? _conv;

    public event Action<ReadOnlyMemory<byte>>? FrameReady;
    public float Level { get; private set; }

    public WasapiAudioCapture(string? deviceId) { _deviceId = deviceId; }

    public Task StartAsync(CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = _deviceId != null
            ? enumerator.GetDevice(_deviceId)
            : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
        var f = _capture.WaveFormat;
        _conv = new PcmConverter(f.SampleRate, f.Channels, f.Encoding == WaveFormatEncoding.IeeeFloat, f.BitsPerSample);
        _capture.DataAvailable += (_, e) =>
        {
            var conv = _conv;
            if (conv == null) return;
            foreach (var frame in conv.Push(e.Buffer.AsSpan(0, e.BytesRecorded)))
                FrameReady?.Invoke(frame);
            Level = conv.LastLevel;
        };
        _capture.StartRecording();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose(); _capture = null; _conv = null; Level = 0;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class AudioCaptureFactory : IAudioCaptureFactory
{
    public bool IsSupported => OperatingSystem.IsWindows();
    public string? UnsupportedReason => IsSupported ? null : "La captura de micrófono todavía solo está disponible en Windows.";

    public IReadOnlyList<AudioDeviceInfo> ListInputDevices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<AudioDeviceInfo>();
        return ListWindows();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AudioDeviceInfo> ListWindows()
    {
        using var e = new MMDeviceEnumerator();
        string? defId = null;
        try { defId = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { }
        return e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defId))
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    public IAudioCapture Create(string? deviceId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(UnsupportedReason);
        return new WasapiAudioCapture(deviceId);
    }
}
