using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Decatron.Desktop.Services;

/// <summary>
/// Auto-update con Velopack contra los releases de GitHub, mismo esquema que Flowdeck:
/// se verifica y aplica ANTES de abrir la ventana principal, con la ventana de arranque
/// propia visible. Así el usuario nunca ve nada nativo y la actualización no depende de
/// que la app se cierre de forma prolija (el esquema anterior "descargar en segundo
/// plano y aplicar al cerrar" se perdía si se mataba el proceso o se apagaba la PC).
///
/// OJO: ApplyUpdatesAndRestart no tiene parámetro silent y muestra el diálogo nativo de
/// Velopack ("Instalando actualización…") — comprobado en Flowdeck. Por eso se usa
/// WaitExitThenApplyUpdates(silent: true, restart: true) seguido de Environment.Exit.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/decatrondev/decatron-desktop";
    private readonly ILogger _log;
    private readonly UpdateManager? _mgr;

    /// <summary>Hay una versión nueva publicada; se instala en el próximo arranque.</summary>
    public event Action<string>? UpdateAvailable;

    public UpdateService(ILogger log)
    {
        _log = log;
        try { _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
        catch (Exception ex) { _log.LogDebug(ex, "Velopack no disponible (ejecución desde build local)"); }
    }

    public bool IsInstalled => _mgr?.IsInstalled ?? false;
    public string? CurrentVersion => IsInstalled ? _mgr!.CurrentVersion?.ToString() : null;

    /// <summary>
    /// Bloquea el arranque a propósito. Si hay actualización, este método no retorna: el
    /// proceso termina y Velopack relanza la versión nueva con los mismos argumentos.
    /// Sin red, GitHub caído o rate limit → sigue el arranque normal y se reintenta la próxima.
    /// </summary>
    public async Task CheckAndApplyBeforeLaunchAsync(string[] restartArgs, Action<string>? onStatus = null)
    {
        if (_mgr == null || !_mgr.IsInstalled) return;   // dotnet run o portable: no hay nada que chequear

        onStatus?.Invoke("Verificando actualizaciones…");
        UpdateInfo? info;
        try { info = await _mgr.CheckForUpdatesAsync(); }
        catch (Exception ex) { _log.LogDebug(ex, "update check al arrancar"); return; }
        if (info == null) return;

        var v = info.TargetFullRelease.Version;
        try
        {
            onStatus?.Invoke($"Descargando la versión {v}…");
            await _mgr.DownloadUpdatesAsync(info, p => onStatus?.Invoke($"Descargando la versión {v}… {p}%"));
        }
        catch (Exception ex) { _log.LogWarning(ex, "no se pudo descargar la actualización {V}", v); return; }

        onStatus?.Invoke("Instalando la actualización…");
        _log.LogInformation("aplicando actualización {V} y reiniciando", v);
        _mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: true, restartArgs: restartArgs);
        Environment.Exit(0);
    }

    /// <summary>
    /// Con la app abierta solo se AVISA cada 6 h: nunca se aplica nada en caliente. Se instala
    /// la próxima vez que se abra la app, desde la ventana de arranque.
    /// </summary>
    public void StartBackgroundNotifier()
    {
        if (_mgr == null || !_mgr.IsInstalled) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromHours(6));
                try
                {
                    var info = await _mgr.CheckForUpdatesAsync();
                    if (info != null) { UpdateAvailable?.Invoke(info.TargetFullRelease.Version.ToString()); return; }
                }
                catch (Exception ex) { _log.LogDebug(ex, "update check periódico"); }
            }
        });
    }
}
