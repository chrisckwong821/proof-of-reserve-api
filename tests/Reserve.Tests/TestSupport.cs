using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Reserve.Api;

namespace Reserve.Tests;

// Shared fixtures and helpers. Fixture reading belongs to the tests, never the server.
public static class Fixtures
{
    public const string ExampleRoot = "b1231de33da17c23cebd80c104b88198e0914b0463d0e14db163605b904a7ba3";
    public const string LargeRoot = "347cfc213378db8331eef9b7fac2dccf625401a1fd1339b9f345432843221a9f";

    public static string PathOf(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    public static Task<UserDataSet> ExampleAsync() => LoadAsync("users-example.json");

    public static async Task<UserDataSet> LoadAsync(string fileName)
    {
        await using var stream = File.OpenRead(PathOf(fileName));
        var users = await JsonSerializer.DeserializeAsync<UserAccount[]>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The fixture must contain a JSON array.");
        return new UserDataSet(fileName, users);
    }
}

// A fresh in-process server per test, backed by a controllable in-memory source.
public sealed class TestApp : IAsyncDisposable
{
    private TestApp(WebApplication app, MutableSource source)
    {
        App = app;
        Source = source;
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    }

    public WebApplication App { get; }
    public MutableSource Source { get; }
    public HttpClient Client { get; }
    public ReserveCache Cache => App.Services.GetRequiredService<ReserveCache>();
    public ReserveOptions Options => App.Services.GetRequiredService<ReserveOptions>();

    // Extra args are passed as configuration, e.g. "--Reserve:RefreshTimeUtc", "12:00:00".
    public static async Task<TestApp> StartAsync(UserDataSet data, params string[] extraArgs)
    {
        var source = new MutableSource(data);
        string[] args = ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning", .. extraArgs];
        var app = ReserveApplication.Build(args, source);
        await app.StartAsync();
        return new TestApp(app, source);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

// Counts reads and simulates delayed or failed loads without changing production code.
public sealed class MutableSource(UserDataSet data) : IUserDataSource
{
    private int reads;

    public UserDataSet Data { get; set; } = data;

    // Fail this many upcoming loads, then succeed; int.MaxValue means keep failing.
    public int FailuresRemaining { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    public int Reads => Volatile.Read(ref reads);

    // Manifest-version checks made by ManifestPoller, and how many upcoming checks should fail.
    public int VersionReads { get; private set; }
    public int VersionFailuresRemaining { get; set; }

    public Task<string> GetManifestVersionAsync(CancellationToken cancellationToken)
    {
        VersionReads++;
        if (VersionFailuresRemaining > 0)
        {
            VersionFailuresRemaining--;
            throw new IOException("Simulated manifest read failure.");
        }

        return Task.FromResult(Data.Version);
    }

    public async Task<UserDataSet> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref reads);
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new IOException("Simulated source failure.");
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }

        return Data;
    }
}

// Independently rebuilds the root from a proof response, without calling the production Merkle library.
public static class ProofVerifier
{
    public static void Verify(JsonElement response)
    {
        int id = response.GetProperty("userId").GetInt32();
        long balance = response.GetProperty("userBalance").GetInt64();
        byte[] current = Hash(
            "ProofOfReserve_Leaf",
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"({id},{balance})")));

        foreach (var tuple in response.GetProperty("proof").EnumerateArray())
        {
            Assert.That(tuple.GetArrayLength(), Is.EqualTo(2), "Each proof step is [hash, position].");
            string hex = tuple[0].GetString()!;
            Assert.That(hex, Has.Length.EqualTo(64).And.EqualTo(hex.ToLowerInvariant()), "Sibling hash format");
            int position = tuple[1].GetInt32();
            Assert.That(position, Is.AnyOf(0, 1), "Sibling position");

            byte[] sibling = Convert.FromHexString(hex);
            current = Hash(
                "ProofOfReserve_Branch",
                position == 0 ? [.. sibling, .. current] : [.. current, .. sibling]);
        }

        Assert.That(
            Convert.ToHexStringLower(current),
            Is.EqualTo(response.GetProperty("root").GetString()),
            $"Proof for user {id} does not rebuild the root.");
    }

    public static void Verify(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        Verify(document.RootElement);
    }

    public static async Task VerifyAsync(HttpClient client, int userId)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/reserves/users/{userId}/proof"));
        Verify(document.RootElement);
    }

    // BIP340-style tagged SHA-256: SHA256(SHA256(tag) || SHA256(tag) || message).
    private static byte[] Hash(string tag, byte[] message)
    {
        byte[] prefix = SHA256.HashData(Encoding.UTF8.GetBytes(tag));
        return SHA256.HashData([.. prefix, .. prefix, .. message]);
    }
}
