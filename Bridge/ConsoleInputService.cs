using DotNetLab.LichessBridge.Game;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Hosting;

namespace DotNetLab.Bridge;

internal sealed class ConsoleInputService(IQuitController quitController, IAutoChallengeController autoChallengeController, IGameController gameController, IStatusDisplay statusDisplay) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        var finalConfirmationRequested = false;

        while (stoppingToken.IsCancellationRequested is false)
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                if (finalConfirmationRequested)
                {
                    finalConfirmationRequested = false;
                    var result = await gameController.ConfirmResignationAsync(key.Key is ConsoleKey.Y, stoppingToken);
                    statusDisplay.ShowResign(result);

                    if (result is ResignationRequestResult.Failed)
                    {
                        statusDisplay.Show("Could not resign game.");
                    }

                    continue;
                }

                if (key.Key is ConsoleKey.Q)
                {
                    quitController.Toggle();
                }

                if (key.Key is ConsoleKey.A)
                {
                    autoChallengeController.Toggle();
                }

                if (key.Key is ConsoleKey.C)
                {
                    autoChallengeController.RequestChallenge();
                }

                if (key.Key is ConsoleKey.R)
                {
                    var result = await gameController.RequestResignationAsync(stoppingToken);
                    finalConfirmationRequested = result is ResignationRequestResult.FinalConfirmationRequested;
                    statusDisplay.ShowResign(result);
                }
            }

            await Task.Delay(100, stoppingToken);
        }
    }
}
