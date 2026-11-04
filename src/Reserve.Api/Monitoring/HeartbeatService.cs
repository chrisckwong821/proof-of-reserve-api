namespace Reserve.Api;

// Logs one line per HeartbeatInterval: snapshot age and health, plus request counts since the last heartbeat.
public sealed class HeartbeatService(
    ReserveOptions options,
    ReserveCache cache,
    RequestStats stats,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                WriteHeartbeat(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public void WriteHeartbeat(DateTimeOffset now)
    {
        var root = cache.Current.Root;
        var counts = stats.TakeAndReset();
        var (stale, _) = SnapshotHealthCheck.Evaluate(root, options, now);
        var age = TimeSpan.FromSeconds(Math.Floor((now - root.GeneratedAtUtc).TotalSeconds));

        logger.Log(
            stale ? LogLevel.Warning : LogLevel.Information,
            "Heartbeat: {Health}; snapshot {SnapshotId} ({UserCount} users) is {SnapshotAge} old, " +
            "next refresh {NextRefreshUtc:u}. Last {Interval}: root {Root} ({RootNotModified} not modified), " +
            "proofs {ProofFromCache} from cache and {ProofBuilt} newly built ({ProofNotModified} not modified), " +
            "{ProofNotFound} not found, {ProofRejected} rejected",
            stale ? "STALE" : "healthy",
            root.SnapshotId,
            root.UserCount,
            age,
            options.GetNextRefreshUtc(now),
            options.HeartbeatInterval,
            counts.Root,
            counts.RootNotModified,
            counts.ProofFromCache,
            counts.ProofBuilt,
            counts.ProofNotModified,
            counts.ProofNotFound,
            counts.ProofRejected);
    }
}
