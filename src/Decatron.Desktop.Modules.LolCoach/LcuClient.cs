using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Decatron.Desktop.Modules.LolCoach;

/// <summary>
/// HTTP contra la API local del cliente de LoL (https://127.0.0.1:{port}, basic auth
/// riot:{password}, certificado autofirmado). Solo lectura: nada de POST/PATCH — es
/// la línea que Riot no quiere que se cruce (auto-aceptar, auto-lock, etc.).
/// </summary>
public sealed class LcuClient : IDisposable
{
    private readonly HttpClient _http;

    public LcuEndpoint Endpoint { get; }

    public LcuClient(LcuEndpoint endpoint)
    {
        Endpoint = endpoint;
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"{endpoint.Protocol}://127.0.0.1:{endpoint.Port}/"), Timeout = TimeSpan.FromSeconds(3) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + endpoint.Password)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>GET y parseo. Null si 404 (recurso no aplica en esta fase) o error de red; lanza solo si el cliente ya no responde (para redetectar).</summary>
    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path.TrimStart('/'), ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) return null;
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public void Dispose() => _http.Dispose();
}
