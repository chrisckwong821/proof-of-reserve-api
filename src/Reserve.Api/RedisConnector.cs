using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace Reserve.Api;

public sealed record RedisManifest(string Version, string Key, int Count);

public sealed class RedisConnector : IDisposable, IAsyncDisposable
{
    // Upper bound on waiting for an unfinished connect during shutdown.
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly ReserveOptions options;
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;

    // Create one lazy connection that is shared by all snapshot loads.
    public RedisConnector(ReserveOptions options)
    {
        this.options = options;
        connection = new(() => ConnectionMultiplexer.ConnectAsync(options.RedisConnection));
    }

    // Read only the published manifest's version: one small GET, used to detect new data without loading it.
    public async Task<string> GetManifestVersionAsync(CancellationToken cancellationToken)
    {
        var db = (await connection.Value.WaitAsync(cancellationToken)).GetDatabase();
        return (await ReadManifestAsync(db, cancellationToken)).Version;
    }

    // Read the published manifest and its immutable user hash, then validate the record count.
    public async Task<UserDataSet> LoadAsync(CancellationToken cancellationToken)
    {
        var multiplexer = await connection.Value.WaitAsync(cancellationToken);
        var db = multiplexer.GetDatabase();
        var manifest = await ReadManifestAsync(db, cancellationToken);

        // The manifest pins an immutable hash. Never SCAN a hash being updated in place.
        // SCAN can repeat fields; deduplicate identical entries before checking the count.
        var users = new Dictionary<int, UserAccount>();
        await foreach (var entry in db.HashScanAsync(manifest.Key, pageSize: 1000)
            .WithCancellation(cancellationToken))
        {
            if (!int.TryParse(entry.Name.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                !long.TryParse(
                    entry.Value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long balance))
            {
                throw new InvalidDataException("Redis user IDs and balances must be nonnegative decimal integers.");
            }

            var user = new UserAccount(id, balance);
            if (users.TryGetValue(id, out var existing) && existing != user)
            {
                throw new InvalidDataException("Redis snapshot changed during loading.");
            }

            users[id] = user;
        }

        if (users.Count != manifest.Count)
        {
            throw new InvalidDataException("Redis snapshot count does not match its manifest.");
        }

        return new UserDataSet(manifest.Version, users.Values.ToArray());
    }

    // Read and validate the manifest that points at the current immutable user hash.
    private async Task<RedisManifest> ReadManifestAsync(IDatabase db, CancellationToken cancellationToken)
    {
        string? json = await db.StringGetAsync(options.RedisManifestKey).WaitAsync(cancellationToken);
        if (json is null)
        {
            throw new InvalidDataException("Redis has no published reserve manifest.");
        }

        var manifest = JsonSerializer.Deserialize<RedisManifest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid Redis reserve manifest.");

        if (string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.Count < 0 ||
            string.IsNullOrEmpty(manifest.Key) ||
            !manifest.Key.StartsWith(options.RedisUserKeyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid Redis reserve manifest fields.");
        }

        return manifest;
    }

    // Used by the host at shutdown: close the connection and wait for it, with a bounded wait for an unfinished connect.
    public async ValueTask DisposeAsync()
    {
        if (!connection.IsValueCreated)
        {
            return;
        }

        ConnectionMultiplexer multiplexer;
        try
        {
            multiplexer = await connection.Value.WaitAsync(DisposeTimeout);
        }
        catch (TimeoutException)
        {
            DisposeWhenConnected();
            return;
        }
        catch (Exception)
        {
            return; // The connect failed, so there is nothing to close.
        }

        await multiplexer.CloseAsync(allowCommandsToComplete: true);
        await multiplexer.DisposeAsync();
    }

    // Close now if connected; otherwise close the connection once its connect finishes.
    public void Dispose()
    {
        if (!connection.IsValueCreated)
        {
            return;
        }

        if (connection.Value.IsCompletedSuccessfully)
        {
            connection.Value.Result.Dispose();
        }
        else
        {
            DisposeWhenConnected();
        }
    }

    private void DisposeWhenConnected() => _ = connection.Value.ContinueWith(
        task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                task.Result.Dispose();
            }
        },
        TaskScheduler.Default);
}
