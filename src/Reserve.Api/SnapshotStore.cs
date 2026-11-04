using Microsoft.Extensions.Caching.Memory;

namespace Reserve.Api;

public sealed class SnapshotStore : IDisposable
{
    private readonly IUserRepository repository;
    private readonly ReserveOptions options;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly MemoryCache proofs;
    private ReserveSnapshot? current;

    public SnapshotStore(IUserRepository repository, ReserveOptions options)
    {
        this.repository = repository;
        this.options = options;
        proofs = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.ProofCacheEntries });
    }

    public ReserveSnapshot Current => Volatile.Read(ref current)
        ?? throw new InvalidOperationException("No reserve snapshot has been loaded.");

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            UserDataSet data = await repository.LoadAsync(cancellationToken);
            // CPU work stays off request threads. The old snapshot serves requests throughout.
            var next = await Task.Run(() => ReserveSnapshot.Build(data, cancellationToken), cancellationToken);
            Volatile.Write(ref current, next);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public byte[]? GetProofJson(ReserveSnapshot snapshot, int userId)
    {
        var key = (snapshot.Root.SnapshotId, userId);
        if (proofs.TryGetValue(key, out byte[]? cached)) return cached;
        byte[]? result = snapshot.CreateProofJson(userId);
        if (result is not null)
        {
            // Unknown IDs are not cached, so arbitrary misses cannot fill the cache.
            // Concurrent misses may repeat O(log n) work but never rebuild the tree.
            proofs.Set(key, result, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = options.RefreshInterval
            });
        }
        return result;
    }

    public void Dispose()
    {
        proofs.Dispose();
        refreshGate.Dispose();
    }
}
