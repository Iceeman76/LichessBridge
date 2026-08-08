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
        services.AddOptions<LichessOptions>()
            .Bind(configuration.GetSection(LichessOptions.SectionName))
            .Validate(options => string.IsNullOrWhiteSpace(options.Token) is false, "A Lichess token is required.")
            .Validate(options => options.ChallengeFilter.Modes.Length > 0 && options.ChallengeFilter.Modes.All(mode => mode.Equals("casual", StringComparison.OrdinalIgnoreCase) || mode.Equals("rated", StringComparison.OrdinalIgnoreCase)), "ChallengeFilter modes must contain casual, rated, or both.")
            .Validate(options => options.ChallengeFilter.Opponents.Length > 0 && options.ChallengeFilter.Opponents.All(opponent => opponent.Equals("human", StringComparison.OrdinalIgnoreCase) || opponent.Equals("bot", StringComparison.OrdinalIgnoreCase)), "ChallengeFilter opponents must contain human, bot, or both.")
            .ValidateOnStart();
        services.AddOptions<EngineOptions>()
            .Bind(configuration.GetSection(EngineOptions.SectionName))
            .ValidateDataAnnotations()
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
        services.AddSingleton<IStatusDisplay, ConsoleStatusDisplay>();
        services.AddHostedService<EngineStartupValidator>();
        services.AddHostedService<LichessBridgeService>();

        return services;
    }
}
