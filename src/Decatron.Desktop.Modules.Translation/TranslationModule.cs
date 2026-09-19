using Avalonia.Controls;
using Decatron.Desktop.Sdk;

namespace Decatron.Desktop.Modules.Translation;

public sealed class TranslationModule : IModule
{
    private readonly TranslationViewModel _vm = new();

    public string Id => TranslationClient.Channel;
    public string Title => "Traducción en vivo";
    public string Icon => "🌐";
    public object ViewModel => _vm;

    public Control CreateView() => new TranslationView { DataContext = _vm };

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        _vm.Attach(context);
        return Task.CompletedTask;
    }

    public Task StopAsync() => _vm.ShutdownAsync();
}
