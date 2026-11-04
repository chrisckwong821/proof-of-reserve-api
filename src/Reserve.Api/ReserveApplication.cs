namespace Reserve.Api;

public static class ReserveApplication
{
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = builder.Configuration.GetSection("Reserve").Get<ReserveOptions>() ?? new ReserveOptions();
        options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IUserRepository>(_ => options.Source == "Redis"
            ? new RedisUserRepository(options)
            : new FileUserRepository(Path.IsPathRooted(options.FilePath)
                ? options.FilePath : Path.Combine(AppContext.BaseDirectory, options.FilePath)));
        builder.Services.AddSingleton<SnapshotStore>();
        builder.Services.AddHostedService<SnapshotRefreshService>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapGet("/api/reserves/root", (HttpContext context, SnapshotStore store) =>
        {
            var snapshot = store.Current;
            return CachedJson(context, snapshot.RootJson, snapshot.Root.SnapshotId);
        });
        app.MapGet("/api/reserves/users/{userId:int}/proof",
            (int userId, string? snapshotId, HttpContext context, SnapshotStore store) =>
            {
                if (userId <= 0) return Results.BadRequest(new { error = "User ID must be positive." });
                var snapshot = store.Current;
                if (snapshotId is not null && snapshotId != snapshot.Root.SnapshotId)
                    return Results.Conflict(new { error = "Snapshot changed; fetch the current root and retry.", snapshotId = snapshot.Root.SnapshotId });
                byte[]? proof = store.GetProofJson(snapshot, userId);
                return proof is null
                    ? Results.NotFound(new { error = "User not found." })
                    : CachedJson(context, proof, $"{snapshot.Root.SnapshotId}-{userId}");
            });
        app.MapGet("/health/ready", (SnapshotStore store) =>
            Results.Ok(new { status = "ready", snapshotId = store.Current.Root.SnapshotId, store.Current.Root.GeneratedAtUtc }));
        return app;
    }

    private static IResult CachedJson(HttpContext context, byte[] json, string version)
    {
        string etag = $"\"{version}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "private, no-cache";
        bool unchanged = context.Request.Headers.IfNoneMatch.ToString().Split(',')
            .Select(value => value.Trim())
            .Any(value => value == etag || value == "W/" + etag || value == "*");
        return unchanged ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Bytes(json, "application/json");
    }
}
