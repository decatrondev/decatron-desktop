using System.Net.Http.Json;

namespace Decatron.Desktop.Core.Linking;

public sealed record LinkResult(bool Success, string? Token, long? DeviceId, string? WsUrl, string? Message);

/// <summary>Canjea el código que el streamer generó en el dashboard por el token de la app.</summary>
public sealed class LinkService
{
    private readonly HttpClient _http;
    private readonly Uri _apiBase;

    public LinkService(HttpClient http, Uri apiBase) { _http = http; _apiBase = apiBase; }

    public async Task<LinkResult> ClaimAsync(string code, string deviceName, string appVersion, CancellationToken ct)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        try
        {
            var res = await _http.PostAsJsonAsync(new Uri(_apiBase, "api/desktop/devices/claim"),
                new { code = code.Trim().ToUpperInvariant(), deviceName, appVersion, platform = os }, ct);
            var body = await res.Content.ReadFromJsonAsync<ClaimResponse>(cancellationToken: ct);
            if (!res.IsSuccessStatusCode || body is null || !body.Success)
                return new LinkResult(false, null, null, null, body?.Message ?? $"Error {(int)res.StatusCode}");
            return new LinkResult(true, body.Token, body.DeviceId, body.WsUrl, null);
        }
        catch (Exception ex)
        {
            return new LinkResult(false, null, null, null, "No se pudo conectar con decatron.net: " + ex.Message);
        }
    }

    private sealed record ClaimResponse(bool Success, string? Token, long? DeviceId, string? WsUrl, string? Message);
}
