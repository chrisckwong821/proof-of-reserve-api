namespace Reserve.Api;

public interface IUserRepository
{
    Task<UserDataSet> LoadAsync(CancellationToken cancellationToken);
}
