namespace Decatron.Desktop.Core.Audio;

/// <summary>
/// Puerta de voz simple por energía: deja pasar frames mientras haya nivel por encima
/// del umbral y durante un colchón después (para no cortar el final de las palabras).
/// No es un VAD de verdad — el que decide frases es Deepgram — solo evita mandar
/// silencio al servidor (ancho de banda y minutos de STT).
/// </summary>
public sealed class VoiceGate
{
    private readonly float _threshold;
    private readonly int _hangFrames;
    private int _hang;

    /// <param name="threshold">RMS 0..1; 0.01 ≈ -40 dBFS, razonable para un mic de stream.</param>
    /// <param name="hangMs">Cuánto seguir mandando tras el último frame con voz.</param>
    public VoiceGate(float threshold = 0.01f, int hangMs = 700)
    {
        _threshold = threshold; _hangFrames = Math.Max(1, hangMs / 20);
    }

    public bool IsOpen => _hang > 0;

    public bool Pass(float level)
    {
        if (level >= _threshold) { _hang = _hangFrames; return true; }
        if (_hang > 0) { _hang--; return true; }
        return false;
    }
}
