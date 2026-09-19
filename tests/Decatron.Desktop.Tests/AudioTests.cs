using Decatron.Desktop.Core.Audio;
using Xunit;

namespace Decatron.Desktop.Tests;

public class AudioTests
{
    [Fact]
    public void Convierte_48k_estereo_float_a_16k_mono_en_frames_de_20ms()
    {
        var conv = new PcmConverter(48000, 2, srcIsFloat: true, srcBits: 32);
        // 100 ms de seno de 440 Hz a 48 kHz estéreo
        int n = 4800;
        var raw = new byte[n * 2 * 4];
        for (int i = 0; i < n; i++)
        {
            float s = MathF.Sin(2 * MathF.PI * 440 * i / 48000f) * 0.5f;
            BitConverter.TryWriteBytes(raw.AsSpan(i * 8, 4), s);
            BitConverter.TryWriteBytes(raw.AsSpan(i * 8 + 4, 4), s);
        }
        var frames = conv.Push(raw).ToList();
        Assert.Equal(5, frames.Count);                 // 100 ms → 5 frames de 20 ms
        Assert.All(frames, f => Assert.Equal(640, f.Length));
        Assert.InRange(conv.LastLevel, 0.33f, 0.37f);  // RMS de un seno de amplitud 0.5 ≈ 0.354

        // La salida tiene que seguir siendo un seno de 440 Hz: 16000/440 ≈ 36.4 muestras por ciclo
        var pcm = frames.SelectMany(f => f.ToArray()).ToArray();
        int crossings = 0; short prev = 0;
        for (int i = 0; i < pcm.Length; i += 2)
        {
            short v = BitConverter.ToInt16(pcm, i);
            if (prev < 0 && v >= 0) crossings++;
            prev = v;
        }
        Assert.InRange(crossings, 42, 46); // 44 ciclos en 100 ms
    }

    [Fact]
    public void Acumula_restos_entre_llamadas()
    {
        var conv = new PcmConverter(16000, 1, srcIsFloat: false, srcBits: 16);
        var chunk = new byte[300]; // 150 muestras, menos de un frame
        Assert.Empty(conv.Push(chunk));
        Assert.Empty(conv.Push(chunk));
        Assert.Single(conv.Push(chunk)); // 450 muestras ≥ 320 → un frame
    }

    [Fact]
    public void Puerta_de_voz_mantiene_abierto_un_colchon()
    {
        var gate = new VoiceGate(threshold: 0.05f, hangMs: 100); // 5 frames
        Assert.False(gate.Pass(0.01f));
        Assert.True(gate.Pass(0.2f));
        for (int i = 0; i < 5; i++) Assert.True(gate.Pass(0.0f));
        Assert.False(gate.Pass(0.0f));
    }
}
