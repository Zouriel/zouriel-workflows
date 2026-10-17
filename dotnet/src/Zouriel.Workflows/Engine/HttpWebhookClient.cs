using System.Net;
using System.Net.Sockets;

namespace Zouriel.Workflows.Engine;

/// <summary>Limits for <see cref="HttpWebhookClient"/>.</summary>
public sealed class HttpWebhookClientOptions
{
    /// <summary>
    /// Allow calls to loopback, private, link-local, unique-local, multicast and unspecified addresses.
    /// Off by default: a webhook to an allowed origin must not reach the server's own network, even when
    /// its DNS name later resolves to such an address (the check runs on the address actually connected).
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>The largest response body read; larger responses fail the call. Default 1 MB.</summary>
    public long MaxResponseBytes { get; set; } = 1024 * 1024;

    /// <summary>How long connecting may take. Default 10 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Sends webhooks with <see cref="HttpClient"/>. Redirects are never followed (a 3xx answer is returned as
/// the status, so it cannot bounce the call to another host), responses are capped in size, and by
/// default the connection is refused when the resolved address is in a private or local network.
/// </summary>
public sealed class HttpWebhookClient : IWebhookClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpWebhookClientOptions _options;
    private readonly bool _ownsClient;

    /// <summary>A client with the default limits (no redirects, 1 MB responses, no private networks).</summary>
    public HttpWebhookClient() : this(new HttpWebhookClientOptions())
    {
    }

    /// <summary>A client with its own guarded connection handler.</summary>
    public HttpWebhookClient(HttpWebhookClientOptions options)
    {
        _options = options;
        _http = new HttpClient(CreateHandler(options)) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = true;
    }

    /// <summary>
    /// Uses a caller-supplied <see cref="HttpClient"/>. Only the response size cap applies: redirects and
    /// the private-network guard depend on how that client's handler is configured.
    /// </summary>
    public HttpWebhookClient(HttpClient http, HttpWebhookClientOptions? options = null)
    {
        _http = http;
        _options = options ?? new HttpWebhookClientOptions();
    }

    /// <summary>
    /// The guarded handler: no redirects, no proxy (a proxy would make the connection, so the private-network check
    /// would only see the proxy's address), and connections only to addresses the options allow.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(HttpWebhookClientOptions options) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = options.ConnectTimeout,
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            var allowed = options.AllowPrivateNetworks ? addresses : addresses.Where(a => !IsPrivate(a)).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException($"Webhooks may not call '{host}': it resolves to a private or local network address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>
    /// True for loopback, private, link-local, carrier-grade NAT, unique-local, multicast, reserved and unspecified
    /// addresses, and for IPv6 forms that carry an IPv4 address (mapped, IPv4-compatible, NAT64 <c>64:ff9b::/96</c> and
    /// <c>64:ff9b:1::/48</c>, 6to4 <c>2002::/16</c>) when that IPv4 address is one of them. Teredo (<c>2001::/32</c>)
    /// tunnels are refused outright: where they lead cannot be told from the address.
    /// </summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6 && EmbeddedIPv4(a) is { } inner) return IsPrivate(inner);
        if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.GetAddressBytes() is [0x20, 0x01, 0x00, 0x00, ..]) return true;
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => true,
                100 => b[1] is >= 64 and <= 127,
                169 => b[1] == 254,
                172 => b[1] is >= 16 and <= 31,
                192 => (b[1] == 168) || (b[1] == 0 && b[2] is 0 or 2),
                198 => b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100),
                203 => b[1] == 0 && b[2] == 113,
                >= 224 => true,
                _ => false,
            };
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (a.Equals(IPAddress.IPv6None) || a.Equals(IPAddress.IPv6Any)) return true;
            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.IsIPv6UniqueLocal) return true;
            var b = a.GetAddressBytes();
            // 2001:db8::/32 is the documentation range: never a real webhook target.
            return b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8;
        }
        return true;
    }

    /// <summary>The IPv4 address an IPv6 address carries (IPv4-compatible, IPv4-translated, NAT64, 6to4), or null.</summary>
    private static IPAddress? EmbeddedIPv4(IPAddress a)
    {
        var b = a.GetAddressBytes();
        // ::a.b.c.d (IPv4-compatible, not :: or ::1) and ::ffff:0:a.b.c.d (IPv4-translated).
        if (b.Take(10).All(x => x == 0) && ((b[10] == 0 && b[11] == 0 && (b[12] | b[13] | b[14]) != 0) || (b[10] == 0xff && b[11] == 0xff)))
            return new IPAddress(b[12..16]);
        if (b.Take(8).All(x => x == 0) && b[8] == 0xff && b[9] == 0xff && b[10] == 0 && b[11] == 0) return new IPAddress(b[12..16]);
        // 64:ff9b::/96 (well-known NAT64 prefix) and 64:ff9b:1::/48 (local-use NAT64, IPv4 in the last 32 bits).
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && (b.Skip(4).Take(8).All(x => x == 0) || (b[4] == 0 && b[5] == 1)))
            return new IPAddress(b[12..16]);
        // 2002:AABB:CCDD::/48 (6to4) carries A.B.C.D in bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b[2..6]);
        return null;
    }

    /// <summary>
    /// Whether a header name is an HTTP token and its value has no control characters (CR and LF above all, which
    /// would start another header). Webhook headers are rendered from records, so they are checked before sending.
    /// </summary>
    public static bool IsValidHeader(string name, string value) =>
        name.Length > 0 && name.All(c => c > ' ' && c < 127 && !"()<>@,;:\\\"/[]?={}".Contains(c))
        && value.All(c => c == '\t' || (c >= ' ' && c != (char)127));

    /// <inheritdoc />
    public async Task<WebhookResponse> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is not null && request.Method != "GET")
            msg.Content = new StringContent(request.Body, System.Text.Encoding.UTF8, "application/json");
        foreach (var (k, v) in request.Headers.Append(new("Idempotency-Key", request.IdempotencyKey)))
        {
            if (!IsValidHeader(k, v)) throw new ArgumentException($"The webhook header '{k}' has characters a header cannot hold.");
            // Checked above (the injection risk); values are passed as given so headers with free-form values keep
            // working. Content headers (Content-Type…) belong to the body; without a body they are left out.
            if (k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.Content is null) continue;
                msg.Content.Headers.Remove(k);
                msg.Content.Headers.TryAddWithoutValidation(k, v);
            }
            else msg.Headers.TryAddWithoutValidation(k, v);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Timeout is { } t) cts.CancelAfter(t);
        using var res = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return new WebhookResponse((int)res.StatusCode, await ReadCappedAsync(res.Content, cts.Token));
    }

    private async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        var max = _options.MaxResponseBytes;
        if (content.Headers.ContentLength > max) throw TooLarge();
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) throw TooLarge();
            buffer.Write(chunk, 0, read);
        }
        var charset = content.Headers.ContentType?.CharSet;
        var encoding = System.Text.Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Unknown charset: read as UTF-8.
            }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private HttpRequestException TooLarge() => new($"The webhook response is too large (over {_options.MaxResponseBytes} bytes).");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
