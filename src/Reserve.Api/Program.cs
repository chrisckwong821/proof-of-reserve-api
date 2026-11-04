using Reserve.Api;

await ReserveApplication.Build(args).RunAsync();

namespace Reserve.Api
{
    public static class ReserveApplication
    {
        // Configure the Redis-backed cache and two endpoints; tests can supply an in-memory loader.
        public static WebApplication Build(
            string[] args,
            Func<CancellationToken, Task<UserDataSet>>? loadUsers = null)
        {
            // Resolve appsettings beside the executable, regardless of the working directory.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });
            var options = builder.Configuration.GetRequiredSection("Reserve").Get<ReserveOptions>()
                ?? throw new InvalidOperationException("Reserve configuration is required.");
            options.Validate();

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<RedisConnector>();
            builder.Services.AddSingleton(services =>
            {
                Func<CancellationToken, Task<UserDataSet>> load = loadUsers
                    ?? services.GetRequiredService<RedisConnector>().LoadAsync;
                return new ReserveCache(
                    options,
                    services.GetRequiredService<ILogger<ReserveCache>>(),
                    load);
            });
            builder.Services.AddHostedService(services => services.GetRequiredService<ReserveCache>());

            var app = builder.Build();

            // Return the root from one complete, currently published snapshot.
            app.MapGet("/api/reserves/root", (HttpContext context, ReserveCache cache) =>
            {
                var snapshot = cache.Current;
                return CachedJson(context, snapshot.RootJson, snapshot.Root.SnapshotId);
            });

            // Return a user proof, rejecting requests pinned to a snapshot that has been replaced.
            app.MapGet("/api/reserves/users/{userId:int}/proof",
                (int userId, string? snapshotId, HttpContext context, ReserveCache cache) =>
                {
                    if (userId <= 0)
                    {
                        return Results.BadRequest(new
                        {
                            error = "User ID must be positive."
                        });
                    }

                    var snapshot = cache.Current;
                    if (snapshotId is not null && snapshotId != snapshot.Root.SnapshotId)
                    {
                        return Results.Conflict(new
                        {
                            error = "Snapshot changed; fetch the current root and retry.",
                            snapshotId = snapshot.Root.SnapshotId
                        });
                    }

                    byte[]? proof = cache.GetProofJson(snapshot, userId);
                    return proof is null
                        ? Results.NotFound(new
                        {
                            error = "User not found."
                        })
                        : CachedJson(context, proof, $"{snapshot.Root.SnapshotId}-{userId}");
                });

            return app;
        }

        // Revalidate the snapshot ETag and return either HTTP 304 or the cached JSON bytes.
        private static IResult CachedJson(HttpContext context, byte[] json, string version)
        {
            string etag = $"\"{version}\"";
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "private, no-cache";

            bool unchanged = context.Request.Headers.IfNoneMatch.ToString().Split(',')
                .Select(value => value.Trim())
                .Any(value => value == etag || value == "W/" + etag || value == "*");

            return unchanged
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(json, "application/json");
        }
    }
}
