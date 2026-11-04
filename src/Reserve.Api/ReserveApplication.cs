using Microsoft.Extensions.Options;

namespace Reserve.Api;

// Wires settings, services and background jobs together, then maps the endpoints.
public static class ReserveApplication
{
    // Configure the Redis-backed cache, refresh scheduler and endpoints; tests can supply an in-memory loader.
    // readVersion is the manifest-version check used by ManifestPoller; it defaults to Redis when loadUsers is not given.
    public static WebApplication Build(
        string[] args,
        Func<CancellationToken, Task<UserDataSet>>? loadUsers = null,
        Func<CancellationToken, Task<string>>? readVersion = null)
    {
        // Resolve appsettings beside the executable, regardless of the working directory.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Bind the "Reserve" section and reject invalid settings when the host starts, before any request.
        builder.Services.AddOptions<ReserveOptions>()
            .BindConfiguration("Reserve")
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ReserveOptions>, ReserveOptionsValidator>();
        // Components keep taking ReserveOptions directly; it is the validated options instance.
        builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<ReserveOptions>>().Value);

        builder.Services.AddSingleton<RedisConnector>();
        builder.Services.AddSingleton(services =>
        {
            Func<CancellationToken, Task<UserDataSet>> load = loadUsers
                ?? services.GetRequiredService<RedisConnector>().LoadAsync;
            return new ReserveCache(
                services.GetRequiredService<ReserveOptions>(),
                services.GetRequiredService<ILogger<ReserveCache>>(),
                load);
        });
        builder.Services.AddHostedService<RefreshScheduler>();
        builder.Services.AddHostedService(services => new ManifestPoller(
            services.GetRequiredService<ReserveCache>(),
            services.GetRequiredService<ReserveOptions>(),
            services.GetRequiredService<ILogger<ManifestPoller>>(),
            readVersion ?? (loadUsers is null
                ? services.GetRequiredService<RedisConnector>().GetManifestVersionAsync
                : null)));
        builder.Services.AddHealthChecks().AddCheck<SnapshotHealthCheck>("snapshot");

        var app = builder.Build();

        app.MapReserveEndpoints();

        return app;
    }
}
