using LichessBridge.Configuration;
using LichessBridge.Game;
using LichessBridge.Lichess;
using LichessBridge.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LichessBridge;

public sealed class LichessBridgeService(ILichessClient lichess, IChallengeFilter challengeFilter, IGameController gameController, IStatusDisplay statusDisplay, IQuitController quitController, IHostApplicationLifetime applicationLifetime, IOptions<EngineOptions> engineOptions, IOptions<LichessOptions> lichessOptions, ILogger<LichessBridgeService> logger) : BackgroundService
{
    private int _busy;
    private Task? _activeGame;
    private LichessAccount? _account;
    private string? _outgoingChallengeId;
    private string? _activeOpponentId;
    private string? _activeOpponentName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        quitController.Changed += HandleQuitChanged;
        _account = await lichess.GetAccountAsync(stoppingToken);
        logger.LogInformation("Connected to Lichess as {Username}", _account.Username);
        logger.LogInformation("Waiting for challenges...");
        var filter = lichessOptions.Value.ChallengeFilter;
        statusDisplay.ShowHeader(new StatusHeader(engineOptions.Value.Name, _account.Username, engineOptions.Value.Ponder, filter.Modes, filter.Opponents, filter.Variants, filter.Speeds, _account.Ratings));
        statusDisplay.Show("Waiting for challenges...");

        var retry = TimeSpan.FromSeconds(1);
        while (stoppingToken.IsCancellationRequested is false)
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
                statusDisplay.Show($"Connection lost. Reconnecting in {retry.TotalSeconds:0} seconds...");
                await Task.Delay(retry, stoppingToken);
                retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, 30));
            }
        }

        if (_activeGame is not null)
        {
            await _activeGame.WaitAsync(CancellationToken.None);
        }
    }

    private void HandleQuitChanged(bool requested)
    {
        statusDisplay.ShowQuit(requested);

        if (requested && Volatile.Read(ref _busy) == 0)
        {
            statusDisplay.Show("Shutting down...");
            applicationLifetime.StopApplication();
        }
    }

    private async Task HandleEventAsync(AccountEvent accountEvent, CancellationToken cancellationToken)
    {
        switch (accountEvent)
        {
            case ChallengeEvent received:
                await HandleChallengeAsync(received.Challenge, cancellationToken);
                break;
            case ChallengeEndedEvent ended:
                HandleChallengeEnded(ended);
                break;
            case GameStartEvent started:
                _outgoingChallengeId = null;
                StartGame(started.GameId, cancellationToken);
                break;
            case GameFinishEvent finished:
                logger.LogDebug("Received gameFinish for {GameId}", finished.GameId);

                if (gameController.NotifyGameFinished(finished.GameId))
                {
                    statusDisplay.Show("Game finished; finalizing...");
                }

                break;
        }
    }

    private async Task HandleChallengeAsync(LichessChallenge challenge, CancellationToken cancellationToken)
    {
        var isOwnChallenge = string.Equals(challenge.ChallengerId, _account?.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(challenge.Challenger, _account?.Username, StringComparison.OrdinalIgnoreCase);
        var timeControl = string.IsNullOrWhiteSpace(challenge.TimeControl) ? string.Empty : $" ({challenge.TimeControl})";

        if (isOwnChallenge)
        {
            logger.LogInformation("Outgoing challenge {ChallengeId}: {Mode} {Variant} {Speed}{TimeControl}", challenge.Id, challenge.Rated ? "rated" : "casual", challenge.Variant, challenge.Speed, timeControl);
            var destination = string.IsNullOrWhiteSpace(challenge.Destination) ? "opponent" : challenge.Destination;
            _outgoingChallengeId = challenge.Id;
            statusDisplay.Show($"Challenging {destination}: {(challenge.Rated ? "rated" : "casual")} {challenge.Speed}{timeControl}...");

            return;
        }

        logger.LogInformation("Challenge {ChallengeId} from {Challenger}: {Mode} {Variant} {Speed}{TimeControl}", challenge.Id, challenge.Challenger, challenge.Rated ? "rated" : "casual", challenge.Variant, challenge.Speed, timeControl);
        statusDisplay.Show($"Challenge from {challenge.Challenger}: {(challenge.Rated ? "rated" : "casual")} {challenge.Speed}{timeControl}");

        var isBusy = Volatile.Read(ref _busy) == 1;
        var activeGamesAgainstChallenger = isBusy && IsActiveOpponent(challenge) ? 1 : 0;
        var botRating = _account?.Ratings.FirstOrDefault(rating => rating.Speed.Equals(challenge.Speed, StringComparison.OrdinalIgnoreCase))?.Rating;
        var decision = challengeFilter.Evaluate(challenge, isBusy, activeGamesAgainstChallenger, botRating);
        if (decision.Accept is false)
        {
            await lichess.DeclineChallengeAsync(challenge.Id, decision.DeclineReason, cancellationToken);
            logger.LogInformation("Challenge {ChallengeId} declined: {Reason}", challenge.Id, decision.DeclineReason);
            statusDisplay.Show("Waiting for challenges...");

            return;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) is not 0)
        {
            await lichess.DeclineChallengeAsync(challenge.Id, "later", cancellationToken);

            return;
        }

        try
        {
            await lichess.AcceptChallengeAsync(challenge.Id, cancellationToken);
            _activeOpponentId = challenge.ChallengerId;
            _activeOpponentName = challenge.Challenger;
            logger.LogInformation("Challenge {ChallengeId} accepted", challenge.Id);
        }
        catch
        {
            Interlocked.Exchange(ref _busy, 0);
            throw;
        }
    }

    private void HandleChallengeEnded(ChallengeEndedEvent ended)
    {
        if (string.Equals(ended.ChallengeId, _outgoingChallengeId, StringComparison.OrdinalIgnoreCase) is false)
        {
            return;
        }

        logger.LogInformation("Outgoing challenge {ChallengeId} {Reason}", ended.ChallengeId, ended.Reason);
        _outgoingChallengeId = null;
        statusDisplay.Show("Waiting for challenges...");
    }

    private bool IsActiveOpponent(LichessChallenge challenge) => (string.IsNullOrWhiteSpace(challenge.ChallengerId) is false && challenge.ChallengerId.Equals(_activeOpponentId, StringComparison.OrdinalIgnoreCase)) || challenge.Challenger.Equals(_activeOpponentName, StringComparison.OrdinalIgnoreCase);

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
            await gameController.PlayAsync(gameId, _account ?? throw new InvalidOperationException("The Lichess account has not been loaded."), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Game {GameId} failed", gameId);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            _activeOpponentId = null;
            _activeOpponentName = null;
            statusDisplay.ClearGame();

            if (quitController.IsRequested)
            {
                logger.LogInformation("Game completed; shutting down as requested");
                statusDisplay.Show("Game completed. Shutting down...");
                applicationLifetime.StopApplication();
            }
            else
            {
                logger.LogInformation("Waiting for challenges...");
                statusDisplay.Show("Waiting for challenges...");
            }
        }
    }
}
