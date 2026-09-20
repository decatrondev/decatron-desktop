using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Modules.LolCoach;

public sealed record LcuSummoner(string Puuid, string GameName, string? TagLine, string DisplayName);

/// <summary>
/// Vigila el cliente de LoL y avisa lo que pasa. Es SOLO lectura del LCU (nada de
/// aceptar cola, lockear ni escribir): lo único que Riot permite. Corre en su propia
/// tarea; los eventos salen en hilos ajenos, quien los consuma pasa por la UI.
///
/// Cadencia: 1 s en champ select (los picks cambian rápido), 2 s en el resto, 3 s en
/// lobby. Cada dato se manda solo si cambió respecto al último envío.
/// </summary>
public sealed class LolClientWatcher : IAsyncDisposable
{
    private readonly Func<string?> _lockfileOverride;
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private LcuClient? _lcu;
    private string _lastPhase = "";
    private string _lastChampSelectJson = "";
    private string _lastLobbyJson = "";
    private string? _myPosition;
    private DateTime? _gameStartedAt;
    private bool _sentInGame, _sentEog;

    public event Action<bool, LcuSummoner?>? ClientChanged;                 // conectado/no + invocador
    public event Action<object>? PhaseChanged;                               // payload "phase"
    public event Action<object>? ChampSelectChanged;                          // payload "champselect"
    public event Action<object>? InGame;                                      // payload "ingame"
    public event Action<object>? EndOfGame;                                   // payload "eog"
    public event Action<string>? Log;

    public bool Connected { get; private set; }
    public LcuSummoner? Summoner { get; private set; }
    public string Phase { get; private set; } = "None";

    public LolClientWatcher(Func<string?> lockfileOverride, ILogger log)
    {
        _lockfileOverride = lockfileOverride;
        _log = log;
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_loop != null) await _loop; } catch { }
        _loop = null; _cts = null;
        // Soltar el cliente: al volver a arrancar hay que redetectarlo (y re-avisar) desde cero.
        ResetClient();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_lcu == null)
                {
                    var ep = LcuLocator.Find(_lockfileOverride());
                    if (ep == null) { SetConnected(false, null); await Task.Delay(3000, ct); continue; }
                    _lcu = new LcuClient(ep);
                    var s = await _lcu.GetAsync("/lol-summoner/v1/current-summoner", ct);
                    if (s == null) { ResetClient(); await Task.Delay(3000, ct); continue; }
                    var summoner = new LcuSummoner(
                        s["puuid"]?.GetValue<string>() ?? "",
                        s["gameName"]?.GetValue<string>() ?? s["displayName"]?.GetValue<string>() ?? "",
                        s["tagLine"]?.GetValue<string>(),
                        s["displayName"]?.GetValue<string>() ?? "");
                    SetConnected(true, summoner);
                    Log?.Invoke($"Cliente de LoL detectado ({ep.LockfilePath})");
                }

                await TickAsync(ct);
                await Task.Delay(Phase == "ChampSelect" ? 1000 : Phase == "Lobby" ? 3000 : 2000, ct);
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException)
            {
                // El cliente se cerró (o cambió de puerto): redetectar desde cero.
                ResetClient();
                try { await Task.Delay(3000, ct); } catch { }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LCU tick");
                try { await Task.Delay(3000, ct); } catch { }
            }
        }
    }

    private void ResetClient()
    {
        _lcu?.Dispose(); _lcu = null;
        _lastPhase = ""; _lastChampSelectJson = ""; _lastLobbyJson = ""; _sentInGame = _sentEog = false; _gameStartedAt = null;
        SetConnected(false, null);
    }

    private void SetConnected(bool connected, LcuSummoner? summoner)
    {
        if (Connected == connected && Equals(Summoner, summoner)) return;
        Connected = connected; Summoner = summoner;
        if (!connected) Phase = "None";
        ClientChanged?.Invoke(connected, summoner);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var lcu = _lcu!;
        var phaseNode = await lcu.GetAsync("/lol-gameflow/v1/gameflow-phase", ct);
        var phase = phaseNode?.GetValue<string>() ?? "None";
        Phase = phase;

        // Fase + cola + lobby: se manda al cambiar la fase o el lobby.
        string? queueName = null; int? queueId = null;
        JsonArray? lobby = null;
        if (phase is "Lobby" or "Matchmaking" or "ReadyCheck" or "ChampSelect" or "GameStart" or "InProgress")
        {
            var session = await lcu.GetAsync("/lol-gameflow/v1/session", ct);
            var queue = session?["gameData"]?["queue"];
            queueId = queue?["id"]?.GetValue<int?>();
            queueName = queue?["description"]?.GetValue<string>() ?? queue?["name"]?.GetValue<string>();
            if (queueId is <= 0) queueId = null;
        }
        if (phase is "Lobby" or "Matchmaking" or "ReadyCheck")
        {
            var l = await lcu.GetAsync("/lol-lobby/v2/lobby", ct);
            var localPuuid = l?["localMember"]?["puuid"]?.GetValue<string>();
            lobby = new JsonArray();
            foreach (var m in (l?["members"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var name = m["gameName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) name = m["summonerName"]?.GetValue<string>() ?? "";
                lobby.Add(new JsonObject
                {
                    ["name"] = name, ["tag"] = m["gameTag"]?.GetValue<string>(), ["puuid"] = m["puuid"]?.GetValue<string>(),
                    ["isMe"] = m["puuid"]?.GetValue<string>() == localPuuid, ["isLeader"] = m["isLeader"]?.GetValue<bool>() ?? false,
                    ["position1"] = m["firstPositionPreference"]?.GetValue<string>(), ["position2"] = m["secondPositionPreference"]?.GetValue<string>(),
                });
            }
        }
        var lobbyJson = lobby?.ToJsonString() ?? "";
        if (phase != _lastPhase || lobbyJson != _lastLobbyJson)
        {
            _lastPhase = phase; _lastLobbyJson = lobbyJson;
            if (phase != "ChampSelect") _lastChampSelectJson = "";
            if (phase is not ("GameStart" or "InProgress" or "Reconnect")) { _sentInGame = false; _gameStartedAt = null; }
            if (phase is not ("WaitingForStats" or "PreEndOfGame" or "EndOfGame")) _sentEog = false;
            PhaseChanged?.Invoke(new { phase, queueId, queueName, lobby = lobby == null ? null : JsonSerializer.Deserialize<object>(lobbyJson) });
        }

        switch (phase)
        {
            case "ChampSelect":
                await ChampSelectAsync(lcu, ct);
                break;
            case "GameStart":
            case "InProgress":
            case "Reconnect":
                if (!_sentInGame) await InGameAsync(lcu, queueId, ct);
                break;
            case "WaitingForStats":
            case "PreEndOfGame":
            case "EndOfGame":
                if (!_sentEog) await EndOfGameAsync(lcu, ct);
                break;
        }
    }

    private async Task ChampSelectAsync(LcuClient lcu, CancellationToken ct)
    {
        var s = await lcu.GetAsync("/lol-champ-select/v1/session", ct);
        if (s == null) return;
        var local = s["localPlayerCellId"]?.GetValue<int>() ?? -1;

        // Acciones: qué celdas ya lockearon y a quién le toca ahora.
        var locked = new HashSet<int>(); var myTurn = false;
        foreach (var group in (s["actions"] as JsonArray)?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>())
            foreach (var a in group.OfType<JsonObject>())
            {
                if (a["type"]?.GetValue<string>() != "pick") continue;
                var cell = a["actorCellId"]?.GetValue<int>() ?? -1;
                if (a["completed"]?.GetValue<bool>() == true) locked.Add(cell);
                else if (a["isInProgress"]?.GetValue<bool>() == true && cell == local) myTurn = true;
            }

        JsonArray Team(JsonNode? arr, bool mine) => new JsonArray((arr as JsonArray)?.OfType<JsonObject>().Select(p =>
        {
            var cell = p["cellId"]?.GetValue<int>() ?? -1;
            var champ = p["championId"]?.GetValue<int>() ?? 0;
            if (champ == 0) champ = p["championPickIntent"]?.GetValue<int>() ?? 0;
            var o = new JsonObject { ["cellId"] = cell, ["championId"] = champ, ["locked"] = locked.Contains(cell) };
            if (mine)
            {
                o["position"] = (p["assignedPosition"]?.GetValue<string>() ?? "").ToUpperInvariant();
                o["spell1Id"] = p["spell1Id"]?.GetValue<int?>(); o["spell2Id"] = p["spell2Id"]?.GetValue<int?>();
                if (cell == local) _myPosition = o["position"]?.GetValue<string>();
            }
            return (JsonNode)o;
        }).ToArray() ?? Array.Empty<JsonNode>());

        JsonArray Bans(JsonNode? arr) => new JsonArray((arr as JsonArray)?.Select(x => (JsonNode)(x?.GetValue<int>() ?? 0)).Where(x => x.GetValue<int>() > 0).ToArray() ?? Array.Empty<JsonNode>());

        var payload = new JsonObject
        {
            ["timerPhase"] = s["timer"]?["phase"]?.GetValue<string>(),
            ["remainingMs"] = s["timer"]?["adjustedTimeLeftInPhase"]?.GetValue<int?>(),
            ["localCellId"] = local,
            ["myTurn"] = myTurn,
            ["myTeam"] = Team(s["myTeam"], true),
            ["theirTeam"] = Team(s["theirTeam"], false),
            ["myBans"] = Bans(s["bans"]?["myTeamBans"]),
            ["theirBans"] = Bans(s["bans"]?["theirTeamBans"]),
        };
        // El timer cambia cada segundo: se compara sin él para no mandar 1 msg/s sin novedad real.
        var cmp = new JsonObject(payload.Where(kv => kv.Key != "remainingMs").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
        var json = cmp.ToJsonString();
        if (json == _lastChampSelectJson) return;
        _lastChampSelectJson = json;
        ChampSelectChanged?.Invoke(JsonSerializer.Deserialize<object>(payload.ToJsonString())!);
    }

    private async Task InGameAsync(LcuClient lcu, int? queueId, CancellationToken ct)
    {
        var session = await lcu.GetAsync("/lol-gameflow/v1/session", ct);
        var myPuuid = Summoner?.Puuid;
        var championId = 0;
        foreach (var p in (session?["gameData"]?["playerChampionSelections"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var puuid = p["puuid"]?.GetValue<string>();
            var name = p["summonerInternalName"]?.GetValue<string>();
            if ((puuid != null && puuid == myPuuid) || (puuid == null && name != null && Summoner != null && name.Equals(Summoner.GameName, StringComparison.OrdinalIgnoreCase)))
            { championId = p["championId"]?.GetValue<int>() ?? 0; break; }
        }
        _gameStartedAt ??= DateTime.UtcNow;
        _sentInGame = true;
        InGame?.Invoke(new
        {
            championId, position = _myPosition, startedAt = _gameStartedAt,
            gameMode = session?["gameData"]?["queue"]?["gameMode"]?.GetValue<string>() ?? session?["map"]?["gameMode"]?.GetValue<string>(),
            queueId,
        });
    }

    private async Task EndOfGameAsync(LcuClient lcu, CancellationToken ct)
    {
        var eog = await lcu.GetAsync("/lol-end-of-game/v1/eog-stats-block", ct);
        if (eog == null) return; // todavía no está; se reintenta en el próximo tick
        var me = eog["localPlayer"] as JsonObject;
        if (me == null) return;
        int Stat(JsonObject? p, string k) => p?["stats"]?[k]?.GetValue<int?>() ?? 0;
        var length = eog["gameLength"]?.GetValue<double?>() ?? 0;
        if (length > 36000) length /= 1000; // algunas versiones lo dan en ms

        JsonArray Team(bool mine) => new JsonArray(((eog["teams"] as JsonArray)?.OfType<JsonObject>()
            .Where(t => (t["isPlayerTeam"]?.GetValue<bool>() ?? false) == mine)
            .SelectMany(t => (t["players"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            .Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p["gameName"]?.GetValue<string>() ?? p["summonerName"]?.GetValue<string>() ?? "",
                ["championId"] = p["championId"]?.GetValue<int>() ?? 0,
                ["kills"] = Stat(p, "CHAMPIONS_KILLED"), ["deaths"] = Stat(p, "NUM_DEATHS"), ["assists"] = Stat(p, "ASSISTS"),
                ["isMe"] = p["isLocalPlayer"]?.GetValue<bool>() ?? false,
            })).ToArray() ?? Array.Empty<JsonNode>());

        _sentEog = true;
        EndOfGame?.Invoke(new
        {
            win = Stat(me, "WIN") == 1 || (eog["teams"] as JsonArray)?.OfType<JsonObject>().Any(t => t["isPlayerTeam"]?.GetValue<bool>() == true && t["isWinningTeam"]?.GetValue<bool>() == true) == true,
            championId = me["championId"]?.GetValue<int>() ?? 0,
            kills = Stat(me, "CHAMPIONS_KILLED"), deaths = Stat(me, "NUM_DEATHS"), assists = Stat(me, "ASSISTS"),
            cs = Stat(me, "MINIONS_KILLED") + Stat(me, "NEUTRAL_MINIONS_KILLED"),
            damage = Stat(me, "TOTAL_DAMAGE_DEALT_TO_CHAMPIONS"),
            visionScore = Stat(me, "VISION_SCORE"),
            durationSeconds = (int)length,
            pointsDelta = me["leaguePointsDelta"]?.GetValue<int?>(),
            myTeam = JsonSerializer.Deserialize<object>(Team(true).ToJsonString()),
            theirTeam = JsonSerializer.Deserialize<object>(Team(false).ToJsonString()),
        });
    }
}
