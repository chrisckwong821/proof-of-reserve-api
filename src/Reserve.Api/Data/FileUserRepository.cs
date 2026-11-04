using System.Text.Json;

namespace Reserve.Api;

public sealed class FileUserRepository(string path) : IUserRepository
{
    public async Task<UserDataSet> LoadAsync(CancellationToken cancellationToken)
    {
        // The publisher must replace the file atomically, not edit it in place.
        await using var stream = File.OpenRead(path);
        var users = await JsonSerializer.DeserializeAsync<UserAccount[]>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
            ?? throw new InvalidDataException("The user file must contain a JSON array.");
        return new UserDataSet(Path.GetFileName(path), users);
    }
}
