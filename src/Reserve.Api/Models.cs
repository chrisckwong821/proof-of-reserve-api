using System.Globalization;

namespace Reserve.Api;

public sealed record UserAccount(int Id, long Balance)
{
    // Produce the exact leaf text without spaces or culture-dependent number formatting.
    public string Serialize() => string.Create(CultureInfo.InvariantCulture, $"({Id},{Balance})");
}

public sealed record UserDataSet(string Version, UserAccount[] Users);

public sealed record RootResponse(
    string SnapshotId,
    string Root,
    int UserCount,
    DateTimeOffset GeneratedAtUtc,
    string DataVersion);

public sealed record ProofResponse(
    string SnapshotId,
    string Root,
    int UserId,
    long UserBalance,
    object[][] Proof);

public sealed class ReserveOptions
{
    public TimeSpan? RefreshTimeUtc { get; set; }
    public int ProofCacheEntries { get; set; }
    public required string RedisConnection { get; set; }
    public required string RedisManifestKey { get; set; }
    public required string RedisUserKeyPrefix { get; set; }

    // Reject missing or invalid configuration before the server starts.
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RedisConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(RedisManifestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(RedisUserKeyPrefix);

        if (RefreshTimeUtc is not { } time || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            throw new ArgumentException("RefreshTimeUtc is required and must be between 00:00:00 and 23:59:59.9999999.");
        }

        if (ProofCacheEntries < 1)
        {
            throw new ArgumentException("ProofCacheEntries must be positive.");
        }
    }

    // Return the next occurrence of the configured UTC time, rolling over to tomorrow when needed.
    public DateTimeOffset GetNextRefreshUtc(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        var scheduled = new DateTimeOffset(utc.Date, TimeSpan.Zero) + RefreshTimeUtc!.Value;
        return scheduled > utc ? scheduled : scheduled.AddDays(1);
    }
}
