using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.Sample;

/// <summary>One visitor's private copy of the sample app: its own database, workflows and clock.</summary>
public sealed class Sandbox(string id, ServiceProvider services, OffsetTimeProvider clock, string dbPath) : IAsyncDisposable
{
    /// <summary>The session id it belongs to.</summary>
    public string Id { get; } = id;
    /// <summary>Its own DI container (database, engine, inbox).</summary>
    public ServiceProvider Services { get; } = services;
    /// <summary>Its clock, which the visitor can move forward.</summary>
    public OffsetTimeProvider Clock { get; } = clock;
    /// <summary>When a request last used it (idle sandboxes are removed).</summary>
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Held while its background work runs, so two runs never overlap.</summary>
    public SemaphoreSlim Work { get; } = new(1, 1);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

/// <summary>Raised when every sandbox slot is taken by sandboxes still being created.</summary>
public sealed class PlaygroundFullException() : Exception("The playground is full right now. Try again in a minute.");

/// <summary>
/// The public playground gives every browser session its own sandbox, created on first use and
/// removed after it has been idle for a while, so visitors never see or break each other's data.
/// Session ids are issued by the server (<see cref="IssueSessionId"/>) and signed, so a visitor cannot
/// pick, guess or take over someone else's sandbox.
/// </summary>
public sealed class SandboxManager(IConfiguration config, ILogger<SandboxManager> log) : BackgroundService
{
    private const int IdBytes = 16;
    private readonly ConcurrentDictionary<string, Lazy<Task<Sandbox>>> _boxes = new();
    private readonly object _admit = new();
    private readonly string _dir = config["Playground:Directory"] ?? Path.Combine(Path.GetTempPath(), "zouriel-workflow-playground");
    private readonly int _max = int.TryParse(config["Playground:MaxSandboxes"], out var m) ? m : 200;
    private readonly TimeSpan _idle = TimeSpan.FromMinutes(int.TryParse(config["Playground:IdleMinutes"], out var i) ? i : 120);
    // Signs issued ids. Set Playground:Secret to keep sessions valid across restarts and instances.
    private readonly byte[] _secret = config["Playground:Secret"] is { Length: >= 16 } s ? Encoding.UTF8.GetBytes(s) : RandomNumberGenerator.GetBytes(32);

    /// <summary>How many sandboxes exist (or are being created).</summary>
    public int Count => _boxes.Count;

    /// <summary>A new session id: 128 random bits plus a signature, both base64url.</summary>
    public string IssueSessionId()
    {
        var random = RandomNumberGenerator.GetBytes(IdBytes);
        return $"{Base64Url(random)}.{Base64Url(Sign(random))}";
    }

    /// <summary>True when <paramref name="id"/> was issued by this server (its signature checks out).</summary>
    public bool IsIssued(string? id)
    {
        if (id is null || id.Length > 64) return false;
        var dot = id.IndexOf('.');
        if (dot <= 0) return false;
        try
        {
            var random = FromBase64Url(id[..dot]);
            var signature = FromBase64Url(id[(dot + 1)..]);
            return random.Length == IdBytes && CryptographicOperations.FixedTimeEquals(signature, Sign(random));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private byte[] Sign(byte[] random) => HMACSHA256.HashData(_secret, random)[..IdBytes];

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
    }

    /// <summary>The sandbox of an issued session id, created on first use (evicting the least recently used when full).</summary>
    public async Task<Sandbox> GetAsync(string id, CancellationToken ct)
    {
        if (!IsIssued(id)) throw new ArgumentException("This session id was not issued by the playground.");
        if (!_boxes.TryGetValue(id, out var lazy))
        {
            // Admission is serialised so the cap holds when many new sessions arrive at once.
            lock (_admit)
            {
                if (!_boxes.TryGetValue(id, out lazy))
                {
                    while (_boxes.Count >= _max)
                        if (!EvictOldest()) throw new PlaygroundFullException();
                    lazy = new Lazy<Task<Sandbox>>(() => CreateAsync(id));
                    _boxes[id] = lazy;
                }
            }
        }
        Sandbox box;
        try
        {
            box = await lazy.Value.WaitAsync(ct);
        }
        catch
        {
            _boxes.TryRemove(id, out _);
            throw;
        }
        box.LastUsed = DateTimeOffset.UtcNow;
        return box;
    }

    private async Task<Sandbox> CreateAsync(string id)
    {
        Directory.CreateDirectory(_dir);
        // A fresh file per sandbox, so a reset never meets the previous sandbox's data.
        var path = Path.Combine(_dir, $"{id}-{Guid.NewGuid():N}.db");
        var clock = new OffsetTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        // Webhooks never leave the sandbox: a visitor could otherwise make the server call any address.
        services.AddSampleApp(SampleSetup.FileDatabase(path), clock, runWorker: false, webhooks: new PlaygroundWebhooks());
        // Requests run with the sandbox's services; the playground endpoints still need the manager.
        services.AddSingleton(this);
        var provider = services.BuildServiceProvider();
        await SampleSetup.SeedAsync(provider);
        log.LogInformation("Created playground sandbox {Id} ({Count} active)", id, _boxes.Count);
        return new Sandbox(id, provider, clock, path);
    }

    /// <summary>Forgets a sandbox now and disposes it a little later, so requests still using it can finish.</summary>
    public Task ResetAsync(string id)
    {
        Forget(id);
        return Task.CompletedTask;
    }

    private void Forget(string id)
    {
        if (_boxes.TryRemove(id, out var lazy) && lazy.IsValueCreated)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                try
                {
                    await (await lazy.Value).DisposeAsync();
                }
                catch (Exception e)
                {
                    log.LogWarning(e, "Disposing sandbox {Id} failed", id);
                }
            });
        }
    }

    /// <summary>Removes the least recently used ready sandbox. False when none is ready to remove.</summary>
    private bool EvictOldest()
    {
        var oldest = _boxes.Where(kv => kv.Value.IsValueCreated && kv.Value.Value.IsCompletedSuccessfully)
            .OrderBy(kv => kv.Value.Value.Result.LastUsed).Select(kv => kv.Key).FirstOrDefault();
        if (oldest is null) return false;
        Forget(oldest);
        return true;
    }

    /// <summary>Runs each sandbox's background work (what <c>WorkflowWorker</c> does in a normal app) and removes idle ones.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Directory.Exists(_dir))
            foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (id, lazy) in _boxes.ToArray())
            {
                if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully) continue;
                var box = lazy.Value.Result;
                if (DateTimeOffset.UtcNow - box.LastUsed > _idle)
                {
                    await ResetAsync(id);
                    continue;
                }
                await RunBackgroundAsync(box, stoppingToken);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs a sandbox's outbox and timers until nothing is left (at most 20 rounds).</summary>
    public async Task RunBackgroundAsync(Sandbox box, CancellationToken ct)
    {
        if (!await box.Work.WaitAsync(0, ct)) return;
        try
        {
            var engine = box.Services.GetRequiredService<WorkflowEngine>();
            for (var n = 0; n < 20; n++)
            {
                await engine.RefreshOwnershipAsync(ct);
                if (await engine.ProcessOutboxAsync(100, ct) + await engine.ProcessTimersAsync(100, ct) == 0) break;
            }
            await engine.FlushBackgroundAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Background work failed in sandbox {Id}", box.Id);
        }
        finally
        {
            box.Work.Release();
        }
    }
}

/// <summary>
/// The playground's webhook client: it records what would have been sent and answers 200 with
/// <c>{"playground":true}</c>, without any network call.
/// </summary>
public sealed class PlaygroundWebhooks : IWebhookClient
{
    private readonly ConcurrentQueue<WebhookRequest> _calls = new();

    /// <summary>The most recent calls (at most 100).</summary>
    public IReadOnlyList<WebhookRequest> Calls => _calls.ToList();

    /// <inheritdoc />
    public Task<WebhookResponse> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        _calls.Enqueue(request);
        while (_calls.Count > 100 && _calls.TryDequeue(out _)) { }
        return Task.FromResult(new WebhookResponse(200, "{\"playground\":true}"));
    }
}
