using System.Globalization;

namespace Reserve.Api;

// User balances as loaded from Redis; one UserDataSet is one published version.
public sealed record UserAccount(int Id, long Balance)
{
    // The Merkle leaf text, e.g. "(3,3333)": no spaces and no culture-dependent number formatting.
    public string Serialize() => string.Create(CultureInfo.InvariantCulture, $"({Id},{Balance})");
}

public sealed record UserDataSet(string Version, UserAccount[] Users);
