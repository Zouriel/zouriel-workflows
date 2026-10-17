namespace Zouriel.Workflows.Engine;

/// <summary>Sends webhooks with <see cref="HttpClient"/>.</summary>
public sealed class HttpWebhookClient(HttpClient http) : IWebhookClient
{
    public async Task<WebhookResponse> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is not null && request.Method != "GET")
            msg.Content = new StringContent(request.Body, System.Text.Encoding.UTF8, "application/json");
        foreach (var (k, v) in request.Headers) msg.Headers.TryAddWithoutValidation(k, v);
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Timeout is { } t) cts.CancelAfter(t);
        using var res = await http.SendAsync(msg, cts.Token);
        var body = await res.Content.ReadAsStringAsync(cts.Token);
        return new WebhookResponse((int)res.StatusCode, body);
    }
}
