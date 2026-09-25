using Avalonia.Controls;
using Decatron.Desktop.Sdk;

namespace Decatron.Desktop.Modules.Downloads;

public sealed class DownloadsModule : IModule
{
    private readonly DownloadsViewModel _vm = new();

    public string Id => DownloadsClient.Channel;
    public string Title => "Descargas";
    public string Icon => "⬇️";
    public object ViewModel => _vm;

    public Control CreateView() => new DownloadsView { DataContext = _vm };

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _vm.Attach(context);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _vm.Shutdown();
        return Task.CompletedTask;
    }
}
