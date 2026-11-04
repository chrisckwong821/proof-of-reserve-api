using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Reserve.Api;

// The HTTP surface: GET root, GET proof and /health, with ETag revalidation and per-request Debug logs.
public static partial class ReserveEndpoints
{
    // Per-request Debug lines go to this category; enable with --Logging:LogLevel:Reserve.Api.Requests=Debug.
    private const string RequestLogCategory = "Reserve.Api.Requests";

    public static WebApplication MapReserveEndpoints(this WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(RequestLogCategory);

        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthJson });
        app.MapGet("/api/reserves/root",
            (HttpContext context, ReserveCache cache, RequestStats stats) =>
                GetRoot(context, cache, stats, log));
        // userId is a string so a non-numeric ID gets a JSON 400 instead of the router's empty 404.
        app.MapGet("/api/reserves/users/{userId}/proof",
            (string userId, string? snapshotId, HttpContext context, ReserveCache cache, RequestStats stats) =>
                GetProof(userId, snapshotId, context, cache, stats, log));

        return app;
    }

    // The root JSON is built once per snapshot, so every request sends the same bytes (or 304).
    private static IResult GetRoot(HttpContext context, ReserveCache cache, RequestStats stats, ILogger log)
    {
        var snapshot = cache.Current;
        string snapshotId = snapshot.Root.SnapshotId;

        bool notModified = SetCacheHeaders(context, snapshotId);
        stats.RecordRoot(notModified);
        LogRoot(log, notModified ? 304 : 200, snapshotId);
        return CachedJson(notModified, snapshot.RootJson);
    }

    // Reject invalid or outdated requests, then serve the proof from the cache or build it.
    private static IResult GetProof(
        string rawUserId, string? pinnedSnapshotId, HttpContext context, ReserveCache cache, RequestStats stats,
        ILogger log)
    {
        // Same parsing the old {userId:int} route constraint used, so every ID accepted before is accepted now.
        if (!int.TryParse(rawUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId))
        {
            stats.RecordProofRejected();
            LogInvalidUserId(log, rawUserId);
            return Results.Json(
                new { error = "User ID must be a positive integer." }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (userId <= 0)
        {
            stats.RecordProofRejected();
            return Reject(log, userId, StatusCodes.Status400BadRequest, "user ID must be positive",
                new { error = "User ID must be positive." });
        }

        var snapshot = cache.Current;
        string snapshotId = snapshot.Root.SnapshotId;
        if (pinnedSnapshotId is not null && pinnedSnapshotId != snapshotId)
        {
            stats.RecordProofRejected();
            return Reject(log, userId, StatusCodes.Status409Conflict, "pinned snapshot was replaced",
                new { error = "Snapshot changed; fetch the current root and retry.", snapshotId });
        }

        byte[]? proof = cache.GetProofJson(snapshot, userId, out bool fromCache);
        if (proof is null)
        {
            stats.RecordProofNotFound();
            return Reject(log, userId, StatusCodes.Status404NotFound, "user not found",
                new { error = "User not found." });
        }

        bool notModified = SetCacheHeaders(context, $"{snapshotId}-{userId}");
        stats.RecordProof(fromCache, notModified);
        LogProof(log, userId, notModified ? 304 : 200, fromCache ? "from cache" : "newly built", snapshotId);
        return CachedJson(notModified, proof);
    }

    // Log a rejected proof request and return its JSON error body.
    private static IResult Reject(ILogger log, int userId, int statusCode, string reason, object body)
    {
        LogProofRejected(log, userId, statusCode, reason);
        return Results.Json(body, statusCode: statusCode);
    }

    // 304 with no body when the client already has this version; otherwise the stored JSON bytes.
    private static IResult CachedJson(bool notModified, byte[] json) => notModified
        ? Results.StatusCode(StatusCodes.Status304NotModified)
        : Results.Bytes(json, "application/json");

    // Set the ETag and Cache-Control headers; returns true when If-None-Match already has this version.
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "GET proof for user {UserId}: 400, user ID is not an integer")]
    private static partial void LogInvalidUserId(ILogger logger, string userId);
}
