using System.Globalization;
using Microsoft.Extensions.Options;

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

    // Optional. How often to log a heartbeat line with snapshot age and request counts; 00:00:00 turns it off.
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(5);

    // Optional. How long after a scheduled refresh /health still reports healthy if no new snapshot has appeared.
    public TimeSpan StaleGracePeriod { get; set; } = TimeSpan.FromMinutes(30);

    // The validated refresh time. RefreshTimeUtc stays nullable so a missing setting is rejected, not read as 00:00.
    public TimeSpan RefreshTime => RefreshTimeUtc
        ?? throw new InvalidOperationException("RefreshTimeUtc is not configured.");

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

        if (HeartbeatInterval < TimeSpan.Zero)
        {
            throw new ArgumentException("HeartbeatInterval must be zero (off) or positive.");
        }

        if (StaleGracePeriod < TimeSpan.Zero || StaleGracePeriod >= TimeSpan.FromDays(1))
        {
            throw new ArgumentException("StaleGracePeriod must be between 00:00:00 and 23:59:59.9999999.");
        }
    }

    // Return the next occurrence of the configured UTC time, rolling over to tomorrow when needed.
    public DateTimeOffset GetNextRefreshUtc(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        var scheduled = new DateTimeOffset(utc.Date, TimeSpan.Zero) + RefreshTime;
        return scheduled > utc ? scheduled : scheduled.AddDays(1);
    }
}

// Runs ReserveOptions.Validate() through the options pipeline so failures surface as OptionsValidationException.
public sealed class ReserveOptionsValidator : IValidateOptions<ReserveOptions>
{
    public ValidateOptionsResult Validate(string? name, ReserveOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException error)
        {
            return ValidateOptionsResult.Fail(error.Message);
        }
    }
}
