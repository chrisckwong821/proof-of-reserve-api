using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Reserve.Api;

namespace Reserve.Tests;

public class ConfigurationTests
{
    [Test]
    public void ShippedAppSettingsAreValid()
    {
        Load().Validate();
    }

    [TestCase("--Reserve:ProofCacheEntries", "0", "ProofCacheEntries")]
    [TestCase("--Reserve:RefreshTimeUtc", "1.00:00:00", "RefreshTimeUtc")]
    [TestCase("--Reserve:RedisManifestKey", " ", "RedisManifestKey")]
    [TestCase("--Reserve:HeartbeatInterval", "-00:00:01", "HeartbeatInterval")]
    [TestCase("--Reserve:StaleGracePeriod", "1.00:00:00", "StaleGracePeriod")]
    [TestCase("--Reserve:RefreshRetryDelay", "-00:00:01", "RefreshRetryDelay")]
    [TestCase("--Reserve:RefreshRetryMaxDelay", "00:00:30", "RefreshRetryMaxDelay")]
    [TestCase("--Reserve:ManifestPollInterval", "-00:00:01", "ManifestPollInterval")]
    public async Task InvalidSettingStopsStartupBeforeLoadingData(string key, string value, string expectedInMessage)
    {
        var source = new MutableSource(await Fixtures.ExampleAsync());
        // Logging off: the host logs the expected startup failure with a full stack trace.
        await using var app = ReserveApplication.Build(
            ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "None", key, value], source.LoadAsync);

        var error = Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        Assert.That(error!.Message, Does.Contain(expectedInMessage));
        Assert.That(source.Reads, Is.Zero, "Data must not be loaded when the settings are invalid.");
    }

    [Test]
    public async Task EveryInvalidSettingIsReportedAtOnce()
    {
        await using var app = ReserveApplication.Build(
            [
                "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "None",
                "--Reserve:ProofCacheEntries", "0",
                "--Reserve:HeartbeatInterval", "-00:00:01",
                "--Reserve:RedisManifestKey", " "
            ],
            new MutableSource(await Fixtures.ExampleAsync()).LoadAsync);

        var error = Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        Assert.That(error!.Failures, Has.Count.EqualTo(3));
        Assert.That(error.Message,
            Does.Contain("ProofCacheEntries").And.Contain("HeartbeatInterval").And.Contain("RedisManifestKey"));
    }

    [TestCase("RefreshTimeUtc")]
    [TestCase("ProofCacheEntries")]
    [TestCase("RedisConnection")]
    [TestCase("RedisManifestKey")]
    [TestCase("RedisUserKeyPrefix")]
    public void MissingSettingIsRejected(string key)
    {
        var options = Load(new Dictionary<string, string?> { [$"Reserve:{key}"] = null });
        Assert.Throws(Is.InstanceOf<ArgumentException>(), options.Validate);
    }

    [TestCase(null)]
    [TestCase("-00:00:01")]
    [TestCase("1.00:00:00")]
    public void InvalidRefreshTimeIsRejected(string? value)
    {
        var options = Load();
        options.RefreshTimeUtc = value is null ? null : TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        Assert.Throws(Is.InstanceOf<ArgumentException>(), options.Validate);
    }

    // Columns: refresh time, current time, expected next refresh (all parsed as invariant culture).
    [TestCase("00:10:00", "2026-01-31T00:09:59Z", "2026-01-31T00:10:00Z")]
    [TestCase("00:10:00", "2026-01-31T00:10:00Z", "2026-02-01T00:10:00Z")]
    [TestCase("00:10:00", "2026-01-31T12:00:00Z", "2026-02-01T00:10:00Z")]
    [TestCase("00:10:00", "2026-01-31T09:09:59+09:00", "2026-01-31T00:10:00Z")]
    [TestCase("00:00:00", "2026-01-31T23:59:59Z", "2026-02-01T00:00:00Z")]
    public void NextRefreshHandlesBoundariesAndRollover(string refreshTime, string now, string expected)
    {
        var options = Load();
        options.RefreshTimeUtc = TimeSpan.Parse(refreshTime, CultureInfo.InvariantCulture);
        options.Validate();

        var next = options.GetNextRefreshUtc(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));

        Assert.That(next, Is.EqualTo(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture)));
    }

    // Read the same appsettings.json the server ships, optionally overriding values.
    private static ReserveOptions Load(Dictionary<string, string?>? overrides = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(overrides ?? [])
            .Build();
        return configuration.GetRequiredSection("Reserve").Get<ReserveOptions>()!;
    }
}
