using System.Diagnostics;
using System.Reflection;

namespace Decatron.Desktop.Installer;

internal static class Program
{
    private const string ResourceName = "Decatron.Desktop.Installer.SetupInner.exe";
    private const string AppFolder = "DecatronDesktop";   // -u de vpk pack
    private const string AppExe = "DecatronDesktop.exe";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var splash = new SplashForm();
        // El instalador real corre en background: en el hilo de UI la ventana se congela sin pintar.
        _ = RunInstallAsync(splash);
        Application.Run(splash);
    }

    private static async Task RunInstallAsync(SplashForm splash)
    {
        try
        {
            var inner = ExtractInnerSetup();
            if (inner is null)
            {
                splash.ShowError("Este instalador está incompleto. Descárgalo de nuevo desde decatron.net.");
                return;
            }

            splash.SetStatus("Instalando Decatron Desktop…");
            var exit = await RunSilentAsync(inner);
            try { File.Delete(inner); } catch { }

            if (exit != 0)
            {
                splash.ShowError($"La instalación terminó con un error (código {exit}). Revisa que tengas permisos de escritura en tu carpeta de usuario.");
                return;
            }

            splash.SetStatus("Abriendo Decatron Desktop…");
            LaunchAppIfNotRunning();
            await Task.Delay(600);
            splash.CloseFromBackground();
        }
        catch (Exception ex)
        {
            splash.ShowError("No se pudo instalar: " + ex.Message);
        }
    }

    private static string? ExtractInnerSetup()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"decatron-desktop-setup-{Guid.NewGuid():N}.exe");
        using (var f = File.Create(tmp)) stream.CopyTo(f);
        return tmp;
    }

    private static async Task<int> RunSilentAsync(string exe)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, "--silent") { UseShellExecute = false });
        if (p is null) return -1;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    // Velopack instala en %LocalAppData%\DecatronDesktop\current\ y su --silent ya
    // suele lanzar la app; esto es la red por si esa versión no lo hace.
    private static void LaunchAppIfNotRunning()
    {
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)).Length > 0) return;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder, "current", AppExe);
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}
