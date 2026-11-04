using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Reserve.Api;

// The HTTP surface: GET root, GET proof and /health, with ETag revalidation and per-request Debug logs.
public static partial class ReserveEndpoints
{
    public static WebApplication MapReserveEndpoints(this WebApplication app)
    {
        // Per-request lines are Debug so they stay off unless enabled; the heartbeat carries the counts.
        var requestLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Reserve.Api.Requests");

        // Report whether the cached snapshot is current; 503 when the last scheduled refresh was missed.
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthJson });

        // Return the root from one complete, currently published snapshot.
        app.MapGet("/api/reserves/root", (HttpContext context, ReserveCache cache, RequestStats stats) =>
        {
            var snapshot = cache.Current;
            bool notModified = SetCacheHeaders(context, snapshot.Root.SnapshotId);
            stats.RecordRoot(notModified);
            LogRoot(requestLog, notModified ? 304 : 200, snapshot.Root.SnapshotId);
            return notModified
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(snapshot.RootJson, "application/json");
        });

        // Return a user proof, rejecting requests pinned to a snapshot that has been replaced.
        app.MapGet("/api/reserves/users/{userId:int}/proof",
            (int userId, string? snapshotId, HttpContext context, ReserveCache cache, RequestStats stats) =>
            {
                if (userId <= 0)
                {
                    stats.RecordProofRejected();
                    LogProofRejected(requestLog, userId, 400, "user ID must be positive");
                    return Results.BadRequest(new
                    {
                        error = "User ID must be positive."
                    });
                }

                var snapshot = cache.Current;
                if (snapshotId is not null && snapshotId != snapshot.Root.SnapshotId)
                {
                    stats.RecordProofRejected();
                    LogProofRejected(requestLog, userId, 409, "pinned snapshot was replaced");
                    return Results.Conflict(new
                    {
                        error = "Snapshot changed; fetch the current root and retry.",
                        snapshotId = snapshot.Root.SnapshotId
                    });
                }

                byte[]? proof = cache.GetProofJson(snapshot, userId, out bool fromCache);
                if (proof is null)
                {
                    stats.RecordProofNotFound();
                    LogProofRejected(requestLog, userId, 404, "user not found");
                    return Results.NotFound(new
                    {
                        error = "User not found."
                    });
                }

                bool notModified = SetCacheHeaders(context, $"{snapshot.Root.SnapshotId}-{userId}");
                stats.RecordProof(fromCache, notModified);
                LogProof(
                    requestLog, userId, notModified ? 304 : 200, fromCache ? "from cache" : "newly built",
                    snapshot.Root.SnapshotId);
                return notModified
                    ? Results.StatusCode(StatusCodes.Status304NotModified)
                    : Results.Bytes(proof, "application/json");
            });

        return app;
    }

    // Set the snapshot ETag and caching headers; true when If-None-Match already has this version (HTTP 304).
    private static bool SetCacheHeaders(HttpContext context, string version)
    {
        string etag = $"\"{version}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "private, no-cache";

        return context.Request.Headers.IfNoneMatch.ToString().Split(',')
            .Select(value => value.Trim())
            .Any(value => value == etag || value == "W/" + etag || value == "*");
    }

    // Write the health result as JSON: overall status plus each check's description and details.
    private static Task WriteHealthJson(HttpContext context, HealthReport report)
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "GET root: {StatusCode}, snapshot {SnapshotId}")]
    private static partial void LogRoot(ILogger logger, int statusCode, string snapshotId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "GET proof for user {UserId}: {StatusCode}, proof {ProofSource}, snapshot {SnapshotId}")]
    private static partial void LogProof(
        ILogger logger, int userId, int statusCode, string proofSource, string snapshotId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "GET proof for user {UserId}: {StatusCode}, {Reason}")]
    private static partial void LogProofRejected(ILogger logger, int userId, int statusCode, string reason);
}
