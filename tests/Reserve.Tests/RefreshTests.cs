using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Reserve.Api;

namespace Reserve.Tests;

// Snapshot building, failure handling and the daily scheduler.
public class RefreshTests
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
    [SetCulture("ar-SA")]
    public async Task OrderingAndSerializationIgnoreCultureAndInputOrder()
    {
        Assert.That(new UserAccount(10, 100).Serialize(), Is.EqualTo("(10,100)"));

        server.Source.Data = example with { Users = example.Users.Reverse().ToArray() };
        await server.Cache.RefreshAsync();

        Assert.That(server.Cache.Current.Root.Root, Is.EqualTo(Fixtures.ExampleRoot));
    }

    [Test]
    public void FailedRefreshKeepsLastSnapshot()
    {
        var previous = server.Cache.Current;
        server.Source.Fail = true;

        Assert.ThrowsAsync<IOException>(() => server.Cache.RefreshAsync());
        Assert.That(server.Cache.Current, Is.SameAs(previous));
    }

    private static IEnumerable<TestCaseData> MalformedDatasets()
    {
        yield return new TestCaseData((object)new[] { new UserAccount(1, 1), new UserAccount(1, 2) })
            .SetName("Duplicate ID");
        yield return new TestCaseData((object)new[] { new UserAccount(0, 1) }).SetName("Zero ID");
        yield return new TestCaseData((object)new[] { new UserAccount(1, -1) }).SetName("Negative balance");
        yield return new TestCaseData((object)new UserAccount[] { null! }).SetName("Null user");
    }

    [TestCaseSource(nameof(MalformedDatasets))]
    public void MalformedDatasetCannotReplaceSnapshot(UserAccount[] users)
    {
        var previous = server.Cache.Current;
        server.Source.Data = new UserDataSet("invalid", users);

        Assert.ThrowsAsync<InvalidDataException>(() => server.Cache.RefreshAsync());
        Assert.That(server.Cache.Current, Is.SameAs(previous));
    }

    [Test]
    public async Task SmallProofCacheEvictsAndRegeneratesCorrectly()
    {
        var settings = server.Options;
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
            ProofVerifier.Verify(small.GetProofJson(small.Current, id)!);
        }
    }

    [Test]
    public async Task SlowRefreshServesOldSnapshotUntilReady()
    {
        var previous = server.Cache.Current;
        server.Source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = server.Cache.RefreshAsync();

        var root = await server.Client.GetFromJsonAsync<RootResponse>("/api/reserves/root");
        Assert.That(root!.SnapshotId, Is.EqualTo(previous.Root.SnapshotId));

        server.Source.Gate.SetResult();
        await refresh;

        Assert.That(server.Cache.Current.Root.SnapshotId, Is.Not.EqualTo(previous.Root.SnapshotId));
        Assert.That(server.Cache.Current.Root.Root, Is.EqualTo(Fixtures.ExampleRoot));
    }

    [Test]
    [CancelAfter(20_000)]
    public async Task ScheduledRefreshPublishesNewDataWithoutRestart(CancellationToken cancellationToken)
    {
        DateTimeOffset scheduled = DateTimeOffset.UtcNow.AddSeconds(5);
        await using var timed = await TestApp.StartAsync(
            example,
            "--Reserve:RefreshTimeUtc", scheduled.TimeOfDay.ToString("c", CultureInfo.InvariantCulture));
        Assert.That(timed.Source.Reads, Is.EqualTo(1));

        timed.Source.Data = new UserDataSet("next", [new UserAccount(1, 999), .. example.Users.Skip(1)]);
        var cache = timed.App.Services.GetRequiredService<ReserveCache>();
        while (cache.Current.Root.DataVersion != "next")
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.That(cache.Current.Root.GeneratedAtUtc, Is.GreaterThanOrEqualTo(scheduled),
            "The refresh must not run before its scheduled time.");
        Assert.That(cache.Current.Root.Root, Is.Not.EqualTo(Fixtures.ExampleRoot));
        Assert.That(timed.Source.Reads, Is.EqualTo(2));
    }

    [Test]
    [CancelAfter(20_000)]
    public async Task FailedScheduledRefreshIsRetriedUntilItSucceeds(CancellationToken cancellationToken)
    {
        DateTimeOffset scheduled = DateTimeOffset.UtcNow.AddSeconds(3);
        await using var timed = await TestApp.StartAsync(
            example,
            "--Reserve:RefreshTimeUtc", scheduled.TimeOfDay.ToString("c", CultureInfo.InvariantCulture),
            "--Reserve:RefreshRetryDelay", "00:00:00.1",
            "--Reserve:RefreshRetryMaxDelay", "00:00:00.2");
        var startup = timed.Cache.Current;
        timed.Source.Data = new UserDataSet("next", [new UserAccount(1, 999), .. example.Users.Skip(1)]);
        timed.Source.FailuresRemaining = 2;

        while (timed.Cache.Current.Root.DataVersion != "next")
        {
            if (timed.Source.Reads > 1)
            {
                Assert.That(timed.Cache.Current, Is.SameAs(startup).Or.Property("Root").Property("DataVersion")
                    .EqualTo("next"), "The old snapshot keeps serving while retries run.");
            }

            await Task.Delay(10, cancellationToken);
        }

        Assert.That(timed.Source.Reads, Is.EqualTo(4), "Startup load, two failed attempts, one successful retry.");
    }

    // Defaults: first retry after 1 minute, doubling, capped at 15 minutes.
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(3, 4)]
    [TestCase(4, 8)]
    [TestCase(5, 15)]
    [TestCase(6, 15)]
    [TestCase(1000, 15)]
    public void RetryDelayDoublesUpToTheCap(int failedAttempts, int expectedMinutes)
    {
        var now = DateTimeOffset.UtcNow;

        var delay = RefreshScheduler.GetRetryDelay(server.Options, failedAttempts, now, now.AddDays(1));

        Assert.That(delay, Is.EqualTo(TimeSpan.FromMinutes(expectedMinutes)));
    }

    [Test]
    public void RetriesStopBeforeTheNextScheduledRefresh()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.That(RefreshScheduler.GetRetryDelay(server.Options, 1, now, now.AddSeconds(30)), Is.Null);
    }

    [Test]
    public void ZeroRetryDelayTurnsRetriesOff()
    {
        var now = DateTimeOffset.UtcNow;
        server.Options.RefreshRetryDelay = TimeSpan.Zero;

        Assert.That(RefreshScheduler.GetRetryDelay(server.Options, 1, now, now.AddDays(1)), Is.Null);
    }
}
