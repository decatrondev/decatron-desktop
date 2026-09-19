using System.Text.Json.Nodes;

namespace Decatron.Desktop.Sdk;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>
/// La única conexión al backend (wss://decatron.net/api/desktop/ws), multiplexada por
/// canal. Texto JSON con <c>ch</c>/<c>type</c>; binario con primer byte de canal.
/// </summary>
public interface IDesktopConnection
{
    ConnectionState State { get; }
    event Action<ConnectionState>? StateChanged;

    /// <summary>Login del canal vinculado, conocido tras el <c>core/hello</c>.</summary>
    string? Login { get; }

    /// <summary>Config de módulos que mandó el servidor en <c>core/hello</c> (por Id de módulo).</summary>
    IReadOnlyDictionary<string, JsonNode?> Modules { get; }
    event Action? HelloReceived;
    /// <summary>El servidor volvió a describir un módulo (nombre del canal); <see cref="Modules"/> ya está actualizado.</summary>
    event Action<string>? ModuleUpdated;

    /// <summary>Suscribe a los mensajes de texto de un canal. Devuelve el IDisposable que desuscribe.</summary>
    IDisposable Subscribe(string channel, Action<string, JsonNode> handler);

    Task SendAsync(string channel, string type, object? payload = null, CancellationToken ct = default);

    /// <summary>Frame binario: el canal va en el primer byte, la carga después.</summary>
    ValueTask SendBinaryAsync(byte channelId, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>Latencia del último ping, o null si no hubo.</summary>
    TimeSpan? Latency { get; }
}
