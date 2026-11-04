namespace Reserve.Api;

public sealed class SnapshotRefreshService(
    SnapshotStore store, ReserveOptions options, ILogger<SnapshotRefreshService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Fail startup if no valid initial snapshot is available.
        await store.RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await store.RefreshAsync(stoppingToken);
                    logger.LogInformation("Published reserve snapshot {SnapshotId}", store.Current.Root.SnapshotId);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogError(error, "Reserve refresh failed; retaining snapshot {SnapshotId}", store.Current.Root.SnapshotId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
