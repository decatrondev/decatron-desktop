using System.Text.Json;
using Decatron.Desktop.Modules.LolCoach;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Decatron.Desktop.Tests;

public class LolCoachTests
{
    [Fact]
    public void Lockfile_se_parsea_y_falta_cuando_el_cliente_esta_cerrado()
    {
        var dir = Path.Combine(Path.GetTempPath(), "decatron-lockfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "lockfile");
            Assert.Null(LcuLocator.TryRead(path));
            File.WriteAllText(path, "LeagueClient:5555:54321:abc:https");
            var ep = LcuLocator.TryRead(path)!;
            Assert.Equal(54321, ep.Port);
            Assert.Equal("abc", ep.Password);
            Assert.Equal("https", ep.Protocol);
            // Ruta de carpeta o del archivo: las dos valen.
            Assert.Equal(path, LcuLocator.CandidateLockfiles(dir).First());
            Assert.Equal(path, LcuLocator.CandidateLockfiles(path).First());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Recorre_lobby_champselect_partida_y_fin_mandando_solo_cambios()
    {
        await using var lcu = new FakeLcuServer();
        lcu.Set("/lol-summoner/v1/current-summoner", """{"puuid":"me-puuid","gameName":"Deca","tagLine":"LAN","displayName":"Deca"}""");
        lcu.Set("/lol-gameflow/v1/gameflow-phase", "\"Lobby\"");
        lcu.Set("/lol-gameflow/v1/session", """{"gameData":{"queue":{"id":420,"description":"Ranked Solo/Duo","gameMode":"CLASSIC"}}}""");
        lcu.Set("/lol-lobby/v2/lobby", """{"localMember":{"puuid":"me-puuid"},"members":[{"puuid":"me-puuid","gameName":"Deca","gameTag":"LAN","isLeader":true,"firstPositionPreference":"BOTTOM"},{"puuid":"p2","gameName":"Roba","gameTag":"LAN","isLeader":false}]}""");

        var watcher = new LolClientWatcher(() => lcu.Dir, NullLogger.Instance);
        var phases = new List<string>(); var champSelects = new List<JsonElement>(); var inGame = new List<JsonElement>(); var eogs = new List<JsonElement>();
        var connected = new TaskCompletionSource<LcuSummoner?>();
        watcher.ClientChanged += (c, s) => { if (c) connected.TrySetResult(s); };
        watcher.PhaseChanged += p => { lock (phases) phases.Add(Json(p).GetProperty("phase").GetString()!); };
        watcher.ChampSelectChanged += p => { lock (champSelects) champSelects.Add(Json(p)); };
        watcher.InGame += p => { lock (inGame) inGame.Add(Json(p)); };
        watcher.EndOfGame += p => { lock (eogs) eogs.Add(Json(p)); };
        watcher.Start();

        var summoner = await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("me-puuid", summoner!.Puuid);
        await WaitUntil(() => { lock (phases) return phases.Contains("Lobby"); });

        // Champ select: dos ticks con el mismo estado (timer distinto) = un solo envío.
        lcu.Set("/lol-gameflow/v1/gameflow-phase", "\"ChampSelect\"");
        lcu.Set("/lol-champ-select/v1/session", ChampSelect(remaining: 30000, myChamp: 51, lockedCells: new[] { 0 }, myTurn: true));
        await WaitUntil(() => { lock (champSelects) return champSelects.Count >= 1; });
        lcu.Set("/lol-champ-select/v1/session", ChampSelect(remaining: 12000, myChamp: 51, lockedCells: new[] { 0 }, myTurn: true));
        await Task.Delay(2500);
        lock (champSelects) Assert.Single(champSelects);
        var cs = champSelects[0];
        Assert.True(cs.GetProperty("myTurn").GetBoolean());
        Assert.Equal(3, cs.GetProperty("localCellId").GetInt32());
        Assert.Equal("BOTTOM", cs.GetProperty("myTeam")[3].GetProperty("position").GetString());
        Assert.True(cs.GetProperty("myTeam")[0].GetProperty("locked").GetBoolean());
        Assert.False(cs.GetProperty("myTeam")[3].GetProperty("locked").GetBoolean());
        Assert.Equal(238, cs.GetProperty("theirBans")[0].GetInt32());

        // Cambia el pick: se manda de nuevo.
        lcu.Set("/lol-champ-select/v1/session", ChampSelect(remaining: 9000, myChamp: 202, lockedCells: new[] { 0, 3 }, myTurn: false));
        await WaitUntil(() => { lock (champSelects) return champSelects.Count >= 2; });
        Assert.Equal(202, champSelects[1].GetProperty("myTeam")[3].GetProperty("championId").GetInt32());

        // Partida: una sola vez, con el campeón de la selección de jugadores y la posición recordada.
        lcu.Set("/lol-gameflow/v1/gameflow-phase", "\"InProgress\"");
        lcu.Set("/lol-gameflow/v1/session", """{"gameData":{"queue":{"id":420,"description":"Ranked Solo/Duo","gameMode":"CLASSIC"},"playerChampionSelections":[{"puuid":"me-puuid","championId":202},{"puuid":"p2","championId":64}]}}""");
        await WaitUntil(() => { lock (inGame) return inGame.Count >= 1; });
        await Task.Delay(2500);
        lock (inGame) Assert.Single(inGame);
        Assert.Equal(202, inGame[0].GetProperty("championId").GetInt32());
        Assert.Equal("BOTTOM", inGame[0].GetProperty("position").GetString());

        // Fin: el bloque de stats tarda en aparecer (404 primero) y luego se manda una vez.
        lcu.Set("/lol-gameflow/v1/gameflow-phase", "\"EndOfGame\"");
        await Task.Delay(2500);
        lock (eogs) Assert.Empty(eogs);
        lcu.Set("/lol-end-of-game/v1/eog-stats-block", """{"gameLength":1832,"localPlayer":{"championId":202,"isLocalPlayer":true,"leaguePointsDelta":22,"stats":{"CHAMPIONS_KILLED":9,"NUM_DEATHS":2,"ASSISTS":7,"MINIONS_KILLED":180,"NEUTRAL_MINIONS_KILLED":12,"TOTAL_DAMAGE_DEALT_TO_CHAMPIONS":22000,"VISION_SCORE":18,"WIN":1}},"teams":[{"isPlayerTeam":true,"isWinningTeam":true,"players":[{"gameName":"Deca","championId":202,"isLocalPlayer":true,"stats":{"CHAMPIONS_KILLED":9,"NUM_DEATHS":2,"ASSISTS":7}}]},{"isPlayerTeam":false,"isWinningTeam":false,"players":[{"gameName":"Enemy","championId":157,"stats":{"CHAMPIONS_KILLED":3,"NUM_DEATHS":9,"ASSISTS":1}}]}]}""");
        await WaitUntil(() => { lock (eogs) return eogs.Count >= 1; });
        var eog = eogs[0];
        Assert.True(eog.GetProperty("win").GetBoolean());
        Assert.Equal(192, eog.GetProperty("cs").GetInt32());
        Assert.Equal(22, eog.GetProperty("pointsDelta").GetInt32());
        Assert.Equal(1832, eog.GetProperty("durationSeconds").GetInt32());
        Assert.Equal("Enemy", eog.GetProperty("theirTeam")[0].GetProperty("name").GetString());

        await watcher.StopAsync();
        Assert.Equal(new[] { "Lobby", "ChampSelect", "InProgress", "EndOfGame" }, phases);
    }

    [Fact]
    public async Task Si_el_cliente_se_cierra_vuelve_a_buscarlo()
    {
        await using var lcu = new FakeLcuServer();
        lcu.Set("/lol-summoner/v1/current-summoner", """{"puuid":"me","gameName":"Deca","tagLine":"LAN","displayName":"Deca"}""");
        lcu.Set("/lol-gameflow/v1/gameflow-phase", "\"None\"");
        var watcher = new LolClientWatcher(() => lcu.Dir, NullLogger.Instance);
        var states = new List<bool>();
        var gotFalseAfterTrue = new TaskCompletionSource();
        watcher.ClientChanged += (c, _) => { lock (states) { states.Add(c); if (!c && states.Contains(true)) gotFalseAfterTrue.TrySetResult(); } };
        watcher.Start();
        await WaitUntil(() => { lock (states) return states.Contains(true); });
        await lcu.DisposeAsync(); // cierra el puerto y borra el lockfile, como al cerrar el cliente
        await gotFalseAfterTrue.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await watcher.StopAsync();
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 10000)
    {
        var start = DateTime.UtcNow;
        while (!cond())
        {
            if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs) throw new TimeoutException("condición no cumplida");
            await Task.Delay(100);
        }
    }

    private static string ChampSelect(int remaining, int myChamp, int[] lockedCells, bool myTurn)
    {
        var actions = string.Join(",", Enumerable.Range(0, 5).Select(c =>
            $$"""{"actorCellId":{{c}},"type":"pick","completed":{{(lockedCells.Contains(c) ? "true" : "false")}},"isInProgress":{{(c == 3 && myTurn ? "true" : "false")}},"championId":{{(c == 3 ? myChamp : 100 + c)}}}"""));
        return $$"""
        {"localPlayerCellId":3,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":{{remaining}}},
         "myTeam":[{"cellId":0,"championId":266,"assignedPosition":"top"},{"cellId":1,"championId":64,"assignedPosition":"jungle"},{"cellId":2,"championId":0,"championPickIntent":103,"assignedPosition":"middle"},{"cellId":3,"championId":{{myChamp}},"assignedPosition":"bottom","spell1Id":4,"spell2Id":7},{"cellId":4,"championId":0,"assignedPosition":"utility"}],
         "theirTeam":[{"cellId":5,"championId":157},{"cellId":6,"championId":0}],
         "bans":{"myTeamBans":[555],"theirTeamBans":[238,0]},
         "actions":[[{{actions}}]]}
        """;
    }
}
