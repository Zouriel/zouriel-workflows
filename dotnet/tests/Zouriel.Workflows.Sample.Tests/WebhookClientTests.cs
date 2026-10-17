using System.Net;
using System.Net.Sockets;
using System.Text;
using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>The default webhook client: no redirects, capped responses, no private networks.</summary>
public sealed class WebhookClientTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _base = "";
    private Task _serve = Task.CompletedTask;

    public Task InitializeAsync()
    {
        var port = FreePort();
        _base = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
        _serve = Task.Run(ServeAsync);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            await _serve;
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            var res = ctx.Response;
            switch (ctx.Request.Url!.AbsolutePath)
            {
                case "/redirect":
                    res.StatusCode = 302;
                    res.RedirectLocation = _base + "/ok";
                    break;
                case "/big":
                    var big = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 10));
                    res.ContentLength64 = big.Length;
                    await res.OutputStream.WriteAsync(big);
                    break;
                default:
                    var ok = Encoding.UTF8.GetBytes("{\"ok\":true}");
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(ok);
                    break;
            }
            res.Close();
        }
    }

    private static WebhookRequest Get(string url) => new("GET", url, new Dictionary<string, string>(), null, "k1", TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Local_addresses_are_refused_by_default()
    {
        using var client = new HttpWebhookClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(Get(_base + "/ok")));
    }

    [Fact]
    public async Task Local_addresses_work_when_allowed()
    {
        using var client = new HttpWebhookClient(new HttpWebhookClientOptions { AllowPrivateNetworks = true });
        var res = await client.SendAsync(Get(_base + "/ok"));
        Assert.Equal(200, res.Status);
        Assert.Equal("{\"ok\":true}", res.Body);
    }

    [Fact]
    public async Task Redirects_are_returned_not_followed()
    {
        using var client = new HttpWebhookClient(new HttpWebhookClientOptions { AllowPrivateNetworks = true });
        var res = await client.SendAsync(Get(_base + "/redirect"));
        Assert.Equal(302, res.Status);
    }

    [Fact]
    public async Task Responses_over_1_MB_fail()
    {
        using var client = new HttpWebhookClient(new HttpWebhookClientOptions { AllowPrivateNetworks = true });
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(Get(_base + "/big")));
        Assert.Contains("too large", e.Message);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Private_ranges_are_recognised(string ip, bool isPrivate) => Assert.Equal(isPrivate, HttpWebhookClient.IsPrivate(IPAddress.Parse(ip)));
}
