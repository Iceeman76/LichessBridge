using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetLab.LichessBridge.Engine;

public sealed class EngineStartupValidator(IChessEngineFactory engineFactory, ILogger<EngineStartupValidator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Validating chess engine startup and WB/2 handshake");
        await using var engine = engineFactory.Create();
        await engine.StartAsync(cancellationToken);
        await engine.StopAsync(cancellationToken);
        logger.LogInformation("Chess engine startup validation succeeded");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
