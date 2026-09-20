using System.Text.Json.Nodes;
using Decatron.Desktop.Sdk;

namespace Decatron.Desktop.Modules.LolCoach;

public sealed record LinkedLolAccount(string Puuid, string Name, string? Region);

/// <summary>
/// Protocolo del canal <c>lol-coach</c> sobre la conexión única. Solo manda lo que el
/// watcher le da y traduce lo que el servidor contesta; no sabe del LCU ni de la UI.
/// </summary>
public sealed class LolCoachClient : IDisposable
{
    public const string Channel = "lol-coach";

    private readonly IDesktopConnection _conn;
    private readonly IDisposable _sub;

    public event Action<IReadOnlyList<LinkedLolAccount>>? AccountsChanged;
    public event Action<string>? Acked;
    public event Action<string>? Error;

    public bool IsAvailable => _conn.Modules.TryGetValue(Channel, out var m) && m?["available"]?.GetValue<bool>() == true;
    public bool IsEnabled => _conn.Modules.TryGetValue(Channel, out var m) && m?["enabled"]?.GetValue<bool>() == true;
    public IReadOnlyList<LinkedLolAccount> Linked { get; private set; } = Array.Empty<LinkedLolAccount>();

    public LolCoachClient(IDesktopConnection conn)
    {
        _conn = conn;
        _sub = conn.Subscribe(Channel, OnMessage);
        conn.HelloReceived += RefreshFromModule;
        conn.ModuleUpdated += n => { if (n == Channel) RefreshFromModule(); };
    }

    private void RefreshFromModule()
    {
        if (_conn.Modules.TryGetValue(Channel, out var m)) ApplyLinked(m?["linked"]);
    }

    private void ApplyLinked(JsonNode? node)
    {
        if (node is not JsonArray arr) return;
        Linked = arr.OfType<JsonObject>()
            .Select(o => new LinkedLolAccount(o["puuid"]?.GetValue<string>() ?? "", o["name"]?.GetValue<string>() ?? "", o["region"]?.GetValue<string>()))
            .Where(a => a.Puuid.Length > 0).ToList();
        AccountsChanged?.Invoke(Linked);
    }

    public Task SendAsync(string type, object payload, CancellationToken ct) => _conn.SendAsync(Channel, type, payload, ct);

    private void OnMessage(string type, JsonNode msg)
    {
        switch (type)
        {
            case "accounts": ApplyLinked(msg["linked"]); break;
            case "ack": Acked?.Invoke(msg["phase"]?.GetValue<string>() ?? ""); break;
            case "error": Error?.Invoke(msg["message"]?.GetValue<string>() ?? "Error"); break;
        }
    }

    public void Dispose() => _sub.Dispose();
}
