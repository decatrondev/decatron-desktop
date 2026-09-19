using Decatron.Desktop.Core.Linking;
using Xunit;

namespace Decatron.Desktop.Tests;

public class LinkServiceTests
{
    [Fact]
    public async Task Codigo_valido_devuelve_token()
    {
        await using var server = new FakeDesktopServer();
        var svc = new LinkService(new HttpClient(), server.HttpBase);
        var r = await svc.ClaimAsync("abcd-2345", "PC", "0.1.0", CancellationToken.None);
        Assert.True(r.Success);
        Assert.Equal(server.ValidToken, r.Token);
        Assert.Equal(7, r.DeviceId);
    }

    [Fact]
    public async Task Codigo_invalido_devuelve_mensaje()
    {
        await using var server = new FakeDesktopServer();
        var svc = new LinkService(new HttpClient(), server.HttpBase);
        var r = await svc.ClaimAsync("ZZZZ-9999", "PC", "0.1.0", CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("inválido", r.Message);
    }
}
