using System.Globalization;
using System.Net;
using System.Text.Json;
using NUnit.Framework;
using Reserve.Api;

namespace Reserve.Tests;

// The /health endpoint and its staleness rule.
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

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
