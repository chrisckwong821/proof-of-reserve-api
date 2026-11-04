using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using TaggedMerkle;

namespace Reserve.Api;

// Holds the published snapshot and the bounded proof cache; RefreshScheduler decides when to refresh.
public sealed class ReserveCache : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;
    private readonly ReserveOptions options;
    private readonly ILogger<ReserveCache> logger;
    private readonly Func<CancellationToken, Task<UserDataSet>> loadUsers;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly MemoryCache proofs;
    private Snapshot? current;

    // Set up the bounded proof cache and the loader used for snapshot refreshes.
    public ReserveCache(
        ReserveOptions options,
        ILogger<ReserveCache> logger,
        Func<CancellationToken, Task<UserDataSet>> loadUsers)
    {
        this.options = options;
        this.logger = logger;
        this.loadUsers = loadUsers;
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
            var data = await loadUsers(cancellationToken);
            var next = await Task.Run(() => BuildSnapshot(data, cancellationToken), cancellationToken);
            // Publish only the complete replacement; readers can finish using the old snapshot.
            Volatile.Write(ref current, next);
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
        proofs.Set(key, json, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpiration = options.GetNextRefreshUtc(DateTimeOffset.UtcNow)
        });

        return json;
    }

    // Validate and sort a copy of the users, then build the tree, user index, and root response.
    public static Snapshot BuildSnapshot(UserDataSet data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data.Users);
        var ordered = (UserAccount[])data.Users.Clone();
        foreach (var user in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (user is null || user.Id <= 0 || user.Balance < 0)
            {
                throw new InvalidDataException("Each user needs a positive ID and a nonnegative JPY balance.");
            }
        }

        Array.Sort(ordered, (a, b) => a.Id.CompareTo(b.Id));
        for (int i = 1; i < ordered.Length; i++)
        {
            if (ordered[i - 1].Id == ordered[i].Id)
            {
                throw new InvalidDataException($"Duplicate user ID: {ordered[i].Id}.");
            }
        }

        string[] leaves = ordered.Select(user => user.Serialize()).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var tree = MerkleTree.CreateSnapshot(leaves, ProofTags.Leaf, ProofTags.Branch);
        cancellationToken.ThrowIfCancellationRequested();

        var users = ordered.Select((user, index) => (user.Id, index, user.Balance))
            .ToFrozenDictionary(item => item.Id, item => (item.index, item.Balance));
        var root = new RootResponse(
            Guid.NewGuid().ToString("N"),
            tree.RootHex,
            ordered.Length,
            DateTimeOffset.UtcNow,
            data.Version);

        return new Snapshot(root, JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions), tree, users);
    }

    // Dispose the proof cache and refresh semaphore.
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
