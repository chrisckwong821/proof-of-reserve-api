namespace Reserve.Api;

// Loads the first snapshot at startup, refreshes daily at RefreshTimeUtc, and retries a failed refresh with backoff.
public sealed class RefreshScheduler(
    ReserveCache cache,
    ReserveOptions options,
    ILogger<RefreshScheduler> logger) : BackgroundService
{
    // Load the first snapshot before the server starts accepting requests; a failure stops startup.
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await cache.RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset scheduled = options.GetNextRefreshUtc(now);
                logger.LogInformation("Next reserve refresh scheduled at {NextRefreshUtc:u}", scheduled);
                await DelayUntilAsync(scheduled, stoppingToken);
                await RefreshWithRetriesAsync(scheduled.AddDays(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    // Task.Delay can wake slightly before the wall clock reaches the target. Wait out the remainder so the refresh never
    // runs (and the snapshot is never stamped) before its scheduled time, and the next loop moves on to the next day.
    private static async Task DelayUntilAsync(DateTimeOffset target, CancellationToken cancellationToken)
    {
        TimeSpan remaining;
        while ((remaining = target - DateTimeOffset.UtcNow) > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    // Retry until a refresh succeeds or the next scheduled refresh takes over; the old snapshot keeps serving meanwhile.
    private async Task RefreshWithRetriesAsync(DateTimeOffset nextScheduled, CancellationToken stoppingToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await cache.RefreshAsync(stoppingToken);
                if (attempt > 1)
                {
                    logger.LogInformation("Refresh succeeded on attempt {Attempt}", attempt);
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                string snapshotId = cache.Current.Root.SnapshotId;
                TimeSpan? delay = GetRetryDelay(options, attempt, DateTimeOffset.UtcNow, nextScheduled);
                if (delay is null)
                {
                    logger.LogError(
                        error,
                        "Refresh failed; keeping snapshot {SnapshotId} (attempt {Attempt}). " +
                        "No more retries before the next scheduled refresh at {NextRefreshUtc:u}",
                        snapshotId, attempt, nextScheduled);
                    return;
                }

                logger.LogError(
                    error,
                    "Refresh failed; keeping snapshot {SnapshotId} (attempt {Attempt}). Retrying in {RetryDelay}",
                    snapshotId, attempt, delay.Value);
                await Task.Delay(delay.Value, stoppingToken);
            }
        }
    }

    // Delay before the next attempt after failedAttempts failures: RefreshRetryDelay doubling up to
    // RefreshRetryMaxDelay. Null means stop: retries are off, or the next scheduled refresh would come first.
    public static TimeSpan? GetRetryDelay(
        ReserveOptions options, int failedAttempts, DateTimeOffset now, DateTimeOffset nextScheduled)
    {
        if (options.RefreshRetryDelay <= TimeSpan.Zero)
        {
            return null;
        }

        double scaled = options.RefreshRetryDelay.Ticks * Math.Pow(2, Math.Clamp(failedAttempts - 1, 0, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(scaled, options.RefreshRetryMaxDelay.Ticks));
        return now + delay < nextScheduled ? delay : null;
    }
}
