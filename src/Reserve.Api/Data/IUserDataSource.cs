namespace Reserve.Api;

// Where user balances come from. RedisConnector is the production source; tests supply an in-memory one.
public interface IUserDataSource
{
    // Load every user of the currently published version.
    Task<UserDataSet> LoadAsync(CancellationToken cancellationToken);

    // Read only the currently published version, a cheap check used to detect new data without loading it.
    Task<string> GetManifestVersionAsync(CancellationToken cancellationToken);
}
