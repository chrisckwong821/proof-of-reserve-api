using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Reserve.Api;
using StackExchange.Redis;

const string exampleRoot = "b1231de33da17c23cebd80c104b88198e0914b0463d0e14db163605b904a7ba3";
int passed = 0;
async Task Test(string name, Func<Task> run)
{
    await run();
    Console.WriteLine($"PASS {++passed:00}: {name}");
}
var example = await new FileUserRepository(Path.Combine(AppContext.BaseDirectory, "data/users-example.json"))
    .LoadAsync(CancellationToken.None);
var repository = new MutableRepository(example);
await using var app = ReserveApplication.Build(
    ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning"],
    services => services.AddSingleton<IUserRepository>(repository));
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
var store = app.Services.GetRequiredService<SnapshotStore>();

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
        repository.Data = example with { Users = example.Users.Reverse().ToArray() };
        await store.RefreshAsync();
        Check(store.Current.Root.Root == exampleRoot);
    }
    finally { CultureInfo.CurrentCulture = previous; }
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
await Test("Empty and singleton HTTP responses", async () =>
{
    repository.Data = new UserDataSet("empty", []);
    await store.RefreshAsync();
    var empty = await client.GetFromJsonAsync<RootResponse>("/api/reserves/root");
    Check(empty!.Root == new string('0', 64) && empty.UserCount == 0);
    repository.Data = await new FileUserRepository(Path.Combine(AppContext.BaseDirectory, "data/user-10-example.json"))
        .LoadAsync(CancellationToken.None);
    await store.RefreshAsync();
    using var proof = JsonDocument.Parse(await client.GetStringAsync("/api/reserves/users/10/proof"));
    Check(proof.RootElement.GetProperty("proof").GetArrayLength() == 0);
    Verify(proof.RootElement);
});
await Test("Odd-sized tree proofs duplicate the last sibling at each necessary level", async () =>
{
    repository.Data = example with { Users = example.Users.Take(5).ToArray() };
    await store.RefreshAsync();
    foreach (var user in repository.Data.Users)
    {
        using var proof = JsonDocument.Parse(await client.GetStringAsync($"/api/reserves/users/{user.Id}/proof"));
        Verify(proof.RootElement);
    }
});
await Test("Saved 100k fixture has exact IDs, valid balances, and correct sampled proofs", async () =>
{
    var data = await new FileUserRepository(Path.Combine(AppContext.BaseDirectory, "data/users-100000.json"))
        .LoadAsync(CancellationToken.None);
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
    using var small = new SnapshotStore(new MutableRepository(example), new ReserveOptions { ProofCacheEntries = 1 });
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

await Test("Periodic refresh publishes changed data without a restart", async () =>
{
    var source = new MutableRepository(example);
    await using var timedApp = ReserveApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Reserve:RefreshInterval", "00:00:00.050", "--Logging:LogLevel:Default", "Warning"],
        services => services.AddSingleton<IUserRepository>(source));
    await timedApp.StartAsync();
    var timedStore = timedApp.Services.GetRequiredService<SnapshotStore>();
    source.Data = new UserDataSet("next", [new UserAccount(10, 100)]);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (timedStore.Current.Root.UserCount != 1)
        await Task.Delay(10, deadline.Token);
    Check(timedStore.Current.Root.DataVersion == "next");
    await timedApp.StopAsync();
});

string? redisConnection = Environment.GetEnvironmentVariable("RESERVE_TEST_REDIS");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    await Test("Redis adapter scans a versioned dataset and validates its manifest", async () =>
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var db = connection.GetDatabase();
        string suffix = Guid.NewGuid().ToString("N");
        string manifestKey = "por:test:" + suffix;
        string usersKey = "por:users:test:" + suffix;
        try
        {
            await db.HashSetAsync(usersKey, example.Users.Select(u => new HashEntry(u.Id, u.Balance)).ToArray());
            var options = new ReserveOptions { RedisConnection = redisConnection, RedisManifestKey = manifestKey };
            using var adapter = new RedisUserRepository(options);
            await db.StringSetAsync(manifestKey, JsonSerializer.Serialize(new RedisManifest("example", usersKey, 8)));
            var data = await adapter.LoadAsync(CancellationToken.None);
            Check(ReserveSnapshot.Build(data, CancellationToken.None).Root.Root == exampleRoot);
            await db.StringSetAsync(manifestKey, JsonSerializer.Serialize(new RedisManifest("bad", usersKey, 9)));
            await Throws<InvalidDataException>(() => adapter.LoadAsync(CancellationToken.None));
        }
        finally { await db.KeyDeleteAsync(new RedisKey[] { manifestKey, usersKey }); }
    });
}
else Console.WriteLine("SKIP Redis integration: set RESERVE_TEST_REDIS to an isolated test instance.");

await app.StopAsync();
Console.WriteLine($"Result: {passed} checks passed.");
return 0;

static void Check(bool condition)
{
    if (!condition) throw new Exception("Assertion failed.");
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static void Verify(JsonElement response)
{
    byte[] Hash(string tag, byte[] message)
    {
        byte[] prefix = SHA256.HashData(Encoding.UTF8.GetBytes(tag));
        return SHA256.HashData([.. prefix, .. prefix, .. message]);
    }
    int id = response.GetProperty("userId").GetInt32();
    long balance = response.GetProperty("userBalance").GetInt64();
    byte[] current = Hash("ProofOfReserve_Leaf", Encoding.UTF8.GetBytes(FormattableString.Invariant($"({id},{balance})")));
    foreach (var tuple in response.GetProperty("proof").EnumerateArray())
    {
        Check(tuple.GetArrayLength() == 2);
        string hex = tuple[0].GetString()!;
        Check(hex.Length == 64 && hex == hex.ToLowerInvariant());
        byte[] sibling = Convert.FromHexString(hex);
        int position = tuple[1].GetInt32();
        Check(position is 0 or 1);
        current = Hash("ProofOfReserve_Branch", position == 0 ? [.. sibling, .. current] : [.. current, .. sibling]);
    }
    Check(Convert.ToHexStringLower(current) == response.GetProperty("root").GetString());
}

internal sealed class MutableRepository(UserDataSet data) : IUserRepository
{
    public UserDataSet Data { get; set; } = data;
    public bool Fail { get; set; }
    public int Reads;
    public TaskCompletionSource? Gate { get; set; }
    public async Task<UserDataSet> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        if (Fail) throw new IOException("Simulated source failure.");
        if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
        return Data;
    }
}
