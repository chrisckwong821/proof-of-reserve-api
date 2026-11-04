using System.Globalization;

namespace Reserve.Api;

public sealed record UserAccount(int Id, long Balance)
{
    public string Serialize() => string.Create(CultureInfo.InvariantCulture, $"({Id},{Balance})");
}

public sealed record UserDataSet(string Version, UserAccount[] Users);

public sealed record RootResponse(
    string SnapshotId, string Root, int UserCount, DateTimeOffset GeneratedAtUtc, string DataVersion);

public sealed record ProofResponse(
    string SnapshotId, string Root, int UserId, long UserBalance, object[][] Proof);

public sealed class ReserveOptions
{
    public string Source { get; set; } = "File";
    public string FilePath { get; set; } = "data/users-100000.json";
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromDays(1);
    public int ProofCacheEntries { get; set; } = 10_000;
    public string RedisConnection { get; set; } = "localhost:6379,abortConnect=false";
    public string RedisManifestKey { get; set; } = "por:current";
    public string RedisUserKeyPrefix { get; set; } = "por:users:";

    public void Validate()
    {
        if (Source is not ("File" or "Redis"))
            throw new ArgumentException("Reserve:Source must be File or Redis.");
        if (RefreshInterval <= TimeSpan.Zero || RefreshInterval > TimeSpan.FromDays(30))
            throw new ArgumentException("RefreshInterval must be positive and at most 30 days.");
        if (ProofCacheEntries < 1)
            throw new ArgumentException("ProofCacheEntries must be positive.");
    }
}
