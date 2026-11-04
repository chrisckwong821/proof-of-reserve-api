using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using TaggedMerkle;

namespace Reserve.Api;

// The snapshot store: holds the published snapshot and the bounded proof cache, and builds replacements.
// RefreshScheduler and ManifestPoller decide when to refresh. (The name predates that split and is kept
// so the log category Reserve.Api.ReserveCache does not change.)
public sealed class ReserveCache : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;
    private readonly ILogger<ReserveCache> logger;
    private readonly IUserDataSource source;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly MemoryCache proofs;
    private Snapshot? current;

    public ReserveCache(
        ReserveOptions options,
        ILogger<ReserveCache> logger,
        IUserDataSource source)
    {
        this.logger = logger;
        this.source = source;
        proofs = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.ProofCacheEntries });
    }

    public Snapshot Current => Volatile.Read(ref current)
        ?? throw new InvalidOperationException("No reserve snapshot has been loaded.");

    // The published snapshot, or null before the first load completes.
    public Snapshot? CurrentOrDefault => Volatile.Read(ref current);

    // Serialize refreshes and atomically publish the completed replacement snapshot.
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            long started = Stopwatch.GetTimestamp();
            var data = await source.LoadAsync(cancellationToken);
            var next = await Task.Run(() => BuildSnapshot(data, cancellationToken), cancellationToken);
            // Publish only the complete replacement; readers can finish using the old snapshot.
            Volatile.Write(ref current, next);
            // Every cached proof belongs to the replaced snapshot, so free the cache for the new one.
            proofs.Compact(1.0);
            logger.LogInformation(
                "Published reserve snapshot {SnapshotId}: {UserCount} users, data version {DataVersion}, " +
                "root {Root}, loaded in {ElapsedMs:F0} ms",
                next.Root.SnapshotId,
                next.Root.UserCount,
                next.Root.DataVersion,
                next.Root.Root,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    // Reuse cached proof JSON, or build it from the snapshot; unknown users return null.
    public byte[]? GetProofJson(Snapshot snapshot, int userId) => GetProofJson(snapshot, userId, out _);

    // Same as above; fromCache reports whether the JSON came from the proof cache or was just built.
    public byte[]? GetProofJson(Snapshot snapshot, int userId, out bool fromCache)
    {
        var key = (snapshot.Root.SnapshotId, userId);
        if (proofs.TryGetValue(key, out byte[]? cached))
        {
            fromCache = true;
            return cached;
        }

        fromCache = false;

        if (!snapshot.Users.TryGetValue(userId, out var user))
        {
            return null;
        }

        object[][] siblings = snapshot.Tree.GetProof(user.Index)
            .Select(node => new object[] { node.Hash, node.Position })
            .ToArray();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ProofResponse(snapshot.Root.SnapshotId, snapshot.Root.Root, userId, user.Balance, siblings),
            JsonOptions);
        // No time-based expiry: RefreshAsync clears the cache when it publishes a new snapshot.
        proofs.Set(key, json, new MemoryCacheEntryOptions { Size = 1 });

        return json;
    }

    // Validate and sort a copy of the users, then build the tree, user index, and root response.
    public static Snapshot BuildSnapshot(UserDataSet data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data.Users);
        var users = (UserAccount[])data.Users.Clone();
        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (user is null || user.Id <= 0 || user.Balance < 0)
            {
                throw new InvalidDataException("Each user needs a positive ID and a nonnegative JPY balance.");
            }
        }

        Array.Sort(users, (a, b) => a.Id.CompareTo(b.Id));
        for (int i = 1; i < users.Length; i++)
        {
            if (users[i - 1].Id == users[i].Id)
            {
                throw new InvalidDataException($"Duplicate user ID: {users[i].Id}.");
            }
        }

        string[] leaves = users.Select(user => user.Serialize()).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var tree = MerkleTree.CreateSnapshot(leaves, ProofTags.Leaf, ProofTags.Branch);
        cancellationToken.ThrowIfCancellationRequested();

        // A user's position in the sorted array is their leaf index in the tree.
        var userIndex = users
            .Select((user, index) => KeyValuePair.Create(user.Id, (index, user.Balance)))
            .ToFrozenDictionary();
        var root = new RootResponse(
            Guid.NewGuid().ToString("N"),
            tree.RootHex,
            users.Length,
            DateTimeOffset.UtcNow,
            data.Version);

        return new Snapshot(root, JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions), tree, userIndex);
    }

    public void Dispose()
    {
        proofs.Dispose();
        refreshGate.Dispose();
    }

    // Data held together so one request always sees a matching root, users, and tree.
    public sealed record Snapshot(
        RootResponse Root,
        byte[] RootJson,
        MerkleTreeSnapshot Tree,
        FrozenDictionary<int, (int Index, long Balance)> Users);
}
