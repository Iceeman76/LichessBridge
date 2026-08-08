using LichessBridge.Configuration;
using LichessBridge.Engine;
using LichessBridge.Game;
using LichessBridge.Lichess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LichessBridge;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLichessBridge(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<LichessOptions>()
            .Bind(configuration.GetSection(LichessOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Token), "A Lichess token is required.")
            .ValidateOnStart();
        services.AddOptions<EngineOptions>()
            .Bind(configuration.GetSection(EngineOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<GamesOptions>()
            .Bind(configuration.GetSection(GamesOptions.SectionName))
            .ValidateOnStart();

        services.AddHttpClient<ILichessClient, LichessClient>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IChallengeFilter, ChallengeFilter>();
        services.AddSingleton<IChessEngineFactory, WinBoardEngineFactory>();
        services.AddSingleton<IGameController, GameController>();
        services.AddHostedService<LichessBridgeService>();
        return services;
    }
}
