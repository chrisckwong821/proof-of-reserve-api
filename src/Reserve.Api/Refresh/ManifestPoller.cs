namespace Reserve.Api;

// Optional: every ManifestPollInterval, read only the manifest version and refresh when it differs from the
// published snapshot, so data published during the day is served without waiting for the daily refresh.
public sealed class ManifestPoller(
    ReserveCache cache,
    ReserveOptions options,
    ILogger<ManifestPoller> logger,
    Func<CancellationToken, Task<string>>? readVersion) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off by default. A test that injects its own loader without a version reader also has nothing to poll.
        if (options.ManifestPollInterval <= TimeSpan.Zero || readVersion is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.ManifestPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PollOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            string published = await readVersion!(stoppingToken);
            string served = cache.Current.Root.DataVersion;
            if (published == served)
            {
                return;
            }

            logger.LogInformation(
                "Manifest version changed from {ServedVersion} to {PublishedVersion}; refreshing", served, published);
            await cache.RefreshAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The current snapshot keeps serving; the next poll tries again.
            logger.LogWarning(
                error, "Manifest poll failed; keeping snapshot {SnapshotId}. Next poll in {PollInterval}",
                cache.Current.Root.SnapshotId, options.ManifestPollInterval);
        }
    }
}
