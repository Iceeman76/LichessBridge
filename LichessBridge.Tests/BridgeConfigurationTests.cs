using Bridge;

namespace LichessBridge.Tests;

[NonParallelizable]
public sealed class BridgeConfigurationTests
{
    private readonly Dictionary<string, string?> originalEnvironment = new();
    private string configurationDirectory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        configurationDirectory = Path.Combine(Path.GetTempPath(), $"LichessBridge-configuration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configurationDirectory);
        File.WriteAllText(Path.Combine(configurationDirectory, "appsettings.json"), """{"Engine":{"Name":"Shared engine"},"Logging":{"FilePath":"data/bridge.log"}}""");

        foreach (var variableName in new[] { "LICHESS_TOKEN", "Lichess__Token", "Engine__Name" })
        {
            originalEnvironment[variableName] = Environment.GetEnvironmentVariable(variableName);
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var (variableName, value) in originalEnvironment)
        {
            Environment.SetEnvironmentVariable(variableName, value);
        }

        originalEnvironment.Clear();
        Directory.Delete(configurationDirectory, recursive: true);
    }

    [Test]
    public void LoadsSettingsFromContentRootWithoutLocalFile()
    {
        var builder = BridgeConfiguration.CreateApplicationBuilder([], configurationDirectory);
        using var host = builder.Build();

        Assert.Multiple(() =>
        {
            Assert.That(builder.Environment.ContentRootPath, Is.EqualTo(configurationDirectory));
            Assert.That(builder.Configuration["Engine:Name"], Is.EqualTo("Shared engine"));
            Assert.That(builder.Configuration["Logging:FilePath"], Is.EqualTo("data/bridge.log"));
        });
    }

    [Test]
    public void LocalSettingsOverrideSharedSettingsAndRetainOtherValues()
    {
        File.WriteAllText(Path.Combine(configurationDirectory, "appsettings.Local.json"), """{"Engine":{"Name":"Local engine"}}""");
        var builder = BridgeConfiguration.CreateApplicationBuilder([], configurationDirectory);
        using var host = builder.Build();

        Assert.Multiple(() =>
        {
            Assert.That(builder.Configuration["Engine:Name"], Is.EqualTo("Local engine"));
            Assert.That(builder.Configuration["Logging:FilePath"], Is.EqualTo("data/bridge.log"));
        });
    }

    [Test]
    public void EnvironmentVariablesOverrideLocalSettings()
    {
        File.WriteAllText(Path.Combine(configurationDirectory, "appsettings.Local.json"), """{"Engine":{"Name":"Local engine"},"Lichess":{"Token":"local-test-token"}}""");
        Environment.SetEnvironmentVariable("Engine__Name", "Environment engine");
        Environment.SetEnvironmentVariable("Lichess__Token", "environment-test-token");
        var builder = BridgeConfiguration.CreateApplicationBuilder([], configurationDirectory);
        using var host = builder.Build();

        Assert.Multiple(() =>
        {
            Assert.That(builder.Configuration["Engine:Name"], Is.EqualTo("Environment engine"));
            Assert.That(builder.Configuration["Lichess:Token"], Is.EqualTo("environment-test-token"));
        });
    }

    [Test]
    public void LichessTokenAliasOverridesStandardEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("Lichess__Token", "environment-test-token");
        Environment.SetEnvironmentVariable("LICHESS_TOKEN", "alias-test-token");
        var builder = BridgeConfiguration.CreateApplicationBuilder([], configurationDirectory);
        using var host = builder.Build();

        Assert.That(builder.Configuration["Lichess:Token"], Is.EqualTo("alias-test-token"));
    }

    [Test]
    public void CommandLineOverridesLocalSettingsAndEnvironmentVariables()
    {
        File.WriteAllText(Path.Combine(configurationDirectory, "appsettings.Local.json"), """{"Engine":{"Name":"Local engine"}}""");
        Environment.SetEnvironmentVariable("Engine__Name", "Environment engine");
        Environment.SetEnvironmentVariable("LICHESS_TOKEN", "alias-test-token");
        var builder = BridgeConfiguration.CreateApplicationBuilder(["--Engine:Name", "Command line engine", "--Lichess:Token", "command-line-test-token"], configurationDirectory);
        using var host = builder.Build();

        Assert.Multiple(() =>
        {
            Assert.That(builder.Configuration["Engine:Name"], Is.EqualTo("Command line engine"));
            Assert.That(builder.Configuration["Lichess:Token"], Is.EqualTo("command-line-test-token"));
        });
    }
}
