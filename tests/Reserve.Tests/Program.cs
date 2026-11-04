using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reserve.Api;
using StackExchange.Redis;

const string exampleRoot = "b1231de33da17c23cebd80c104b88198e0914b0463d0e14db163605b904a7ba3";
int passed = 0;

// Run one named check and print its result; exceptions make the executable fail.
async Task Test(string name, Func<Task> run)
{
    await run();
    Console.WriteLine($"PASS {++passed:00}: {name}");
}

// Start one HTTP server with a controllable source for cache and refresh scenarios.
var example = await LoadFixtureAsync(
    Path.Combine(AppContext.BaseDirectory, "Fixtures/users-example.json"), CancellationToken.None);
var repository = new MutableSource(example);
await using var app = ReserveApplication.Build(
    ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning"],
    repository.LoadAsync);
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
var store = app.Services.GetRequiredService<ReserveCache>();
var settings = app.Services.GetRequiredService<ReserveOptions>();

await Test("Configuration supplies all settings and missing values are rejected", async () =>
{
    settings.Validate();
    foreach (string key in new[]
    {
        "RefreshTimeUtc", "ProofCacheEntries", "RedisConnection", "RedisManifestKey", "RedisUserKeyPrefix"
    })
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Reserve:{key}"] = null })
            .Build();
        var options = configuration.GetRequiredSection("Reserve").Get<ReserveOptions>()!;
        await Throws<ArgumentException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }
});

await Test("Daily UTC schedule handles boundaries, date rollover, and invalid settings", async () =>
{
    var options = new ReserveOptions
    {
        RefreshTimeUtc = TimeSpan.FromMinutes(10),
        ProofCacheEntries = settings.ProofCacheEntries,
        RedisConnection = settings.RedisConnection,
        RedisManifestKey = settings.RedisManifestKey,
        RedisUserKeyPrefix = settings.RedisUserKeyPrefix
    };
    foreach (var (now, expected) in new[]
    {
        ("2026-01-31T00:09:59Z", "2026-01-31T00:10:00Z"),
        ("2026-01-31T00:10:00Z", "2026-02-01T00:10:00Z"),
        ("2026-01-31T12:00:00Z", "2026-02-01T00:10:00Z"),
        ("2026-01-31T09:09:59+09:00", "2026-01-31T00:10:00Z")
    })
    {
        Check(options.GetNextRefreshUtc(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture))
            == DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture));
    }

    options.RefreshTimeUtc = TimeSpan.Zero;
    options.Validate();
    Check(options.GetNextRefreshUtc(new DateTimeOffset(2026, 1, 31, 23, 59, 59, TimeSpan.Zero))
        == new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
    foreach (TimeSpan? invalid in new TimeSpan?[] { null, TimeSpan.FromSeconds(-1), TimeSpan.FromDays(1) })
    {
        options.RefreshTimeUtc = invalid;
        await Throws<ArgumentException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }
});

await Test("Eight-user root matches independent known answer", async () =>
{
    var root = await client.GetFromJsonAsync<RootResponse>("/api/reserves/root");
    Check(root!.Root == exampleRoot && root.UserCount == 8);
});

await Test("Every example proof has balance, tuple shape, direction and valid root", async () =>
{
    foreach (var user in example.Users)
    {
        using var proof = JsonDocument.Parse(await client.GetStringAsync($"/api/reserves/users/{user.Id}/proof"));
        Check(proof.RootElement.GetProperty("userBalance").GetInt64() == user.Balance);
        Verify(proof.RootElement);
    }
});

await Test("Both endpoints reuse cache and support conditional GET", async () =>
{
    foreach (string path in new[] { "/api/reserves/root", "/api/reserves/users/1/proof" })
    {
        using var first = await client.GetAsync(path);
        string body = await first.Content.ReadAsStringAsync();
        using var second = await client.GetAsync(path);
        Check(body == await second.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await client.SendAsync(request);
        Check(unchanged.StatusCode == HttpStatusCode.NotModified);
    }
    Check(repository.Reads == 1);
    var snapshot = store.Current;
    Check(ReferenceEquals(store.GetProofJson(snapshot, 1), store.GetProofJson(snapshot, 1)));
});

await Test("Concurrent cached requests do not reread the database", async () =>
{
    await Task.WhenAll(Enumerable.Range(0, 64).Select(async i =>
    {
        string path = i % 2 == 0 ? "/api/reserves/root" : $"/api/reserves/users/{i % 8 + 1}/proof";
        using var response = await client.GetAsync(path);
        Check(response.IsSuccessStatusCode);
    }));
    Check(repository.Reads == 1);
});

await Test("Unknown and invalid IDs return 404 and 400", async () =>
{
    using var missing = await client.GetAsync("/api/reserves/users/999/proof");
    using var invalid = await client.GetAsync("/api/reserves/users/0/proof");
    Check(missing.StatusCode == HttpStatusCode.NotFound && invalid.StatusCode == HttpStatusCode.BadRequest);
});

await Test("Numeric ordering and invariant tuple serialization", async () =>
{
    var previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
        Check(new UserAccount(10, 100).Serialize() == "(10,100)");
        repository.Data = example with
        {
            Users = example.Users.Reverse().ToArray()
        };
        await store.RefreshAsync();
        Check(store.Current.Root.Root == exampleRoot);
    }
    finally
    {
        CultureInfo.CurrentCulture = previous;
    }
});

await Test("Refresh changes both caches and rejects requests pinned to an old snapshot", async () =>
{
    var previous = store.Current;
    byte[] oldProof = store.GetProofJson(previous, 1)!;
    repository.Data = new UserDataSet("updated", [new UserAccount(1, 999), .. example.Users.Skip(1)]);
    await store.RefreshAsync();
    Check(store.Current.Root.Root != previous.Root.Root);
    Check(!ReferenceEquals(oldProof, store.GetProofJson(store.Current, 1)));
    using var response = await client.GetAsync($"/api/reserves/users/1/proof?snapshotId={previous.Root.SnapshotId}");
    Check(response.StatusCode == HttpStatusCode.Conflict);
    using var proof = JsonDocument.Parse(await client.GetStringAsync("/api/reserves/users/1/proof"));
    Check(proof.RootElement.GetProperty("userBalance").GetInt64() == 999);
    Verify(proof.RootElement);
    using var old = JsonDocument.Parse(oldProof);
    Verify(old.RootElement);
});

await Test("Failed refresh retains the last complete snapshot", async () =>
{
    var previous = store.Current;
    repository.Fail = true;
    await Throws<IOException>(() => store.RefreshAsync());
    Check(ReferenceEquals(previous, store.Current));
    repository.Fail = false;
});

await Test("Malformed datasets cannot replace the cached snapshot", async () =>
{
    var previous = store.Current;
    foreach (UserAccount[] values in new[]
    {
        new[] { new UserAccount(1, 1), new UserAccount(1, 2) },
        new[] { new UserAccount(0, 1) },
        new[] { new UserAccount(1, -1) },
        new UserAccount[] { null! }
    })
    {
        repository.Data = new UserDataSet("invalid", values);
        await Throws<InvalidDataException>(() => store.RefreshAsync());
        Check(ReferenceEquals(previous, store.Current));
    }
});

await Test("Empty dataset returns a zero root", async () =>
{
    repository.Data = new UserDataSet("empty", []);
    await store.RefreshAsync();
    var empty = await client.GetFromJsonAsync<RootResponse>("/api/reserves/root");
    Check(empty!.Root == new string('0', 64) && empty.UserCount == 0);

});

await Test("Odd-sized tree proofs duplicate the last sibling at each necessary level", async () =>
{
    repository.Data = example with
    {
        Users = example.Users.Take(5).ToArray()
    };
    await store.RefreshAsync();
    foreach (var user in repository.Data.Users)
    {
        using var proof = JsonDocument.Parse(await client.GetStringAsync($"/api/reserves/users/{user.Id}/proof"));
        Verify(proof.RootElement);
    }
});

await Test("Saved 100k fixture has exact IDs, valid balances, and correct sampled proofs", async () =>
{
    var data = await LoadFixtureAsync(
        Path.Combine(AppContext.BaseDirectory, "Fixtures/users-100000.json"), CancellationToken.None);
    Check(data.Users.Length == 100_000);
    Check(data.Users.Select(u => u.Id).SequenceEqual(Enumerable.Range(1, 100_000)));
    Check(data.Users.All(u => u.Balance is >= 0 and < 1_000_000));
    repository.Data = data;
    await store.RefreshAsync();
    Check(store.Current.Root.UserCount == 100_000);
    foreach (int id in new[] { 1, 2, 3, 32768, 65535, 99999, 100000 })
    {
        using var proof = JsonDocument.Parse(await client.GetStringAsync($"/api/reserves/users/{id}/proof"));
        Verify(proof.RootElement);
    }
});

await Test("Small proof cache can evict and regenerate proofs correctly", async () =>
{
    using var small = new ReserveCache(
        new ReserveOptions
        {
            RefreshTimeUtc = settings.RefreshTimeUtc,
            ProofCacheEntries = 1,
            RedisConnection = settings.RedisConnection,
            RedisManifestKey = settings.RedisManifestKey,
            RedisUserKeyPrefix = settings.RedisUserKeyPrefix
        },
        NullLogger<ReserveCache>.Instance,
        new MutableSource(example).LoadAsync);
    await small.RefreshAsync();
    foreach (int id in new[] { 1, 2, 3, 1 })
    {
        using var proof = JsonDocument.Parse(small.GetProofJson(small.Current, id)!);
        Verify(proof.RootElement);
    }
});

await Test("Slow refresh serves the old snapshot until replacement is ready", async () =>
{
    var previous = store.Current;
    repository.Data = example;
    repository.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var refresh = store.RefreshAsync();
    var root = await client.GetFromJsonAsync<RootResponse>("/api/reserves/root");
    Check(root!.SnapshotId == previous.Root.SnapshotId);
    repository.Gate.SetResult();
    await refresh;
    repository.Gate = null;
    Check(store.Current.Root.Root == exampleRoot && store.Current.Root.SnapshotId != previous.Root.SnapshotId);
});

await Test("Scheduled UTC refresh publishes changed data without a restart", async () =>
{
    var source = new MutableSource(example);
    DateTimeOffset scheduled = DateTimeOffset.UtcNow.AddSeconds(5);
    await using var timedApp = ReserveApplication.Build(
        [
            "--urls", "http://127.0.0.1:0",
            "--Reserve:RefreshTimeUtc", scheduled.TimeOfDay.ToString("c", CultureInfo.InvariantCulture),
            "--Logging:LogLevel:Default", "Warning"
        ],
        source.LoadAsync);
    await timedApp.StartAsync();
    var timedStore = timedApp.Services.GetRequiredService<ReserveCache>();
    Check(source.Reads == 1);
    source.Data = new UserDataSet("next", [new UserAccount(1, 999), .. example.Users.Skip(1)]);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (timedStore.Current.Root.DataVersion != "next")
    {
        await Task.Delay(10, deadline.Token);
    }

    Check(DateTimeOffset.UtcNow >= scheduled);
    Check(timedStore.Current.Root.Root != exampleRoot && source.Reads == 2);
    await timedApp.StopAsync();
});

string? redisConnection = Environment.GetEnvironmentVariable("RESERVE_TEST_REDIS");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    await Test("Redis-only app serves all saved fixtures and rejects an invalid manifest", async () =>
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var db = connection.GetDatabase();
        string suffix = Guid.NewGuid().ToString("N");
        string manifestKey = "por:test:" + suffix;
        string usersKey = "por:users:test:" + suffix;
        try
        {
            foreach (string fixture in new[] { "users-example.json", "users-100000.json" })
            {
                var data = await LoadFixtureAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), CancellationToken.None);
                await db.KeyDeleteAsync(usersKey);
                foreach (var batch in data.Users.Chunk(1000))
                {
                    await db.HashSetAsync(usersKey, batch.Select(u => new HashEntry(u.Id, u.Balance)).ToArray());
                }

                await db.StringSetAsync(manifestKey,
                    JsonSerializer.Serialize(new RedisManifest(fixture, usersKey, data.Users.Length)));

                // No loader override: the server must read the dataset from Redis.
                await using var redisApp = ReserveApplication.Build(
                    [
                        "--urls", "http://127.0.0.1:0",
                        "--Reserve:RedisConnection", redisConnection,
                        "--Reserve:RedisManifestKey", manifestKey,
                        "--Logging:LogLevel:Default", "Warning"
                    ]);
                await redisApp.StartAsync();
                using var http = new HttpClient { BaseAddress = new Uri(redisApp.Urls.Single()) };
                var root = await http.GetFromJsonAsync<RootResponse>("/api/reserves/root");
                Check(root!.UserCount == data.Users.Length && root.DataVersion == fixture);
                if (fixture == "users-example.json")
                {
                    Check(root.Root == exampleRoot);
                }

                if (fixture == "users-100000.json")
                {
                    Check(root.Root == "347cfc213378db8331eef9b7fac2dccf625401a1fd1339b9f345432843221a9f");
                }

                int[] ids = data.Users.Length > 8
                    ? [1, 2, 3, 32768, 65535, 99999, 100000]
                    : data.Users.Select(user => user.Id).ToArray();
                foreach (int id in ids)
                {
                    using var proof = JsonDocument.Parse(await http.GetStringAsync($"/api/reserves/users/{id}/proof"));
                    Check(proof.RootElement.GetProperty("root").GetString() == root.Root);
                    Check(proof.RootElement.GetProperty("userBalance").GetInt64()
                        == data.Users.Single(user => user.Id == id).Balance);
                    Verify(proof.RootElement);
                }
                await redisApp.StopAsync();
            }
            var options = new ReserveOptions
            {
                RefreshTimeUtc = settings.RefreshTimeUtc,
                ProofCacheEntries = settings.ProofCacheEntries,
                RedisConnection = redisConnection,
                RedisManifestKey = manifestKey,
                RedisUserKeyPrefix = settings.RedisUserKeyPrefix
            };
            using var adapter = new RedisConnector(options);
            await db.StringSetAsync(manifestKey, JsonSerializer.Serialize(new RedisManifest("bad", usersKey, 100001)));
            await Throws<InvalidDataException>(() => adapter.LoadAsync(CancellationToken.None));
        }
        finally
        {
            await db.KeyDeleteAsync(new RedisKey[] { manifestKey, usersKey });
        }
    });
}
else
{
    Console.WriteLine("SKIP Redis integration: set RESERVE_TEST_REDIS to an isolated test instance.");
}

await app.StopAsync();
Console.WriteLine($"Result: {passed} checks passed.");
return 0;

// Fixture reading belongs to the test executable, never the server.
static async Task<UserDataSet> LoadFixtureAsync(string path, CancellationToken cancellationToken)
{
    await using var stream = File.OpenRead(path);
    var users = await JsonSerializer.DeserializeAsync<UserAccount[]>(
        stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
        ?? throw new InvalidDataException("The fixture must contain a JSON array.");
    return new UserDataSet(Path.GetFileName(path), users);
}

// Fail immediately when a test condition is not satisfied.
static void Check(bool condition)
{
    if (!condition)
    {
        throw new Exception("Assertion failed.");
    }
}

// Verify that the operation throws the expected exception type.
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }
    throw new Exception($"Expected {typeof(T).Name}.");
}

// Independently reconstruct the root from the returned balance and sibling tuples.
static void Verify(JsonElement response)
{
    // Apply the tagged SHA-256 formula without calling the production Merkle library.
    byte[] Hash(string tag, byte[] message)
    {
        byte[] prefix = SHA256.HashData(Encoding.UTF8.GetBytes(tag));
        return SHA256.HashData([.. prefix, .. prefix, .. message]);
    }

    int id = response.GetProperty("userId").GetInt32();
    long balance = response.GetProperty("userBalance").GetInt64();
    byte[] current = Hash(
        "ProofOfReserve_Leaf",
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"({id},{balance})")));
    foreach (var tuple in response.GetProperty("proof").EnumerateArray())
    {
        Check(tuple.GetArrayLength() == 2);
        string hex = tuple[0].GetString()!;
        Check(hex.Length == 64 && hex == hex.ToLowerInvariant());
        byte[] sibling = Convert.FromHexString(hex);
        int position = tuple[1].GetInt32();
        Check(position is 0 or 1);
        current = Hash(
            "ProofOfReserve_Branch",
            position == 0 ? [.. sibling, .. current] : [.. current, .. sibling]);
    }
    Check(Convert.ToHexStringLower(current) == response.GetProperty("root").GetString());
}

internal sealed class MutableSource(UserDataSet data)
{
    public UserDataSet Data { get; set; } = data;
    public bool Fail
    {
        get; set;
    }
    public int Reads;
    public TaskCompletionSource? Gate
    {
        get; set;
    }

    // Count reads and simulate delayed or failed loads without changing production code.
    public async Task<UserDataSet> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        if (Fail)
        {
            throw new IOException("Simulated source failure.");
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }

        return Data;
    }
}
