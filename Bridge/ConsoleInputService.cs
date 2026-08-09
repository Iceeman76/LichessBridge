using LichessBridge.Status;
using Microsoft.Extensions.Hosting;

namespace Bridge;

internal sealed class ConsoleInputService(IQuitController quitController) : BackgroundService
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
            }

            await Task.Delay(100, stoppingToken);
        }
    }
}
