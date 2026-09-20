using Avalonia.Controls;
using Decatron.Desktop.Sdk;

namespace Decatron.Desktop.Modules.LolCoach;

public sealed class LolCoachModule : IModule
{
    private readonly LolCoachViewModel _vm = new();

    public string Id => LolCoachClient.Channel;
    public string Title => "Coach de LoL";
    public string Icon => "🎮";
    public object ViewModel => _vm;

    public Control CreateView() => new LolCoachView { DataContext = _vm };

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _vm.Attach(context);
        return Task.CompletedTask;
    }

    public Task StopAsync() => _vm.ShutdownAsync();
}
