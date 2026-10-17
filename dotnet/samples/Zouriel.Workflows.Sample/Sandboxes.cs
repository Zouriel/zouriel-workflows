using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.Sample;

/// <summary>One visitor's private copy of the sample app: its own database, workflows and clock.</summary>
public sealed class Sandbox(string id, ServiceProvider services, OffsetTimeProvider clock, string dbPath) : IAsyncDisposable
{
    public string Id { get; } = id;
    public ServiceProvider Services { get; } = services;
    public OffsetTimeProvider Clock { get; } = clock;
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    public SemaphoreSlim Work { get; } = new(1, 1);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

/// <summary>
/// The public playground gives every browser session its own sandbox, created on first use and
/// removed after it has been idle for a while, so visitors never see or break each other's data.
/// </summary>
public sealed partial class SandboxManager(IConfiguration config, ILogger<SandboxManager> log) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Sandbox>>> _boxes = new();
    private readonly string _dir = config["Playground:Directory"] ?? Path.Combine(Path.GetTempPath(), "zouriel-workflow-playground");
    private readonly int _max = int.TryParse(config["Playground:MaxSandboxes"], out var m) ? m : 200;
    private readonly TimeSpan _idle = TimeSpan.FromMinutes(int.TryParse(config["Playground:IdleMinutes"], out var i) ? i : 120);

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    public static partial Regex ValidId();

    public int Count => _boxes.Count;

    public async Task<Sandbox> GetAsync(string id, CancellationToken ct)
    {
        if (!ValidId().IsMatch(id)) throw new ArgumentException("Bad sandbox id.");
        if (!_boxes.ContainsKey(id) && _boxes.Count >= _max) await EvictOldestAsync();
        var lazy = _boxes.GetOrAdd(id, key => new Lazy<Task<Sandbox>>(() => CreateAsync(key)));
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
        services.AddSampleApp(SampleSetup.FileDatabase(path), clock, runWorker: false);
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
        return Task.CompletedTask;
    }

    private async Task EvictOldestAsync()
    {
        var oldest = _boxes.Where(kv => kv.Value.IsValueCreated && kv.Value.Value.IsCompletedSuccessfully)
            .OrderBy(kv => kv.Value.Value.Result.LastUsed).Select(kv => kv.Key).FirstOrDefault();
        if (oldest is not null) await ResetAsync(oldest);
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
