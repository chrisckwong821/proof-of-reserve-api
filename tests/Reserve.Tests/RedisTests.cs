using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Reserve.Api;
using StackExchange.Redis;

namespace Reserve.Tests;

// Runs only when RESERVE_TEST_REDIS points at an isolated test Redis; otherwise reported as skipped.
public class RedisTests
{
    private string connectionString = null!;
    private ConnectionMultiplexer connection = null!;
    private IDatabase db = null!;
    private string manifestKey = null!;
    private string usersKey = null!;

    [SetUp]
    public async Task Connect()
    {
        string? configured = Environment.GetEnvironmentVariable("RESERVE_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("Set RESERVE_TEST_REDIS to an isolated test Redis instance to run Redis tests.");
        }

        connectionString = configured;
        connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        db = connection.GetDatabase();
        string suffix = Guid.NewGuid().ToString("N");
        manifestKey = "por:test:" + suffix;
        usersKey = "por:users:test:" + suffix;
    }

    [TearDown]
    public async Task Cleanup()
    {
        if (connection is not null)
        {
            await db.KeyDeleteAsync([manifestKey, usersKey]);
            connection.Dispose();
        }
    }

    [TestCase("users-example.json", Fixtures.ExampleRoot)]
    [TestCase("users-100000.json", Fixtures.LargeRoot)]
    public async Task ServerReadsFixtureFromRedis(string fixture, string expectedRoot)
    {
        var data = await Fixtures.LoadAsync(fixture);
        await PublishAsync(data, fixture);

        // No loader override: the server must read the dataset from Redis.
        await using var app = ReserveApplication.Build(
        [
            "--urls", "http://127.0.0.1:0",
            "--Reserve:RedisConnection", connectionString,
            "--Reserve:RedisManifestKey", manifestKey,
            "--Logging:LogLevel:Default", "Warning"
        ]);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var root = await http.GetFromJsonAsync<RootResponse>("/api/reserves/root");
        Assert.That(root!.Root, Is.EqualTo(expectedRoot));
        Assert.That(root.UserCount, Is.EqualTo(data.Users.Length));
        Assert.That(root.DataVersion, Is.EqualTo(fixture));

        int[] ids = data.Users.Length > 8
            ? [1, 2, 3, 32768, 65535, 99999, 100000]
            : data.Users.Select(user => user.Id).ToArray();
        var balances = data.Users.ToDictionary(user => user.Id, user => user.Balance);
        foreach (int id in ids)
        {
            using var proof = JsonDocument.Parse(await http.GetStringAsync($"/api/reserves/users/{id}/proof"));
            Assert.That(proof.RootElement.GetProperty("root").GetString(), Is.EqualTo(root.Root));
            Assert.That(proof.RootElement.GetProperty("userBalance").GetInt64(), Is.EqualTo(balances[id]));
            ProofVerifier.Verify(proof.RootElement);
        }

        await app.StopAsync();
    }

    [Test]
    public async Task ManifestVersionIsReadWithoutLoadingUsers()
    {
        await PublishAsync(await Fixtures.ExampleAsync(), "version-check");
        using var connector = new RedisConnector(new ReserveOptions
        {
            RefreshTimeUtc = TimeSpan.Zero,
            ProofCacheEntries = 1,
            RedisConnection = connectionString,
            RedisManifestKey = manifestKey,
            RedisUserKeyPrefix = "por:users:"
        });

        Assert.That(await connector.GetManifestVersionAsync(CancellationToken.None), Is.EqualTo("version-check"));
    }

    [Test]
    public async Task ManifestCountMismatchIsRejected()
    {
        var data = await Fixtures.ExampleAsync();
        await PublishAsync(data, "bad");
        await db.StringSetAsync(
            manifestKey, JsonSerializer.Serialize(new RedisManifest("bad", usersKey, data.Users.Length + 1)));

        using var connector = new RedisConnector(new ReserveOptions
        {
            RefreshTimeUtc = TimeSpan.Zero,
            ProofCacheEntries = 1,
            RedisConnection = connectionString,
            RedisManifestKey = manifestKey,
            RedisUserKeyPrefix = "por:users:"
        });

        Assert.ThrowsAsync<InvalidDataException>(() => connector.LoadAsync(CancellationToken.None));
    }

    [Test]
    public async Task DisposeAsyncClosesTheRedisConnection()
    {
        await PublishAsync(await Fixtures.ExampleAsync(), "dispose");
        string clientName = "por-dispose-test-" + Guid.NewGuid().ToString("N");
        var connector = new RedisConnector(new ReserveOptions
        {
            RefreshTimeUtc = TimeSpan.Zero,
            ProofCacheEntries = 1,
            RedisConnection = connectionString + ",name=" + clientName,
            RedisManifestKey = manifestKey,
            RedisUserKeyPrefix = "por:users:"
        });
        // CLIENT LIST is an admin command, so inspect connections through a separate admin connection.
        using var admin = await ConnectionMultiplexer.ConnectAsync(connectionString + ",allowAdmin=true");
        var server = admin.GetServer(admin.GetEndPoints().Single());
        async Task<bool> ConnectedAsync() => (await server.ClientListAsync()).Any(client => client.Name == clientName);

        await connector.LoadAsync(CancellationToken.None);
        Assert.That(await ConnectedAsync(), Is.True, "Connected while loading");

        await connector.DisposeAsync();

        // Redis may take a moment to drop the closed sockets from CLIENT LIST.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await ConnectedAsync())
        {
            await Task.Delay(50, deadline.Token);
        }
    }

    private async Task PublishAsync(UserDataSet data, string version)
    {
        foreach (var batch in data.Users.Chunk(1000))
        {
            await db.HashSetAsync(usersKey, batch.Select(u => new HashEntry(u.Id, u.Balance)).ToArray());
        }

        await db.StringSetAsync(
            manifestKey, JsonSerializer.Serialize(new RedisManifest(version, usersKey, data.Users.Length)));
    }
}
