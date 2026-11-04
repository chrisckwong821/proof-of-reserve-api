using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Reserve.Api;

namespace Reserve.Tests;

// The /health endpoint, request counters and the heartbeat log line.
public class ObservabilityTests
{
    private TestApp server = null!;

    [SetUp]
    public async Task StartServer() => server = await TestApp.StartAsync(await Fixtures.ExampleAsync());

    [TearDown]
    public async Task StopServer() => await server.DisposeAsync();

    [Test]
    public async Task HealthReportsCurrentSnapshot()
    {
        using var response = await server.Client.GetAsync("/health");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
        var data = body.RootElement.GetProperty("checks").GetProperty("snapshot").GetProperty("data");
        Assert.That(data.GetProperty("snapshotId").GetString(), Is.EqualTo(server.Cache.Current.Root.SnapshotId));
        Assert.That(data.GetProperty("userCount").GetInt32(), Is.EqualTo(8));
    }

    // Refresh is daily at 00:10 UTC with a 30-minute grace period.
    [TestCase("2026-01-31T00:10:05Z", "2026-01-31T12:00:00Z", false, TestName = "Refreshed today: healthy")]
    [TestCase("2026-01-31T00:05:00Z", "2026-01-31T00:09:00Z", false, TestName = "Started before today's refresh")]
    [TestCase("2026-01-30T00:10:05Z", "2026-01-31T00:20:00Z", false, TestName = "Refresh due, within grace")]
    [TestCase("2026-01-30T00:10:05Z", "2026-01-31T00:41:00Z", true, TestName = "Refresh missed: stale")]
    [TestCase("2026-01-29T00:10:05Z", "2026-01-31T00:05:00Z", true, TestName = "Two days old: stale")]
    public void StalenessFollowsTheDailySchedule(string generatedAt, string now, bool expectedStale)
    {
        var options = server.Options;
        options.RefreshTimeUtc = TimeSpan.FromMinutes(10);
        options.StaleGracePeriod = TimeSpan.FromMinutes(30);
        var root = server.Cache.Current.Root with { GeneratedAtUtc = Parse(generatedAt) };

        var (stale, _) = SnapshotHealthCheck.Evaluate(root, options, Parse(now));

        Assert.That(stale, Is.EqualTo(expectedStale));
    }

    [Test]
    public async Task RequestStatsCountCacheHitsNewProofsAndRejections()
    {
        using var root = await server.Client.GetAsync("/api/reserves/root");
        await RevalidateAsync("/api/reserves/root", root);
        using var first = await server.Client.GetAsync("/api/reserves/users/1/proof");
        (await server.Client.GetAsync("/api/reserves/users/1/proof")).Dispose();
        await RevalidateAsync("/api/reserves/users/1/proof", first);
        (await server.Client.GetAsync("/api/reserves/users/999/proof")).Dispose();
        (await server.Client.GetAsync("/api/reserves/users/0/proof")).Dispose();

        var counts = server.App.Services.GetRequiredService<RequestStats>().TakeAndReset();

        Assert.That(counts, Is.EqualTo(new RequestCounts(
            Root: 2, RootNotModified: 1,
            ProofFromCache: 2, ProofBuilt: 1, ProofNotModified: 1,
            ProofNotFound: 1, ProofRejected: 1)));
        Assert.That(
            server.App.Services.GetRequiredService<RequestStats>().TakeAndReset(),
            Is.EqualTo(default(RequestCounts)),
            "Counts reset after each heartbeat interval.");
    }

    [Test]
    public async Task HeartbeatLogsSnapshotAndRequestCounts()
    {
        (await server.Client.GetAsync("/api/reserves/users/1/proof")).Dispose();
        var logger = new ListLogger<HeartbeatService>();

        Heartbeat(logger).WriteHeartbeat(DateTimeOffset.UtcNow);

        var (level, message) = logger.Entries.Single();
        Assert.That(level, Is.EqualTo(LogLevel.Information));
        Assert.That(message, Does.StartWith("Heartbeat: healthy"));
        Assert.That(message, Does.Contain(server.Cache.Current.Root.SnapshotId).And.Contain("1 newly built"));
    }

    [Test]
    public void HeartbeatWarnsWhenSnapshotIsStale()
    {
        var logger = new ListLogger<HeartbeatService>();

        Heartbeat(logger).WriteHeartbeat(DateTimeOffset.UtcNow.AddDays(2));

        var (level, message) = logger.Entries.Single();
        Assert.That(level, Is.EqualTo(LogLevel.Warning));
        Assert.That(message, Does.StartWith("Heartbeat: STALE"));
    }

    private HeartbeatService Heartbeat(ILogger<HeartbeatService> logger) => new(
        server.Options, server.Cache, server.App.Services.GetRequiredService<RequestStats>(), logger);

    private async Task RevalidateAsync(string path, HttpResponseMessage previous)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(previous.Headers.ETag!);
        using var response = await server.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
