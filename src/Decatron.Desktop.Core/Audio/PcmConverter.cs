namespace Decatron.Desktop.Core.Audio;

/// <summary>
/// Pasa lo que entrega el dispositivo (float o int16, cualquier tasa, 1–2 canales) a
/// PCM 16 kHz mono s16le en frames de 20 ms. Remuestreo lineal: para voz hacia STT
/// sobra, y evita depender de MediaFoundation (solo Windows).
/// </summary>
public sealed class PcmConverter
{
    public const int TargetRate = 16000;
    public const int FrameBytes = 640; // 20 ms × 16 kHz × 2 bytes

    private readonly int _srcRate;
    private readonly int _srcChannels;
    private readonly bool _srcIsFloat;
    private readonly int _srcBits;
    private readonly double _step;
    private double _pos;
    private float _lastSample;
    private readonly List<float> _mono = new(8192);
    private readonly List<byte> _out = new(FrameBytes * 4);

    public PcmConverter(int srcRate, int srcChannels, bool srcIsFloat, int srcBits)
    {
        _srcRate = srcRate; _srcChannels = srcChannels; _srcIsFloat = srcIsFloat; _srcBits = srcBits;
        _step = (double)srcRate / TargetRate;
    }

    /// <summary>Nivel RMS (0..1) de lo procesado en la última llamada.</summary>
    public float LastLevel { get; private set; }

    /// <summary>Convierte un bloque crudo y devuelve los frames completos de 640 bytes que salieron.</summary>
    public IEnumerable<ReadOnlyMemory<byte>> Push(ReadOnlySpan<byte> raw)
    {
        _mono.Clear();
        double sumSq = 0;
        int bytesPerSample = _srcIsFloat ? 4 : _srcBits / 8;
        int frameSize = bytesPerSample * _srcChannels;
        int count = raw.Length / frameSize;
        for (int i = 0; i < count; i++)
        {
            float acc = 0;
            for (int c = 0; c < _srcChannels; c++)
            {
                int off = i * frameSize + c * bytesPerSample;
                float s = _srcIsFloat
                    ? BitConverter.ToSingle(raw.Slice(off, 4))
                    : _srcBits == 16 ? BitConverter.ToInt16(raw.Slice(off, 2)) / 32768f
                    : _srcBits == 32 ? BitConverter.ToInt32(raw.Slice(off, 4)) / 2147483648f
                    : (raw[off + 2] << 24 | raw[off + 1] << 16 | raw[off] << 8) / 2147483648f; // 24-bit
                acc += s;
            }
            var m = acc / _srcChannels;
            _mono.Add(m);
            sumSq += m * m;
        }
        LastLevel = count > 0 ? (float)Math.Sqrt(sumSq / count) : 0;

        // Remuestreo lineal a 16 kHz
        var outBytes = new List<ReadOnlyMemory<byte>>();
        for (; _pos < _mono.Count; _pos += _step)
        {
            int i0 = (int)_pos;
            float a = i0 == 0 ? _lastSample : _mono[i0 - 1];
            float b = _mono[i0];
            float frac = (float)(_pos - i0);
            // interpolación entre la muestra anterior y la actual
            float s = a + (b - a) * frac;
            short v = (short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue);
            _out.Add((byte)(v & 0xFF));
            _out.Add((byte)(v >> 8));
            if (_out.Count >= FrameBytes)
            {
                outBytes.Add(_out.GetRange(0, FrameBytes).ToArray());
                _out.RemoveRange(0, FrameBytes);
            }
        }
        if (_mono.Count > 0) { _lastSample = _mono[^1]; _pos -= _mono.Count; }
        return outBytes;
    }
}
