using System.Globalization;

namespace Reserve.Api;

// User balances as loaded from Redis; one UserDataSet is one published version.
public sealed record UserAccount(int Id, long Balance)
{
    // Produce the exact leaf text without spaces or culture-dependent number formatting.
    public string Serialize() => string.Create(CultureInfo.InvariantCulture, $"({Id},{Balance})");
}

public sealed record UserDataSet(string Version, UserAccount[] Users);
