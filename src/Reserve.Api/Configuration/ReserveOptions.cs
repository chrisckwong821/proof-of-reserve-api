using Microsoft.Extensions.Options;

namespace Reserve.Api;

public sealed class ReserveOptions
{
    public TimeSpan? RefreshTimeUtc { get; set; }
    public int ProofCacheEntries { get; set; }
    public required string RedisConnection { get; set; }
    public required string RedisManifestKey { get; set; }
    public required string RedisUserKeyPrefix { get; set; }

    // Optional. How long after a scheduled refresh /health still reports healthy if no new snapshot has appeared.
    public TimeSpan StaleGracePeriod { get; set; } = TimeSpan.FromMinutes(30);

    // Optional. First wait before retrying a failed scheduled refresh; doubles each time. 00:00:00 turns retries off.
    public TimeSpan RefreshRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    // Optional. Longest wait between retries.
    public TimeSpan RefreshRetryMaxDelay { get; set; } = TimeSpan.FromMinutes(15);

    // Optional. How often to check the Redis manifest version and refresh when it changes; 00:00:00 (default) is off.
    public TimeSpan ManifestPollInterval { get; set; } = TimeSpan.Zero;

    // The validated refresh time. RefreshTimeUtc stays nullable so a missing setting is rejected, not read as 00:00.
    public TimeSpan RefreshTime => RefreshTimeUtc
        ?? throw new InvalidOperationException("RefreshTimeUtc is not configured.");

    // Every problem with the current values, one message per setting; empty when the settings are valid.
    public IReadOnlyList<string> GetErrors()
    {
        var errors = new List<string>();
        void Require(bool valid, string message)
        {
            if (!valid)
            {
                errors.Add(message);
            }
        }

        Require(!string.IsNullOrWhiteSpace(RedisConnection), "RedisConnection is required.");
        Require(!string.IsNullOrWhiteSpace(RedisManifestKey), "RedisManifestKey is required.");
        Require(!string.IsNullOrWhiteSpace(RedisUserKeyPrefix), "RedisUserKeyPrefix is required.");
        Require(RefreshTimeUtc is { } time && IsUnderOneDay(time),
            "RefreshTimeUtc is required and must be between 00:00:00 and 23:59:59.9999999.");
        Require(ProofCacheEntries >= 1, "ProofCacheEntries must be positive.");
        Require(IsUnderOneDay(StaleGracePeriod),
            "StaleGracePeriod must be between 00:00:00 and 23:59:59.9999999.");
        Require(IsUnderOneDay(RefreshRetryDelay),
            "RefreshRetryDelay must be zero (off) or positive and under one day.");
        Require(RefreshRetryMaxDelay >= RefreshRetryDelay && IsUnderOneDay(RefreshRetryMaxDelay),
            "RefreshRetryMaxDelay must be at least RefreshRetryDelay and under one day.");
        Require(IsUnderOneDay(ManifestPollInterval),
            "ManifestPollInterval must be zero (off) or positive and under one day.");
        return errors;
    }

    // Throws an ArgumentException listing every invalid setting.
    public void Validate()
    {
        var errors = GetErrors();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors));
        }
    }

    private static bool IsUnderOneDay(TimeSpan value) => value >= TimeSpan.Zero && value < TimeSpan.FromDays(1);

    // Return the next occurrence of the configured UTC time, rolling over to tomorrow when needed.
    public DateTimeOffset GetNextRefreshUtc(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        var scheduled = new DateTimeOffset(utc.Date, TimeSpan.Zero) + RefreshTime;
        return scheduled > utc ? scheduled : scheduled.AddDays(1);
    }
}

// Plugs the checks into ASP.NET Core options validation, so startup fails with every invalid setting listed.
public sealed class ReserveOptionsValidator : IValidateOptions<ReserveOptions>
{
    public ValidateOptionsResult Validate(string? name, ReserveOptions options)
    {
        var errors = options.GetErrors();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
