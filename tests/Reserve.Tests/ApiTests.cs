using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Reserve.Api;

namespace Reserve.Tests;

// Each test gets its own server loaded with the eight-user example.
public class ApiTests
{
    private UserDataSet example = null!;
    private TestApp server = null!;

    [SetUp]
    public async Task StartServer()
    {
        example = await Fixtures.ExampleAsync();
        server = await TestApp.StartAsync(example);
    }

    [TearDown]
    public async Task StopServer() => await server.DisposeAsync();

    [Test]
    public async Task RootMatchesKnownAnswer()
    {
        var root = await server.Client.GetFromJsonAsync<RootResponse>("/api/reserves/root");

        Assert.That(root!.Root, Is.EqualTo(Fixtures.ExampleRoot));
        Assert.That(root.UserCount, Is.EqualTo(8));
    }

    [Test]
    public async Task EveryProofHasCorrectBalanceAndRebuildsRoot()
    {
        foreach (var user in example.Users)
        {
            using var proof = JsonDocument.Parse(
                await server.Client.GetStringAsync($"/api/reserves/users/{user.Id}/proof"));

            Assert.That(proof.RootElement.GetProperty("userBalance").GetInt64(), Is.EqualTo(user.Balance));
            ProofVerifier.Verify(proof.RootElement);
        }
    }

    // Pins the exact response bytes (the README example) so a refactor cannot change the wire format.
    [Test]
    public async Task ProofJsonMatchesDocumentedFormatExactly()
    {
        string body = await server.Client.GetStringAsync("/api/reserves/users/3/proof");
        string snapshotId = server.Cache.Current.Root.SnapshotId;

        Assert.That(body, Is.EqualTo(
            "{\"snapshotId\":\"" + snapshotId + "\"," +
            "\"root\":\"" + Fixtures.ExampleRoot + "\"," +
            "\"userId\":3,\"userBalance\":3333,\"proof\":[" +
            "[\"8520072399ad3462db395a7a9803c6fe3f4143d502a0eb145e6c69ba7ec6d22d\",1]," +
            "[\"99fb04c9b8fd37e66b2dde367d91f2c930b2ab162dbbf7298e9313c309c7925f\",0]," +
            "[\"9d7f79fa8e788d4a32c9c674b67dcfaf0885f539ac2699129e3c4d88c11c76e7\",1]]}"));
    }

    [TestCase("/api/reserves/root")]
    [TestCase("/api/reserves/users/1/proof")]
    public async Task ResponsesAreCachedAndSupportConditionalGet(string path)
    {
        using var first = await server.Client.GetAsync(path);
        using var second = await server.Client.GetAsync(path);
        Assert.That(
            await second.Content.ReadAsStringAsync(),
            Is.EqualTo(await first.Content.ReadAsStringAsync()));

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var revalidated = await server.Client.SendAsync(request);

        Assert.That(revalidated.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(server.Source.Reads, Is.EqualTo(1), "Requests must not reread the data source.");
    }

    [Test]
    public void ProofJsonIsReusedFromCache()
    {
        var snapshot = server.Cache.Current;

        Assert.That(server.Cache.GetProofJson(snapshot, 1), Is.SameAs(server.Cache.GetProofJson(snapshot, 1)));
    }

    [Test]
    public async Task ConcurrentRequestsDoNotRereadTheSource()
    {
        await Task.WhenAll(Enumerable.Range(0, 64).Select(async i =>
        {
            string path = i % 2 == 0 ? "/api/reserves/root" : $"/api/reserves/users/{i % 8 + 1}/proof";
            using var response = await server.Client.GetAsync(path);
            Assert.That(response.IsSuccessStatusCode, $"{path} returned {response.StatusCode}");
        }));

        Assert.That(server.Source.Reads, Is.EqualTo(1));
    }

    // Pins status, content type and exact body of each error response.
    [TestCase("/api/reserves/users/999/proof", HttpStatusCode.NotFound, "{\"error\":\"User not found.\"}")]
    [TestCase("/api/reserves/users/0/proof", HttpStatusCode.BadRequest, "{\"error\":\"User ID must be positive.\"}")]
    public async Task UnknownAndInvalidIdsAreRejected(string path, HttpStatusCode expected, string expectedBody)
    {
        using var response = await server.Client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(expected));
        Assert.That(response.Content.Headers.ContentType?.ToString(), Is.EqualTo("application/json; charset=utf-8"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(expectedBody));
    }

    [Test]
    public async Task PinnedOldSnapshotReturnsConflictWithCurrentId()
    {
        using var response = await server.Client.GetAsync("/api/reserves/users/1/proof?snapshotId=old");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(response.Content.Headers.ContentType?.ToString(), Is.EqualTo("application/json; charset=utf-8"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(
            "{\"error\":\"Snapshot changed; fetch the current root and retry.\"," +
            "\"snapshotId\":\"" + server.Cache.Current.Root.SnapshotId + "\"}"));
    }

    [Test]
    public async Task RefreshClearsProofsOfTheReplacedSnapshot()
    {
        var previous = server.Cache.Current;
        server.Cache.GetProofJson(previous, 1, out _);
        server.Cache.GetProofJson(previous, 1, out bool cachedBefore);
        Assert.That(cachedBefore, Is.True);

        await server.Cache.RefreshAsync();

        server.Cache.GetProofJson(previous, 1, out bool cachedAfter);
        Assert.That(cachedAfter, Is.False, "Old-snapshot proofs must not keep using cache space.");
    }

    [Test]
    public async Task RefreshReplacesCachesAndRejectsOldSnapshotPins()
    {
        var previous = server.Cache.Current;
        byte[] oldProof = server.Cache.GetProofJson(previous, 1)!;

        server.Source.Data = new UserDataSet("updated", [new UserAccount(1, 999), .. example.Users.Skip(1)]);
        await server.Cache.RefreshAsync();

        Assert.That(server.Cache.Current.Root.Root, Is.Not.EqualTo(previous.Root.Root));
        Assert.That(server.Cache.GetProofJson(server.Cache.Current, 1), Is.Not.SameAs(oldProof));

        using var pinned = await server.Client.GetAsync(
            $"/api/reserves/users/1/proof?snapshotId={previous.Root.SnapshotId}");
        Assert.That(pinned.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        using var proof = JsonDocument.Parse(await server.Client.GetStringAsync("/api/reserves/users/1/proof"));
        Assert.That(proof.RootElement.GetProperty("userBalance").GetInt64(), Is.EqualTo(999));
        ProofVerifier.Verify(proof.RootElement);
        ProofVerifier.Verify(oldProof);
    }

    [Test]
    public async Task OddSizedTreeProofsAreValid()
    {
        server.Source.Data = example with { Users = example.Users.Take(5).ToArray() };
        await server.Cache.RefreshAsync();

        foreach (var user in server.Source.Data.Users)
        {
            await ProofVerifier.VerifyAsync(server.Client, user.Id);
        }
    }

    [Test]
    public async Task EmptyDatasetReturnsZeroRoot()
    {
        server.Source.Data = new UserDataSet("empty", []);
        await server.Cache.RefreshAsync();

        var root = await server.Client.GetFromJsonAsync<RootResponse>("/api/reserves/root");

        Assert.That(root!.Root, Is.EqualTo(new string('0', 64)));
        Assert.That(root.UserCount, Is.Zero);
    }

    [Test]
    public async Task LargeFixtureHasExpectedContentAndValidProofs()
    {
        var data = await Fixtures.LoadAsync("users-100000.json");
        Assert.That(data.Users.Select(u => u.Id), Is.EqualTo(Enumerable.Range(1, 100_000)));
        Assert.That(data.Users.Select(u => u.Balance), Is.All.InRange(0L, 999_999L));

        server.Source.Data = data;
        await server.Cache.RefreshAsync();

        Assert.That(server.Cache.Current.Root.UserCount, Is.EqualTo(100_000));
        Assert.That(server.Cache.Current.Root.Root, Is.EqualTo(Fixtures.LargeRoot));
        foreach (int id in new[] { 1, 2, 3, 32768, 65535, 99999, 100000 })
        {
            await ProofVerifier.VerifyAsync(server.Client, id);
        }
    }
}
