using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Decatron.Desktop.Sdk;
using Decatron.Desktop.Services;

namespace Decatron.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;

    [ObservableProperty] private bool _isLinked;
    [ObservableProperty] private bool _isLinking;
    [ObservableProperty] private string _linkCode = "";
    [ObservableProperty] private string? _linkError;
    [ObservableProperty] private ConnectionState _connectionState;
    [ObservableProperty] private string _connectionText = "Sin conexión";
    [ObservableProperty] private string? _login;
    [ObservableProperty] private string? _latencyText;
    [ObservableProperty] private ModuleEntry? _selectedModule;
    [ObservableProperty] private Control? _currentView;
    [ObservableProperty] private string? _updateBanner;

    public ObservableCollection<ModuleEntry> Modules { get; } = new();
    public string Version => AppHost.Version;

    public sealed record ModuleEntry(IModule Module)
    {
        public string Title => Module.Title;
        public string Icon => Module.Icon;
    }

    public MainViewModel(AppHost host)
    {
        _host = host;
        foreach (var m in host.Modules) Modules.Add(new ModuleEntry(m));
        IsLinked = host.IsLinked;
        host.Connection.StateChanged += s => Dispatcher.UIThread.Post(() => ApplyState(s));
        host.Connection.HelloReceived += () => Dispatcher.UIThread.Post(() => { Login = host.Connection.Login; ApplyState(host.Connection.State); });
        host.Connection.Unauthorized += () => Dispatcher.UIThread.Post(async () =>
        {
            await host.UnlinkAsync();
            IsLinked = false;
            LinkError = "Esta app fue desvinculada desde el dashboard. Genera un código nuevo para volver a vincularla.";
        });
        host.Connection.LastErrorChanged += _ => Dispatcher.UIThread.Post(() => ApplyState(host.Connection.State));
        SelectedModule = Modules.FirstOrDefault();
        _ = Tick();
    }

    private async Task Tick()
    {
        while (true)
        {
            await Task.Delay(2000);
            var l = _host.Connection.Latency;
            LatencyText = l.HasValue ? $"{l.Value.TotalMilliseconds:F0} ms" : null;
        }
    }

    partial void OnSelectedModuleChanged(ModuleEntry? value)
    {
        CurrentView = value?.Module.CreateView();
    }

    private void ApplyState(ConnectionState s)
    {
        ConnectionState = s;
        ConnectionText = s switch
        {
            ConnectionState.Connected => Login != null ? $"Conectado como {Login}" : "Conectado",
            ConnectionState.Connecting => "Conectando…",
            ConnectionState.Reconnecting => "Reconectando…" + (_host.Connection.LastError != null ? $" ({_host.Connection.LastError})" : ""),
            _ => "Sin conexión",
        };
    }

    [RelayCommand(CanExecute = nameof(CanLink))]
    private async Task LinkAsync()
    {
        IsLinking = true; LinkError = null;
        try
        {
            var r = await _host.LinkAsync(LinkCode, CancellationToken.None);
            if (!r.Success) { LinkError = r.Message; return; }
            IsLinked = true;
            LinkCode = "";
        }
        finally { IsLinking = false; }
    }
    private bool CanLink() => !IsLinking && LinkCode.Replace("-", "").Trim().Length == 8;
    partial void OnLinkCodeChanged(string value) => LinkCommand.NotifyCanExecuteChanged();
    partial void OnIsLinkingChanged(bool value) => LinkCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task UnlinkAsync()
    {
        await _host.UnlinkAsync();
        IsLinked = false;
        Login = null;
        ApplyState(ConnectionState.Disconnected);
    }

    [RelayCommand]
    private void OpenDashboard() => OpenUrl(new Uri(AppHost.ApiBase, "features/live-translation").ToString());

    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
