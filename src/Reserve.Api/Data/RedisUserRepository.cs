using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace Reserve.Api;

public sealed record RedisManifest(string Version, string Key, int Count);

public sealed class RedisUserRepository : IUserRepository, IDisposable
{
    private readonly ReserveOptions options;
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;

    public RedisUserRepository(ReserveOptions options)
    {
        this.options = options;
        connection = new(() => ConnectionMultiplexer.ConnectAsync(options.RedisConnection));
    }

    public async Task<UserDataSet> LoadAsync(CancellationToken cancellationToken)
    {
        var multiplexer = await connection.Value.WaitAsync(cancellationToken);
        var db = multiplexer.GetDatabase();
        string? json = await db.StringGetAsync(options.RedisManifestKey).WaitAsync(cancellationToken);
        if (json is null)
            throw new InvalidDataException("Redis has no published reserve manifest.");
        var manifest = JsonSerializer.Deserialize<RedisManifest>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Invalid Redis reserve manifest.");
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Count < 0 ||
            string.IsNullOrEmpty(manifest.Key) || !manifest.Key.StartsWith(options.RedisUserKeyPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Invalid Redis reserve manifest fields.");

        // The manifest pins an immutable hash. Never SCAN a hash being updated in place.
        // SCAN can repeat fields; deduplicate identical entries before checking the count.
        var users = new Dictionary<int, UserAccount>();
        await foreach (var entry in db.HashScanAsync(manifest.Key, pageSize: 1000).WithCancellation(cancellationToken))
        {
            if (!int.TryParse(entry.Name.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                !long.TryParse(entry.Value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long balance))
                throw new InvalidDataException("Redis user IDs and balances must be nonnegative decimal integers.");
            var user = new UserAccount(id, balance);
            if (users.TryGetValue(id, out var existing) && existing != user)
                throw new InvalidDataException("Redis snapshot changed during loading.");
            users[id] = user;
        }
        if (users.Count != manifest.Count)
            throw new InvalidDataException("Redis snapshot count does not match its manifest.");
        return new UserDataSet(manifest.Version, users.Values.ToArray());
    }

    public void Dispose()
    {
        if (connection.IsValueCreated)
        {
            // Also dispose a connection that finishes after startup cancellation.
            _ = connection.Value.ContinueWith(
                task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); },
                TaskScheduler.Default);
        }
    }
}
