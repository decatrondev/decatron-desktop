using Avalonia;
using Decatron.Desktop.Core.Settings;
using Decatron.Desktop.Services;
using Microsoft.Extensions.Logging;
using Serilog;
using Velopack;

namespace Decatron.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack tiene que correr antes que nada: en el primer arranque tras instalar o
        // actualizar hace su trabajo y sale, sin mostrar ventana.
        VelopackApp.Build().Run();

        Directory.CreateDirectory(AppSettingsStore.DataDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppSettingsStore.DataDirectory, "logs", "desktop-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        App.Logging = LoggerFactory.Create(b => b.AddSerilog(Log.Logger));

        using var guard = new SingleInstanceGuard();
        if (!guard.IsPrimary) return 0;

        App.Updates = new UpdateService(App.Logging.CreateLogger("Updates"));
        App.Updates.Start();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "la app se cerró por un error");
            throw;
        }
        finally { Log.CloseAndFlush(); }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
