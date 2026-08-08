using LichessBridge.Game;
using LichessBridge.Lichess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LichessBridge;

public sealed class LichessBridgeService(
    ILichessClient lichess,
    IChallengeFilter challengeFilter,
    IGameController gameController,
    ILogger<LichessBridgeService> logger) : BackgroundService
{
    private int _busy;
    private Task? _activeGame;
    private LichessAccount? _account;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _account = await lichess.GetAccountAsync(stoppingToken);
        logger.LogInformation("Connected to Lichess as {Username}", _account.Username);
        logger.LogInformation("Waiting for challenges...");

        var retry = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var accountEvent in lichess.StreamAccountEventsAsync(stoppingToken))
                {
                    retry = TimeSpan.FromSeconds(1);
                    await HandleEventAsync(accountEvent, stoppingToken);
                }

                throw new EndOfStreamException("Lichess account event stream ended.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Account event stream disconnected; reconnecting in {Delay}", retry);
                await Task.Delay(retry, stoppingToken);
                retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, 30));
            }
        }

        if (_activeGame is not null)
            await _activeGame.WaitAsync(CancellationToken.None);
    }

    private async Task HandleEventAsync(AccountEvent accountEvent, CancellationToken cancellationToken)
    {
        switch (accountEvent)
        {
            case ChallengeEvent received:
                await HandleChallengeAsync(received.Challenge, cancellationToken);
                break;
            case GameStartEvent started:
                StartGame(started.GameId, cancellationToken);
                break;
            case GameFinishEvent finished:
                logger.LogDebug("Received gameFinish for {GameId}", finished.GameId);
                break;
        }
    }

    private async Task HandleChallengeAsync(LichessChallenge challenge, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Challenge {ChallengeId} from {Challenger}: {Mode} {Variant} {Speed}",
            challenge.Id,
            challenge.Challenger,
            challenge.Rated ? "rated" : "casual",
            challenge.Variant,
            challenge.Speed);

        var decision = challengeFilter.Evaluate(challenge, Volatile.Read(ref _busy) == 1);
        if (!decision.Accept)
        {
            await lichess.DeclineChallengeAsync(challenge.Id, decision.DeclineReason!, cancellationToken);
            logger.LogInformation("Challenge {ChallengeId} declined: {Reason}", challenge.Id, decision.DeclineReason);
            return;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            await lichess.DeclineChallengeAsync(challenge.Id, "later", cancellationToken);
            return;
        }

        try
        {
            await lichess.AcceptChallengeAsync(challenge.Id, cancellationToken);
            logger.LogInformation("Challenge {ChallengeId} accepted", challenge.Id);
        }
        catch
        {
            Interlocked.Exchange(ref _busy, 0);
            throw;
        }
    }

    private void StartGame(string gameId, CancellationToken cancellationToken)
    {
        if (_activeGame is { IsCompleted: false })
        {
            logger.LogWarning("Ignoring unexpected concurrent game {GameId}", gameId);
            return;
        }

        Interlocked.Exchange(ref _busy, 1);
        _activeGame = RunGameAsync(gameId, cancellationToken);
    }

    private async Task RunGameAsync(string gameId, CancellationToken cancellationToken)
    {
        try
        {
            await gameController.PlayAsync(gameId, _account!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Game {GameId} failed", gameId);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            logger.LogInformation("Waiting for challenges...");
        }
    }
}
