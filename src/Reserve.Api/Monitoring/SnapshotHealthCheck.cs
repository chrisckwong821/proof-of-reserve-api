using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Reserve.Api;

// Healthy while the published snapshot is from the latest scheduled refresh (or that refresh is within its grace period).
public sealed class SnapshotHealthCheck(ReserveCache cache, ReserveOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = cache.CurrentOrDefault;
        if (snapshot is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("No reserve snapshot has been loaded."));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var (stale, description) = Evaluate(snapshot.Root, options, now);
        var data = new Dictionary<string, object>
        {
            ["snapshotId"] = snapshot.Root.SnapshotId,
            ["dataVersion"] = snapshot.Root.DataVersion,
            ["userCount"] = snapshot.Root.UserCount,
            ["generatedAtUtc"] = snapshot.Root.GeneratedAtUtc,
            ["nextRefreshUtc"] = options.GetNextRefreshUtc(now)
        };

        return Task.FromResult(stale
            ? HealthCheckResult.Unhealthy(description, data: data)
            : HealthCheckResult.Healthy(description, data));
    }

    // Stale means the snapshot predates the most recent scheduled refresh and the grace period has passed.
    public static (bool Stale, string Description) Evaluate(RootResponse root, ReserveOptions options, DateTimeOffset now)
    {
        DateTimeOffset lastScheduled = options.GetNextRefreshUtc(now).AddDays(-1);
        if (root.GeneratedAtUtc >= lastScheduled)
        {
            return (false, "Snapshot is current.");
        }

        if (now < lastScheduled + options.StaleGracePeriod)
        {
            return (false, "Scheduled refresh is due and still within its grace period.");
        }

        return (true, $"Snapshot predates the {lastScheduled:u} scheduled refresh; that refresh failed or is still running.");
    }

    // The /health response body: overall status plus each check's description and details.
    public static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    data = entry.Value.Data
                })
        });
    }
}
