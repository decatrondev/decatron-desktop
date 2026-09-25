using System.Reflection;
using Decatron.Desktop.Core.Audio;
using Decatron.Desktop.Core.Connection;
using Decatron.Desktop.Core.Linking;
using Decatron.Desktop.Core.Settings;
using Decatron.Desktop.Modules.Downloads;
using Decatron.Desktop.Modules.LolCoach;
using Decatron.Desktop.Modules.Translation;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Services;

/// <summary>
/// Composición de la app: ajustes, token, conexión y módulos. Un solo lugar donde se
/// decide qué módulos existen y en qué orden aparecen.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    public static readonly Uri ApiBase = new(Environment.GetEnvironmentVariable("DECATRON_API") ?? "https://decatron.net/");
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public ILoggerFactory LoggerFactory { get; }
    public AppSettingsStore Settings { get; } = new();
    public SecretStore Secrets { get; } = new();
    public DesktopConnection Connection { get; }
    public LinkService Link { get; }
    public IAudioCaptureFactory Audio { get; } = new AudioCaptureFactory();
    public IAudioPlayer Player { get; } = new AudioPlayer();
    public IReadOnlyList<IModule> Modules { get; }

    private readonly ILogger _log;
    private bool _modulesStarted;

    public AppHost(ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger("App");
        Settings.Load();
        var wsUri = new Uri(ApiBase.ToString().Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/api/desktop/ws");
        Connection = new DesktopConnection(wsUri, () => Secrets.Read(), Version, loggerFactory.CreateLogger("Conn"));
        Link = new LinkService(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, ApiBase);
        Modules = new IModule[]
        {
            new TranslationModule(),
            new LolCoachModule(),
            new DownloadsModule(),
        };
    }

    public bool IsLinked => !string.IsNullOrEmpty(Secrets.Read());

    /// <summary>Arranca conexión y módulos si hay token. Idempotente.</summary>
    public async Task StartAsync()
    {
        if (!IsLinked) return;
        Connection.Start();
        if (_modulesStarted) return;
        _modulesStarted = true;
        foreach (var m in Modules)
        {
            try
            {
                await m.StartAsync(new ModuleContext
                {
                    Connection = Connection,
                    Settings = Settings.ForModule(m.Id),
                    Audio = Audio,
                    Player = Player,
                    LoggerFactory = LoggerFactory,
                    AppVersion = Version,
                }, CancellationToken.None);
            }
            catch (Exception ex) { _log.LogError(ex, "módulo {Id} no arrancó", m.Id); }
        }
    }

    public async Task<LinkResult> LinkAsync(string code, CancellationToken ct)
    {
        var name = Environment.MachineName;
        var r = await Link.ClaimAsync(code, name, Version, ct);
        if (r.Success && r.Token != null)
        {
            Secrets.Write(r.Token);
            await StartAsync();
        }
        return r;
    }

    public async Task UnlinkAsync()
    {
        foreach (var m in Modules) { try { await m.StopAsync(); } catch { } }
        _modulesStarted = false;
        await Connection.StopAsync();
        Secrets.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in Modules) { try { await m.StopAsync(); } catch { } }
        await Connection.StopAsync();
        try { await Settings.SaveAsync(); } catch { }
    }
}
