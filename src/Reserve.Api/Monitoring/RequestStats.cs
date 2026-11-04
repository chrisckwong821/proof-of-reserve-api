namespace Reserve.Api;

// Lock-free request counters, read and reset by each heartbeat.
public sealed class RequestStats
{
    private long root;
    private long rootNotModified;
    private long proofFromCache;
    private long proofBuilt;
    private long proofNotModified;
    private long proofNotFound;
    private long proofRejected;

    public void RecordRoot(bool notModified)
    {
        Interlocked.Increment(ref root);
        if (notModified)
        {
            Interlocked.Increment(ref rootNotModified);
        }
    }

    public void RecordProof(bool fromCache, bool notModified)
    {
        Interlocked.Increment(ref fromCache ? ref proofFromCache : ref proofBuilt);
        if (notModified)
        {
            Interlocked.Increment(ref proofNotModified);
        }
    }

    public void RecordProofNotFound() => Interlocked.Increment(ref proofNotFound);

    public void RecordProofRejected() => Interlocked.Increment(ref proofRejected);

    // Return the counts since the previous call and start a new interval.
    public RequestCounts TakeAndReset() => new(
        Interlocked.Exchange(ref root, 0),
        Interlocked.Exchange(ref rootNotModified, 0),
        Interlocked.Exchange(ref proofFromCache, 0),
        Interlocked.Exchange(ref proofBuilt, 0),
        Interlocked.Exchange(ref proofNotModified, 0),
        Interlocked.Exchange(ref proofNotFound, 0),
        Interlocked.Exchange(ref proofRejected, 0));
}

// Root counts every root response; RootNotModified is the 304 subset.
// Proofs served = ProofFromCache + ProofBuilt; ProofNotModified is the 304 subset of those.
public readonly record struct RequestCounts(
    long Root,
    long RootNotModified,
    long ProofFromCache,
    long ProofBuilt,
    long ProofNotModified,
    long ProofNotFound,
    long ProofRejected);
