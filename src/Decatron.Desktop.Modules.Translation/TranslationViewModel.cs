using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Decatron.Desktop.Core.Audio;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Modules.Translation;

/// <summary>
/// Estado de la pantalla de traducción: micrófono, sesión y contadores. Toda mutación
/// de propiedades observables pasa por el hilo de UI (los eventos de audio y de red
/// llegan de otros hilos).
/// </summary>
public sealed partial class TranslationViewModel : ObservableObject
{
    private ModuleContext? _ctx;
    private TranslationClient? _client;
    private IAudioCapture? _capture;
    private VoiceGate _gate = new();
    private CancellationTokenSource? _sessionCts;
    private ILogger? _log;
    private DateTime _lastLevelUi;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isEnabledOnServer;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _micSupported = true;
    [ObservableProperty] private string? _micUnsupportedReason;
    [ObservableProperty] private float _level;
    [ObservableProperty] private bool _voiceOpen;
    [ObservableProperty] private string _languagesText = "";
    [ObservableProperty] private string _listenersText = "0";
    [ObservableProperty] private string _speechText = "0:00";
    [ObservableProperty] private int _segments;
    [ObservableProperty] private long _creditsUsed;
    [ObservableProperty] private string _lastPhraseText = "—";
    private DateTime? _lastSegmentAt;
    private int _lastSegments;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;
    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;
    [ObservableProperty] private bool _autoStart;

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    public ObservableCollection<ListenerRow> Listeners { get; } = new();

    public sealed record ListenerRow(string Language, int Count, bool Active);

    public void Attach(ModuleContext ctx)
    {
        _ctx = ctx;
        _log = ctx.LoggerFactory.CreateLogger("Translation");
        _client = new TranslationClient(ctx.Connection);
        _client.StatusChanged += s => Dispatcher.UIThread.Post(() => ApplyStatus(s));
        _client.Started += () => Dispatcher.UIThread.Post(() => { IsRunning = true; IsBusy = false; SetMessage("Traduciendo en vivo", false); });
        _client.Stopped += (reason, err) => Dispatcher.UIThread.Post(() => OnStopped(reason, err));
        _client.Error += e => Dispatcher.UIThread.Post(() => { IsBusy = false; SetMessage(e, true); });
        ctx.Connection.HelloReceived += () => Dispatcher.UIThread.Post(RefreshFromServer);
        ctx.Connection.ModuleUpdated += name => { if (name == TranslationClient.Channel) Dispatcher.UIThread.Post(RefreshFromServer); };
        ctx.Connection.StateChanged += st => Dispatcher.UIThread.Post(() =>
        {
            if (st != ConnectionState.Connected && IsRunning) { _ = StopCaptureAsync(); IsRunning = false; SetMessage("Conexión perdida; la sesión se cortó", true); }
        });

        MicSupported = ctx.Audio.IsSupported;
        MicUnsupportedReason = ctx.Audio.UnsupportedReason;
        RefreshDevices();
        AutoStart = ctx.Settings.Get<bool>("autoStart");
        var savedDevice = ctx.Settings.Get<string>("deviceId");
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == savedDevice) ?? Devices.FirstOrDefault(d => d.IsDefault) ?? Devices.FirstOrDefault();
        RefreshFromServer();
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (_ctx == null || value == null) return;
        _ctx.Settings.Set("deviceId", value.Id);
        _ = _ctx.Settings.SaveAsync();
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_ctx == null) return;
        _ctx.Settings.Set("autoStart", value);
        _ = _ctx.Settings.SaveAsync();
    }

    [RelayCommand]
    public void RefreshDevices()
    {
        if (_ctx == null) return;
        var current = SelectedDevice?.Id;
        Devices.Clear();
        foreach (var d in _ctx.Audio.ListInputDevices()) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == current) ?? Devices.FirstOrDefault(d => d.IsDefault) ?? Devices.FirstOrDefault();
    }

    private void RefreshFromServer()
    {
        if (_client == null) return;
        IsAvailable = _client.IsAvailable;
        IsEnabledOnServer = _client.IsEnabled;
        LanguagesText = string.Join(" · ", _client.ConfiguredLanguages.Select(l => l.ToUpperInvariant()));
        if (!IsEnabledOnServer)
            SetMessage("Activa la traducción en vivo desde el dashboard (Funciones → Traducción en vivo).", false);
        else if (!IsRunning)
            SetMessage(null, false);
        if (AutoStart && IsEnabledOnServer && !IsRunning && _ctx?.Connection.State == ConnectionState.Connected)
            _ = StartAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_ctx == null || _client == null) return;
        IsBusy = true;
        SetMessage("Conectando…", false);
        try
        {
            _sessionCts = new CancellationTokenSource();
            _capture = _ctx.Audio.Create(SelectedDevice?.Id);
            _gate = new VoiceGate();
            _capture.FrameReady += OnFrame;
            await _capture.StartAsync(_sessionCts.Token);
            await _client.StartAsync(_sessionCts.Token);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "no se pudo iniciar");
            await StopCaptureAsync();
            IsBusy = false;
            SetMessage("No se pudo abrir el micrófono: " + ex.Message, true);
        }
    }
    private bool CanStart() => !IsRunning && !IsBusy && MicSupported && IsEnabledOnServer && SelectedDevice != null;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_client == null) return;
        IsBusy = true;
        await _client.StopAsync(CancellationToken.None);
        await StopCaptureAsync();
    }
    private bool CanStop() => IsRunning && !IsBusy;

    // El audio se manda continuo, silencios incluidos. Deepgram necesita oír el silencio
    // para cerrar cada frase (speech_final) y, si se recorta, su reloj se desfasa del
    // real y las frases quedan colgadas hasta la siguiente. El costo de STT del silencio
    // (~$0,45/h) vale la pena frente a frases que llegan tarde. La puerta de voz queda
    // solo para el medidor de la pantalla.
    private void OnFrame(ReadOnlyMemory<byte> frame)
    {
        var client = _client; var cts = _sessionCts; var cap = _capture;
        if (client == null || cts == null || cts.IsCancellationRequested || cap == null) return;
        var level = cap.Level;
        var pass = _gate.Pass(level);
        _ = client.SendAudioAsync(frame, cts.Token);

        // El medidor se refresca a ~20 fps; los frames llegan a 50/s.
        var now = DateTime.UtcNow;
        if ((now - _lastLevelUi).TotalMilliseconds >= 50)
        {
            _lastLevelUi = now;
            Dispatcher.UIThread.Post(() => { Level = Math.Min(1f, level * 6f); VoiceOpen = pass; });
        }
    }

    private async Task StopCaptureAsync()
    {
        _sessionCts?.Cancel();
        var cap = _capture; _capture = null;
        if (cap != null) { cap.FrameReady -= OnFrame; await cap.DisposeAsync(); }
        Level = 0; VoiceOpen = false;
    }

    private void ApplyStatus(TranslationStatus s)
    {
        IsRunning = s.Active;
        if (s.Segments != _lastSegments) { _lastSegments = s.Segments; _lastSegmentAt = DateTime.UtcNow; }
        LastPhraseText = _lastSegmentAt is { } t ? $"hace {(int)(DateTime.UtcNow - t).TotalSeconds} s" : "—";
        Segments = s.Segments;
        CreditsUsed = s.CreditsUsed;
        var ts = TimeSpan.FromSeconds(s.SpeechSeconds);
        SpeechText = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        ListenersText = s.Listeners.Values.Sum().ToString();
        Listeners.Clear();
        foreach (var lang in s.Languages)
            Listeners.Add(new ListenerRow(lang.ToUpperInvariant(), s.Listeners.GetValueOrDefault(lang), s.ActivePipelines.Contains(lang)));
        if (s.LastError != null) SetMessage(s.LastError, true);
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged();
    }

    private void OnStopped(string reason, string? err)
    {
        _ = StopCaptureAsync();
        IsRunning = false; IsBusy = false;
        var text = reason switch
        {
            "stopped_by_user" => "Sesión detenida.",
            "no_credits" => "Te quedaste sin créditos. Compra un paquete en el dashboard para seguir traduciendo.",
            "timeout" => "El servidor cortó la sesión por falta de audio.",
            "admin" => "Un administrador detuvo la sesión.",
            "server" or "error" => "El servidor cerró la sesión" + (err != null ? ": " + err : "."),
            _ => "Sesión terminada (" + reason + ").",
        };
        SetMessage(text, reason is not "stopped_by_user");
    }

    private void SetMessage(string? m, bool isError)
    {
        Message = m; MessageIsError = isError;
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value) { StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) { StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); }
    partial void OnIsEnabledOnServerChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    public async Task ShutdownAsync()
    {
        if (IsRunning && _client != null) { try { await _client.StopAsync(CancellationToken.None); } catch { } }
        await StopCaptureAsync();
        _client?.Dispose();
    }
}
