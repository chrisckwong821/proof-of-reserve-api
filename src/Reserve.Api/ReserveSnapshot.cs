using System.Collections.Frozen;
using System.Text.Json;
using TaggedMerkle;

namespace Reserve.Api;

public sealed class ReserveSnapshot
{
    public const string LeafTag = "ProofOfReserve_Leaf";
    public const string BranchTag = "ProofOfReserve_Branch";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly MerkleTreeSnapshot tree;
    private readonly FrozenDictionary<int, (int Index, long Balance)> users;

    private ReserveSnapshot(MerkleTreeSnapshot tree, UserAccount[] ordered, string version)
    {
        this.tree = tree;
        users = ordered.Select((user, index) => (user.Id, index, user.Balance))
            .ToFrozenDictionary(item => item.Id, item => (item.index, item.Balance));
        Root = new RootResponse(Guid.NewGuid().ToString("N"), tree.RootHex, ordered.Length, DateTimeOffset.UtcNow, version);
        RootJson = JsonSerializer.SerializeToUtf8Bytes(Root, JsonOptions);
    }

    public RootResponse Root { get; }
    public byte[] RootJson { get; }

    public static ReserveSnapshot Build(UserDataSet data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data.Users);
        var ordered = (UserAccount[])data.Users.Clone();
        foreach (var user in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (user is null || user.Id <= 0 || user.Balance < 0)
                throw new InvalidDataException("Each user needs a positive ID and a nonnegative JPY balance.");
        }
        Array.Sort(ordered, (a, b) => a.Id.CompareTo(b.Id));
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i - 1].Id == ordered[i].Id)
                throw new InvalidDataException($"Duplicate user ID: {ordered[i].Id}.");
        string[] leaves = ordered.Select(user => user.Serialize()).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var tree = MerkleTree.CreateSnapshot(leaves, LeafTag, BranchTag);
        cancellationToken.ThrowIfCancellationRequested();
        return new ReserveSnapshot(tree, ordered, data.Version);
    }

    public byte[]? CreateProofJson(int id)
    {
        if (!users.TryGetValue(id, out var user)) return null;
        object[][] proof = tree.GetProof(user.Index)
            .Select(node => new object[] { node.Hash, node.Position })
            .ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(
            new ProofResponse(Root.SnapshotId, Root.Root, id, user.Balance, proof), JsonOptions);
    }
}
