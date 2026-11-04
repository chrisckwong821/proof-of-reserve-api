namespace Reserve.Api;

// JSON bodies of GET /api/reserves/root and GET /api/reserves/users/{userId}/proof.
// Proof is a list of [siblingHash, position] pairs, written as JSON arrays.
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
