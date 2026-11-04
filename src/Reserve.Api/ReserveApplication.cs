using Microsoft.Extensions.Options;

namespace Reserve.Api;

// Wires settings, services and background jobs together, then maps the endpoints.
public static class ReserveApplication
{
    // Production reads users from Redis; tests pass their own dataSource instead.
    public static WebApplication Build(string[] args, IUserDataSource? dataSource = null)
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

        if (dataSource is null)
        {
            builder.Services.AddSingleton<IUserDataSource, RedisConnector>();
        }
        else
        {
            builder.Services.AddSingleton(dataSource);
        }

        builder.Services.AddSingleton<ReserveCache>();
        builder.Services.AddHostedService<RefreshScheduler>();
        builder.Services.AddHostedService<ManifestPoller>();
        builder.Services.AddHealthChecks().AddCheck<SnapshotHealthCheck>("snapshot");

        var app = builder.Build();

        app.MapReserveEndpoints();

        return app;
    }
}
