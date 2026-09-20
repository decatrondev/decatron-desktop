using System.Text.Json.Nodes;
using Decatron.Desktop.Sdk;

namespace Decatron.Desktop.Modules.Translation;

public sealed record TranslationStatus(
    bool Active, IReadOnlyList<string> Languages, IReadOnlyDictionary<string, int> Listeners,
    IReadOnlyList<string> ActivePipelines, double SpeechSeconds, int Segments, long CreditsUsed, string? LastError);

/// <summary>
/// Protocolo del canal <c>translation</c> sobre la conexión única. Solo traduce
/// mensajes a eventos tipados; no sabe de micrófonos ni de UI.
/// </summary>
public sealed class TranslationClient : IDisposable
{
    public const string Channel = "translation";
    public const byte AudioBinaryChannel = 0x01;

    private readonly IDesktopConnection _conn;
    private readonly IDisposable _sub;

    public event Action<TranslationStatus>? StatusChanged;
    public event Action<string>? Error;
    public event Action<string, string?>? Stopped;   // reason, error
    public event Action? Started;

    public bool IsAvailable => _conn.Modules.TryGetValue(Channel, out var m) && m?["available"]?.GetValue<bool>() == true;
    public bool IsEnabled => _conn.Modules.TryGetValue(Channel, out var m) && m?["enabled"]?.GetValue<bool>() == true;
    /// <summary>Saldo del canal según el último hello/module/status. null = aún no se sabe.</summary>
    public CreditBalance? Balance { get; private set; }
    public event Action<CreditBalance>? BalanceChanged;

    public IReadOnlyList<string> ConfiguredLanguages =>
        _conn.Modules.TryGetValue(Channel, out var m) && m?["languages"] is JsonArray a
            ? a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList()
            : Array.Empty<string>();

    public TranslationClient(IDesktopConnection conn)
    {
        _conn = conn;
        _sub = conn.Subscribe(Channel, OnMessage);
        conn.HelloReceived += RefreshBalanceFromModule;
        conn.ModuleUpdated += n => { if (n == Channel) RefreshBalanceFromModule(); };
    }

    private void RefreshBalanceFromModule()
    {
        if (_conn.Modules.TryGetValue(Channel, out var m)) ApplyBalance(m?["credits"]);
    }

    private void ApplyBalance(JsonNode? c)
    {
        if (c is not JsonObject o) return;
        Balance = new CreditBalance(o["available"]?.GetValue<long>() ?? 0, o["unlimited"]?.GetValue<bool>() ?? false);
        BalanceChanged?.Invoke(Balance);
    }

    public Task StartAsync(CancellationToken ct) => _conn.SendAsync(Channel, "start", null, ct);
    public Task StopAsync(CancellationToken ct) => _conn.SendAsync(Channel, "stop", null, ct);
    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) => _conn.SendBinaryAsync(AudioBinaryChannel, pcm16, ct);

    private void OnMessage(string type, JsonNode msg)
    {
        switch (type)
        {
            case "started":
                ApplyBalance(msg["credits"]);
                Started?.Invoke();
                if (Parse(msg["session"]) is { } s0) StatusChanged?.Invoke(s0);
                break;
            case "status":
                ApplyBalance(msg["credits"]);
                if (Parse(msg["session"]) is { } s1) StatusChanged?.Invoke(s1);
                break;
            case "stopped":
                Stopped?.Invoke(msg["reason"]?.GetValue<string>() ?? "unknown", msg["error"]?.GetValue<string>());
                break;
            case "error":
                Error?.Invoke(msg["message"]?.GetValue<string>() ?? "Error");
                break;
        }
    }

    private static TranslationStatus? Parse(JsonNode? s)
    {
        if (s is not JsonObject o) return null;
        var listeners = new Dictionary<string, int>();
        if (o["listeners"] is JsonObject lo)
            foreach (var (k, v) in lo) listeners[k] = v?.GetValue<int>() ?? 0;
        return new TranslationStatus(
            o["active"]?.GetValue<bool>() ?? false,
            (o["languages"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? new(),
            listeners,
            (o["activePipelines"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? new(),
            o["speechSeconds"]?.GetValue<double>() ?? 0,
            o["segments"]?.GetValue<int>() ?? 0,
            o["creditsUsed"]?.GetValue<long>() ?? 0,
            o["lastError"]?.GetValue<string>());
    }

    public void Dispose() => _sub.Dispose();
}

public sealed record CreditBalance(long Available, bool Unlimited);
