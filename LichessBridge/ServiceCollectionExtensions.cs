using LichessBridge.Configuration;
using LichessBridge.Engine;
using LichessBridge.Game;
using LichessBridge.Lichess;
using LichessBridge.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LichessBridge;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLichessBridge(this IServiceCollection services, IConfiguration configuration)
    {
        var configuredModes = configuration.GetSection($"{LichessOptions.SectionName}:ChallengeFilter:Modes").Get<string[]>();
        var configuredOpponents = configuration.GetSection($"{LichessOptions.SectionName}:ChallengeFilter:Opponents").Get<string[]>();
        var configuredVariants = configuration.GetSection($"{LichessOptions.SectionName}:ChallengeFilter:Variants").Get<string[]>();
        var configuredSpeeds = configuration.GetSection($"{LichessOptions.SectionName}:ChallengeFilter:Speeds").Get<string[]>();

        services.AddOptions<LichessOptions>()
            .Bind(configuration.GetSection(LichessOptions.SectionName))
            .PostConfigure(options =>
            {
                options.ChallengeFilter.Modes = configuredModes ?? options.ChallengeFilter.Modes;
                options.ChallengeFilter.Opponents = configuredOpponents ?? options.ChallengeFilter.Opponents;
                options.ChallengeFilter.Variants = configuredVariants ?? options.ChallengeFilter.Variants;
                options.ChallengeFilter.Speeds = configuredSpeeds ?? options.ChallengeFilter.Speeds;
            })
            .ValidateDataAnnotations()
            .Validate(options => string.IsNullOrWhiteSpace(options.Token) is false, "A Lichess token is required.")
            .Validate(options => options.ChallengeFilter.Modes.Length > 0 && options.ChallengeFilter.Modes.All(mode => mode.Equals("casual", StringComparison.OrdinalIgnoreCase) || mode.Equals("rated", StringComparison.OrdinalIgnoreCase)), "ChallengeFilter modes must contain casual, rated, or both.")
            .Validate(options => options.ChallengeFilter.Opponents.Length > 0 && options.ChallengeFilter.Opponents.All(opponent => opponent.Equals("human", StringComparison.OrdinalIgnoreCase) || opponent.Equals("bot", StringComparison.OrdinalIgnoreCase)), "ChallengeFilter opponents must contain human, bot, or both.")
            .Validate(options => options.ChallengeFilter.MinBaseSeconds is null || options.ChallengeFilter.MaxBaseSeconds is null || options.ChallengeFilter.MinBaseSeconds <= options.ChallengeFilter.MaxBaseSeconds, "ChallengeFilter MinBaseSeconds cannot exceed MaxBaseSeconds.")
            .Validate(options => options.ChallengeFilter.MinIncrementSeconds is null || options.ChallengeFilter.MaxIncrementSeconds is null || options.ChallengeFilter.MinIncrementSeconds <= options.ChallengeFilter.MaxIncrementSeconds, "ChallengeFilter MinIncrementSeconds cannot exceed MaxIncrementSeconds.")
            .Validate(options => options.ChallengeFilter.MinRating is null || options.ChallengeFilter.MaxRating is null || options.ChallengeFilter.MinRating <= options.ChallengeFilter.MaxRating, "ChallengeFilter MinRating cannot exceed MaxRating.")
            .ValidateOnStart();
        services.AddOptions<EngineOptions>()
            .Bind(configuration.GetSection(EngineOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<AutoBotChallengeOptions>()
            .Bind(configuration.GetSection(AutoBotChallengeOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.Enabled is false || options.Profiles.Any(profile => profile.Enabled), "AutoBotChallenge requires at least one enabled profile when enabled.")
            .Validate(options => options.Profiles.All(profile => profile.Mode.Equals("casual", StringComparison.OrdinalIgnoreCase) || profile.Mode.Equals("rated", StringComparison.OrdinalIgnoreCase)), "AutoBotChallenge profile modes must be casual or rated.")
            .Validate(options => options.Profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == options.Profiles.Length, "AutoBotChallenge profile names must be unique.")
            .Validate(options => options.Profiles.All(profile => profile.InitialSeconds > 0 || profile.IncrementSeconds > 0), "AutoBotChallenge profiles require a positive initial time or increment.")
            .Validate(options => options.MinRating is null || options.MaxRating is null || options.MinRating <= options.MaxRating, "AutoBotChallenge MinRating cannot exceed MaxRating.")
            .Validate(options => options.Profiles.All(profile => (profile.MinRating ?? options.MinRating) is not int minimum || (profile.MaxRating ?? options.MaxRating) is not int maximum || minimum <= maximum), "AutoBotChallenge profile MinRating cannot exceed its effective MaxRating.")
            .ValidateOnStart();
        services.AddOptions<GamesOptions>()
            .Bind(configuration.GetSection(GamesOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<ChatOptions>()
            .Bind(configuration.GetSection(ChatOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<ILichessClient, LichessClient>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IChallengeFilter, ChallengeFilter>();
        services.AddSingleton<IChessEngineFactory, WinBoardEngineFactory>();
        services.AddSingleton<IGameController, GameController>();
        services.AddSingleton<IQuitController, QuitController>();
        services.AddSingleton<IAutoChallengeController>(serviceProvider => new AutoChallengeController(serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AutoBotChallengeOptions>>().Value.Enabled));
        services.AddHostedService<EngineStartupValidator>();
        services.AddHostedService<LichessBridgeService>();

        return services;
    }
}
