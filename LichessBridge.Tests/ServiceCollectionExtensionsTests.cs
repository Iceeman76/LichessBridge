using DotNetLab.LichessBridge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotNetLab.LichessBridge.Tests;

public sealed class ServiceCollectionExtensionsTests
{
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
