using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Sdk;

/// <summary>Lo que el shell le presta a cada módulo.</summary>
public sealed class ModuleContext
{
    public required IDesktopConnection Connection { get; init; }
    public required IModuleSettings Settings { get; init; }
    public required IAudioCaptureFactory Audio { get; init; }
    public required IAudioPlayer Player { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required string AppVersion { get; init; }
}

/// <summary>Ajustes propios del módulo, guardados por el shell bajo su Id.</summary>
public interface IModuleSettings
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
    Task SaveAsync();
}
