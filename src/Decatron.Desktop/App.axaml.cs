using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Decatron.Desktop.Services;
using Decatron.Desktop.ViewModels;
using Decatron.Desktop.Views;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop;

public partial class App : Application
{
    public static ILoggerFactory Logging { get; set; } = LoggerFactory.Create(_ => { });
    public static UpdateService? Updates { get; set; }

    private AppHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost(Logging);
            var vm = new MainViewModel(_host);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            if (Updates != null) Updates.UpdateReady += v => Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.UpdateBanner = $"Versión {v} descargada. Se instala al cerrar la app.");
            desktop.ShutdownRequested += (_, _) => { _host.DisposeAsync().AsTask().GetAwaiter().GetResult(); Updates?.ApplyPendingOnExit(); };
            _ = _host.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
