using System.Diagnostics;
using System.Runtime.Versioning;
using Decatron.Desktop.Sdk;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Decatron.Desktop.Core.Audio;

/// <summary>
/// Reproduce MP3 cortos. Windows: MediaFoundation decodifica y WASAPI (shared) saca
/// el audio por el dispositivo elegido — así el streamer puede mandar la voz del
/// coach a sus auriculares y no al stream, o al revés. macOS: afplay. Linux:
/// mpg123/ffplay si están. Serializado: un clip a la vez.
/// </summary>
public sealed class AudioPlayer : IAudioPlayer
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    public string? UnsupportedReason => IsSupported ? null : "Reproducción de audio no disponible en este sistema.";

    public IReadOnlyList<AudioDeviceInfo> ListOutputDevices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<AudioDeviceInfo>();
        return ListWindows();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AudioDeviceInfo> ListWindows()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            string? defId = null;
            try { defId = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            return e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defId)).ToList();
        }
        catch { return Array.Empty<AudioDeviceInfo>(); }
    }

    public async Task PlayAsync(byte[] mp3, string? deviceId, float volume, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"decatron-coach-{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(tmp, mp3, ct);
            try
            {
                if (OperatingSystem.IsWindows()) await PlayWindowsAsync(tmp, deviceId, volume, ct);
                else if (OperatingSystem.IsMacOS()) await RunAsync("afplay", $"-v {Math.Clamp(volume, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} \"{tmp}\"", ct);
                else await PlayLinuxAsync(tmp, ct);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        finally { _one.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task PlayWindowsAsync(string path, string? deviceId, float volume, CancellationToken ct)
    {
        // MediaFoundation y WASAPI quieren su propio hilo; el clip es corto, se espera ahí.
        await Task.Run(() =>
        {
            MMDevice? device = null;
            try
            {
                if (!string.IsNullOrEmpty(deviceId))
                {
                    using var e = new MMDeviceEnumerator();
                    try { device = e.GetDevice(deviceId); } catch { device = null; }
                }
                using var reader = new MediaFoundationReader(path);
                var sampleProvider = reader.ToSampleProvider();
                var vol = new NAudio.Wave.SampleProviders.VolumeSampleProvider(sampleProvider) { Volume = Math.Clamp(volume, 0f, 1f) };
                using var output = device != null ? new WasapiOut(device, AudioClientShareMode.Shared, true, 100) : new WasapiOut(AudioClientShareMode.Shared, 100);
                output.Init(vol);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested) Thread.Sleep(50);
                output.Stop();
            }
            finally { device?.Dispose(); }
        }, ct);
    }

    private static async Task PlayLinuxAsync(string path, CancellationToken ct)
    {
        foreach (var (exe, args) in new[] { ("mpg123", $"-q \"{path}\""), ("ffplay", $"-nodisp -autoexit -loglevel quiet \"{path}\""), ("paplay", $"\"{path}\"") })
        {
            try { await RunAsync(exe, args, ct); return; }
            catch (System.ComponentModel.Win32Exception) { /* no está instalado: probar el siguiente */ }
        }
    }

    private static async Task RunAsync(string exe, string args, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!;
        await p.WaitForExitAsync(ct);
    }
}
