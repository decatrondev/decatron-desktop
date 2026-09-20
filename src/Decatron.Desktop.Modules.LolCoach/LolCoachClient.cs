using System.Text.Json.Nodes;
using Decatron.Desktop.Sdk;

namespace Decatron.Desktop.Modules.LolCoach;

public sealed record LinkedLolAccount(string Puuid, string Name, string? Region);
public sealed record CoachMessage(string Kind, string Comment, string? Suggestion, string? Runes, string? Spells, string? Build, string? Matchup, IReadOnlyList<string> Tips, string CoachName, DateTime At);

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
    /// <summary>Veredicto del servidor sobre la cuenta abierta: la vinculada que coincide (por PUUID o nombre#tag), o null.</summary>
    public event Action<LinkedLolAccount?, string?>? Matched;
    public event Action<string>? Acked;
    /// <summary>El coach (IA) dijo algo: comentario de pick, sugerencia en tu turno, plan final o resumen post-partida.</summary>
    public event Action<CoachMessage>? Coach;
    public event Action<string>? Error;

    public bool IsAvailable => _conn.Modules.TryGetValue(Channel, out var m) && m?["available"]?.GetValue<bool>() == true;
    public bool IsEnabled => _conn.Modules.TryGetValue(Channel, out var m) && m?["enabled"]?.GetValue<bool>() == true;
    public IReadOnlyList<LinkedLolAccount> Linked { get; private set; } = Array.Empty<LinkedLolAccount>();
    /// <summary>Config del coach según el servidor (hello/module): activo y nombre.</summary>
    public bool CoachEnabled => _conn.Modules.TryGetValue(Channel, out var m) && m?["coach"]?["enabled"]?.GetValue<bool>() == true;
    public string CoachName => _conn.Modules.TryGetValue(Channel, out var m) ? m?["coach"]?["name"]?.GetValue<string>() ?? "Coach" : "Coach";

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
            case "accounts":
                ApplyLinked(msg["linked"]);
                if (msg.AsObject().ContainsKey("summonerPuuid"))
                {
                    var m = msg["matched"] as JsonObject;
                    Matched?.Invoke(m == null ? null : new LinkedLolAccount(m["puuid"]?.GetValue<string>() ?? "", m["name"]?.GetValue<string>() ?? "", m["region"]?.GetValue<string>()),
                        msg["summonerPuuid"]?.GetValue<string>());
                }
                break;
            case "ack": Acked?.Invoke(msg["phase"]?.GetValue<string>() ?? ""); break;
            case "coach":
                Coach?.Invoke(new CoachMessage(
                    msg["kind"]?.GetValue<string>() ?? "", msg["comment"]?.GetValue<string>() ?? "",
                    msg["suggestion"]?.GetValue<string>(), msg["runes"]?.GetValue<string>(), msg["spells"]?.GetValue<string>(),
                    msg["build"]?.GetValue<string>(), msg["matchup"]?.GetValue<string>(),
                    (msg["tips"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? "").Where(t => t.Length > 0).ToList() ?? new List<string>(),
                    msg["coachName"]?.GetValue<string>() ?? "Coach", DateTime.Now));
                break;
            case "error": Error?.Invoke(msg["message"]?.GetValue<string>() ?? "Error"); break;
        }
    }

    public void Dispose() => _sub.Dispose();
}
