using LichessBridge.Game;
using LichessBridge.Status;
using Microsoft.Extensions.Hosting;

namespace Bridge;

internal sealed class ConsoleInputService(IQuitController quitController, IAutoChallengeController autoChallengeController, IGameController gameController, IStatusDisplay statusDisplay) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        while (stoppingToken.IsCancellationRequested is false)
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

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
                    statusDisplay.ShowResign(result is ResignationRequestResult.ConfirmationRequested);

                    if (result is ResignationRequestResult.Failed)
                    {
                        statusDisplay.Show("Could not resign game.");
                    }
                }
            }

            await Task.Delay(100, stoppingToken);
        }
    }
}
