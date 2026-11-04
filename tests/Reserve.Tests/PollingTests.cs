using NUnit.Framework;
using Reserve.Api;

namespace Reserve.Tests;

// ManifestPoller: picking up data published during the day.
public class PollingTests
{
    private UserDataSet example = null!;

    [SetUp]
    public async Task LoadExample() => example = await Fixtures.ExampleAsync();

    [Test]
    [CancelAfter(10_000)]
    public async Task NewManifestVersionIsServedWithoutWaitingForTheDailyRefresh(CancellationToken cancellationToken)
    {
        await using var server = await TestApp.StartAsync(example, "--Reserve:ManifestPollInterval", "00:00:00.05");
        server.Source.Data = new UserDataSet("midday", [new UserAccount(1, 999), .. example.Users.Skip(1)]);

        while (server.Cache.Current.Root.DataVersion != "midday")
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.That(server.Source.Reads, Is.EqualTo(2), "Startup load plus one load for the new version.");
        await ProofVerifier.VerifyAsync(server.Client, 1);
    }

    [Test]
    public async Task UnchangedVersionDoesNotReloadData()
    {
        await using var server = await TestApp.StartAsync(example, "--Reserve:ManifestPollInterval", "00:00:00.05");

        await Task.Delay(500);

        Assert.That(server.Source.VersionReads, Is.GreaterThan(2), "The poller kept checking.");
        Assert.That(server.Source.Reads, Is.EqualTo(1), "Only the startup load; no reload for an unchanged version.");
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task FailedPollKeepsServingAndRecovers(CancellationToken cancellationToken)
    {
        await using var server = await TestApp.StartAsync(example, "--Reserve:ManifestPollInterval", "00:00:00.05");
        var startup = server.Cache.Current;
        server.Source.VersionFailuresRemaining = 3;
        server.Source.Data = new UserDataSet("midday", example.Users);

        while (server.Cache.Current.Root.DataVersion != "midday")
        {
            if (server.Source.VersionFailuresRemaining > 0)
            {
                Assert.That(server.Cache.Current, Is.SameAs(startup), "The current snapshot serves while polls fail.");
            }

            await Task.Delay(10, cancellationToken);
        }

        Assert.That(server.Source.VersionReads, Is.GreaterThanOrEqualTo(4));
    }

    [Test]
    public async Task PollingIsOffByDefault()
    {
        await using var server = await TestApp.StartAsync(example);

        await Task.Delay(300);

        Assert.That(server.Source.VersionReads, Is.Zero);
    }
}
