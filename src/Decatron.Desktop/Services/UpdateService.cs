using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Decatron.Desktop.Services;

/// <summary>
/// Auto-update con Velopack contra los releases de GitHub (mismo esquema que Flowdeck):
/// sin wizard, sin diálogos nativos. Comprueba al arrancar y cada 6 h; si hay versión
/// nueva la descarga en segundo plano y la aplica al cerrar la app.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/decatrondev/decatron-desktop";
    private readonly ILogger _log;
    private UpdateManager? _mgr;
    private UpdateInfo? _pending;

    public event Action<string>? UpdateReady;   // versión lista para aplicar al cerrar

    public UpdateService(ILogger log) { _log = log; }

    public bool IsInstalled => _mgr?.IsInstalled ?? false;

    public void Start()
    {
        try { _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
        catch (Exception ex) { _log.LogDebug(ex, "Velopack no disponible (ejecución desde build local)"); return; }
        if (!_mgr.IsInstalled) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { await CheckAsync(); }
                catch (Exception ex) { _log.LogDebug(ex, "update check"); }
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    private async Task CheckAsync()
    {
        if (_mgr == null || _pending != null) return;
        var info = await _mgr.CheckForUpdatesAsync();
        if (info == null) return;
        await _mgr.DownloadUpdatesAsync(info);
        _pending = info;
        _log.LogInformation("actualización {V} descargada, se aplica al cerrar", info.TargetFullRelease.Version);
        UpdateReady?.Invoke(info.TargetFullRelease.Version.ToString());
    }

    /// <summary>Llamar al salir: si hay update descargado, se aplica y la app se reinicia sola.</summary>
    public void ApplyPendingOnExit()
    {
        if (_mgr == null || _pending == null) return;
        try { _mgr.WaitExitThenApplyUpdates(_pending, silent: true, restart: false); }
        catch (Exception ex) { _log.LogWarning(ex, "no se pudo programar la actualización"); }
    }
}
