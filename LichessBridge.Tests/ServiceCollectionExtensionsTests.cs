using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [TestCase(10000, 15000, true)]
    [TestCase(10000, 10000, false)]
    [TestCase(-1, 15000, false)]
    [TestCase(0, 0, true)]
    public void ValidatesConsoleQuietThresholds(int quiet, int resume, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:ConsoleQuietBelowMilliseconds"] = quiet.ToString(), ["Logging:ConsoleResumeAboveMilliseconds"] = resume.ToString() }).Build();
        var services = new ServiceCollection();
        services.AddLichessBridge(configuration);
        using var provider = services.BuildServiceProvider();

        if (valid)
        {
            Assert.That(provider.GetRequiredService<IOptions<ConsoleOutputOptions>>().Value.ConsoleQuietBelowMilliseconds, Is.EqualTo(quiet));
        }
        else
        {
            Assert.Throws<OptionsValidationException>(() => _ = provider.GetRequiredService<IOptions<ConsoleOutputOptions>>().Value);
        }
    }

    [TestCase(null, typeof(WinBoardEngine))]
    [TestCase("WinBoard", typeof(WinBoardEngine))]
    [TestCase("Uci", typeof(UciEngine))]
    public async Task CreatesEngineUsingConfiguredProtocol(string? protocol, Type expectedType)
    {
        var settings = new Dictionary<string, string?> { ["Engine:Path"] = "local-engine", ["Engine:Uci:Options:Hash"] = "128" };

        if (protocol is not null)
        {
            settings["Engine:Protocol"] = protocol;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLichessBridge(configuration);
        using var provider = services.BuildServiceProvider();
        await using var engine = provider.GetRequiredService<IChessEngineFactory>().Create();

        Assert.Multiple(() =>
        {
            Assert.That(engine, Is.TypeOf(expectedType));
            Assert.That(provider.GetRequiredService<IOptions<EngineOptions>>().Value.Uci.Options["Hash"], Is.EqualTo("128"));
        });
    }

    [TestCase("Engine:Protocol", "999")]
    [TestCase("Engine:Uci:ResponseTimeoutSeconds", "0")]
    [TestCase("Engine:StartupCommands:0", "go")]
    public void RejectsInvalidUciConfiguration(string key, string value)
    {
        var settings = new Dictionary<string, string?> { ["Engine:Path"] = "local-engine", ["Engine:Protocol"] = "Uci", [key] = value };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLichessBridge(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => _ = provider.GetRequiredService<IOptions<EngineOptions>>().Value);
    }

    [Test]
    public void UsesExactlyConfiguredChallengeFilterLists()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Lichess:Token"] = "test-token",
            ["Lichess:ChallengeFilter:Modes:0"] = "rated",
            ["Lichess:ChallengeFilter:Opponents:0"] = "bot",
            ["Lichess:ChallengeFilter:Variants:0"] = "chess960",
            ["Lichess:ChallengeFilter:Speeds:0"] = "bullet"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddLichessBridge(configuration);

        using var provider = services.BuildServiceProvider();
        var filter = provider.GetRequiredService<IOptions<LichessOptions>>().Value.ChallengeFilter;

        Assert.Multiple(() =>
        {
            Assert.That(filter.Modes, Is.EqualTo(["rated"]));
            Assert.That(filter.Opponents, Is.EqualTo(["bot"]));
            Assert.That(filter.Variants, Is.EqualTo(["chess960"]));
            Assert.That(filter.Speeds, Is.EqualTo(["bullet"]));
        });
    }
}
