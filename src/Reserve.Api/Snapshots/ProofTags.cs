namespace Reserve.Api;

// Domain-separation tags for the tagged SHA-256 Merkle tree.
// Changing either value changes every root and invalidates every proof already published.
public static class ProofTags
{
    public const string Leaf = "ProofOfReserve_Leaf";
    public const string Branch = "ProofOfReserve_Branch";
}
