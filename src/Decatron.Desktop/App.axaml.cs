using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
            // Primero la ventana de arranque propia; recién después el chequeo de
            // actualizaciones y la ventana principal. Si hay update, el proceso termina
            // dentro de RunStartupAsync y el splash nunca llega a cerrarse: es esperado.
            var splashVm = new SplashViewModel();
            var splash = new SplashWindow { DataContext = splashVm };
            desktop.MainWindow = splash;
            splash.Show();
            _ = RunStartupAsync(desktop, splash, splashVm);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunStartupAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash, SplashViewModel splashVm)
    {
        try
        {
            if (Updates != null)
                await Updates.CheckAndApplyBeforeLaunchAsync(desktop.Args ?? [], s => Dispatcher.UIThread.Post(() => splashVm.StatusText = s));
        }
        catch (Exception ex) { Logging.CreateLogger("Updates").LogWarning(ex, "chequeo de actualización al arrancar"); }

        splashVm.StatusText = "Iniciando…";
        _host = new AppHost(Logging);
        var vm = new MainViewModel(_host);
        var main = new MainWindow { DataContext = vm };
        if (Updates != null)
        {
            Updates.UpdateAvailable += v => Dispatcher.UIThread.Post(() => vm.UpdateBanner = $"Versión {v} disponible. Se instala la próxima vez que abras Decatron Desktop.");
            Updates.StartBackgroundNotifier();
        }
        // Cerrar en serio. Esperar la limpieza (mic, cliente de LoL, WebSocket) como mucho
        // 3 s y sin bloquear el hilo de UI (los módulos publican estado por Dispatcher al
        // parar: esperarlos sincrónicamente aquí era un interbloqueo). Y al terminar el
        // loop, Environment.Exit: si quedó algún hilo en primer plano (captura de audio,
        // HttpClient del LCU), el proceso seguía vivo tras cerrar la ventana.
        desktop.ShutdownRequested += (_, _) =>
        {
            try { Task.Run(() => _host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { Logging.CreateLogger("App").LogWarning(ex, "limpieza al cerrar"); }
        };
        desktop.Exit += (_, _) => Environment.Exit(0);
        desktop.MainWindow = main;
        main.Show();
        splash.Close();
        _ = _host.StartAsync();
    }
}
