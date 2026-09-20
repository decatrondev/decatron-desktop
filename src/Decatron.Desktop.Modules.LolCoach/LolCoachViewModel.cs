using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Modules.LolCoach;

/// <summary>
/// Pantalla del Coach de LoL (fase 1: solo lectura del cliente → overlay en vivo).
/// Muestra si se ve el cliente, qué invocador está abierto, si esa cuenta está
/// vinculada en el dashboard, la fase actual y qué se mandó por última vez.
/// </summary>
public sealed partial class LolCoachViewModel : ObservableObject
{
    private ModuleContext? _ctx;
    private LolCoachClient? _client;
    private LolClientWatcher? _watcher;
    private ILogger? _log;

    [ObservableProperty] private bool _isEnabledOnServer;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _clientDetected;
    [ObservableProperty] private string _summonerText = "—";
    [ObservableProperty] private bool _summonerLinked = true;
    [ObservableProperty] private string _phaseText = "Sin cliente";
    [ObservableProperty] private string _lastSentText = "—";
    [ObservableProperty] private string? _lockfilePath;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;
    [ObservableProperty] private string _linkedText = "";
    [ObservableProperty] private string _matchText = "";
    [ObservableProperty] private bool _coachEnabled;
    [ObservableProperty] private string _coachName = "Coach";
    [ObservableProperty] private string _coachStatusText = "";
    private bool _serverVerdict;

    public ObservableCollection<CoachRow> CoachMessages { get; } = new();
    public sealed record CoachRow(string Title, string Comment, string? Details, string Time);

    public void Attach(ModuleContext ctx)
    {
        _ctx = ctx;
        _log = ctx.LoggerFactory.CreateLogger("LolCoach");
        _client = new LolCoachClient(ctx.Connection);
        _client.AccountsChanged += _ => Dispatcher.UIThread.Post(RefreshLinked);
        _client.Matched += (m, puuid) => Dispatcher.UIThread.Post(() =>
        {
            // El servidor es quien decide (cruza por PUUID y por nombre#tag): manda sobre el cálculo local.
            _serverVerdict = true;
            SummonerLinked = m != null || !ClientDetected;
            MatchText = !ClientDetected ? "" : m != null ? $"Vinculada como {m.Name}" : $"No coincide con ninguna cuenta vinculada (PUUID del cliente: {Short(puuid)})";
        });
        _client.Error += e => Dispatcher.UIThread.Post(() => SetMessage(e, true));
        _client.Coach += m => Dispatcher.UIThread.Post(() =>
        {
            var title = m.Kind switch { "pick" => "Pick / ban", "my_turn" => "¡Tu turno!", "final" => "Plan final", "postgame" => "Post-partida", _ => m.Kind };
            if (m.Suggestion != null) title += $" → {m.Suggestion}";
            var details = string.Join("\n", new[] { m.Matchup, string.Join(" · ", new[] { m.Runes, m.Spells, m.Build }.Where(x => !string.IsNullOrWhiteSpace(x))) }
                .Concat(m.Tips.Select(t => "• " + t)).Where(x => !string.IsNullOrWhiteSpace(x)));
            CoachMessages.Insert(0, new CoachRow($"{m.CoachName} · {title}", m.Comment, details.Length == 0 ? null : details, m.At.ToString("HH:mm:ss")));
            while (CoachMessages.Count > 12) CoachMessages.RemoveAt(CoachMessages.Count - 1);
        });
        ctx.Connection.HelloReceived += () => Dispatcher.UIThread.Post(RefreshFromServer);
        ctx.Connection.ModuleUpdated += n => { if (n == LolCoachClient.Channel) Dispatcher.UIThread.Post(RefreshFromServer); };
        ctx.Connection.StateChanged += st => Dispatcher.UIThread.Post(() =>
        {
            // Al reconectar, el servidor olvidó nuestra fase: re-mandar el estado del cliente.
            if (st == ConnectionState.Connected && _watcher != null) _ = SendClientAsync(_watcher.Connected, _watcher.Summoner);
        });

        Enabled = ctx.Settings.Get<bool?>("enabled") ?? true;
        LockfilePath = ctx.Settings.Get<string>("lockfilePath");

        _watcher = new LolClientWatcher(() => LockfilePath, _log);
        _watcher.ClientChanged += (c, s) => Dispatcher.UIThread.Post(() => OnClientChanged(c, s));
        _watcher.PhaseChanged += p => Send("phase", p);
        _watcher.ChampSelectChanged += p => Send("champselect", p);
        _watcher.InGame += p => Send("ingame", p);
        _watcher.EndOfGame += p => Send("eog", p);
        _watcher.Log += t => _log?.LogInformation("{Msg}", t);

        RefreshFromServer();
        if (Enabled) _watcher.Start();
    }

    private void RefreshFromServer()
    {
        if (_client == null) return;
        IsEnabledOnServer = _client.IsEnabled;
        CoachEnabled = _client.CoachEnabled;
        CoachName = _client.CoachName;
        CoachStatusText = CoachEnabled ? $"{CoachName} está activo: comenta la selección de campeón y opina al terminar." : "El coach con IA está apagado. Actívalo en el dashboard (Funciones → Decatron Coach · LoL).";
        RefreshLinked();
        if (!IsEnabledOnServer)
            SetMessage("Vincula tu cuenta de LoL en el dashboard (Overlays → Game Overlays → Cuentas) para que el overlay muestre lo que pasa en el cliente.", false);
        else SetMessage(null, false);
    }

    private void RefreshLinked()
    {
        var linked = _client?.Linked ?? Array.Empty<LinkedLolAccount>();
        LinkedText = linked.Count == 0 ? "ninguna" : string.Join(", ", linked.Select(a => a.Name));
        if (_serverVerdict) return;
        var puuid = _watcher?.Summoner?.Puuid;
        SummonerLinked = string.IsNullOrEmpty(puuid) || linked.Count == 0 || linked.Any(a => string.Equals(a.Puuid, puuid, StringComparison.OrdinalIgnoreCase));
    }

    private static string Short(string? puuid) => string.IsNullOrEmpty(puuid) ? "—" : puuid.Length > 12 ? puuid[..8] + "…" + puuid[^4..] : puuid;

    private void OnClientChanged(bool connected, LcuSummoner? s)
    {
        ClientDetected = connected;
        _serverVerdict = false;
        MatchText = connected ? "Comprobando con el servidor…" : "";
        SummonerText = s == null ? "—" : string.IsNullOrEmpty(s.TagLine) ? s.GameName : $"{s.GameName}#{s.TagLine}";
        PhaseText = connected ? "En el cliente" : "Sin cliente";
        RefreshLinked();
        _ = SendClientAsync(connected, s);
    }

    private Task SendClientAsync(bool connected, LcuSummoner? s)
    {
        if (_client == null || _ctx?.Connection.State != ConnectionState.Connected) return Task.CompletedTask;
        return _client.SendAsync("client", new { connected, summoner = s == null ? null : new { puuid = s.Puuid, gameName = s.GameName, tagLine = s.TagLine, displayName = s.DisplayName } }, CancellationToken.None);
    }

    private void Send(string type, object payload)
    {
        if (_client == null || _ctx?.Connection.State != ConnectionState.Connected) return;
        _ = _client.SendAsync(type, payload, CancellationToken.None);
        Dispatcher.UIThread.Post(() =>
        {
            LastSentText = $"{type} · {DateTime.Now:HH:mm:ss}";
            if (type == "phase" && _watcher != null) PhaseText = PhaseLabel(_watcher.Phase);
            else if (type == "champselect") PhaseText = "Selección de campeón";
            else if (type == "ingame") PhaseText = "En partida";
            else if (type == "eog") PhaseText = "Fin de partida";
        });
    }

    private static string PhaseLabel(string lcu) => lcu switch
    {
        "None" => "En el cliente", "Lobby" => "En lobby", "Matchmaking" => "Buscando partida", "ReadyCheck" => "Partida encontrada",
        "ChampSelect" => "Selección de campeón", "GameStart" or "InProgress" or "Reconnect" => "En partida",
        "WaitingForStats" or "PreEndOfGame" or "EndOfGame" => "Fin de partida", _ => lcu,
    };

    partial void OnEnabledChanged(bool value)
    {
        _ctx?.Settings.Set("enabled", value); _ = _ctx?.Settings.SaveAsync();
        if (_watcher == null) return;
        if (value) _watcher.Start(); else _ = _watcher.StopAsync();
    }

    partial void OnLockfilePathChanged(string? value)
    {
        _ctx?.Settings.Set("lockfilePath", value ?? ""); _ = _ctx?.Settings.SaveAsync();
    }

    /// <summary>Vuelve a buscar el cliente y a cruzar la cuenta con el servidor (tras cambiar de cuenta en LoL o vincular una nueva).</summary>
    [RelayCommand]
    private async Task RescanAsync()
    {
        if (_watcher == null) return;
        await _watcher.StopAsync();
        Enabled = true;
        _watcher.Start();
    }

    private void SetMessage(string? m, bool error) { Message = m; MessageIsError = error; }

    public async Task ShutdownAsync()
    {
        if (_watcher != null) await _watcher.StopAsync();
        _client?.Dispose();
    }
}
