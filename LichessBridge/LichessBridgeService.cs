using System.Text.Json;
using System.Globalization;
using LichessBridge.Configuration;
using LichessBridge.Game;
using LichessBridge.Lichess;
using LichessBridge.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LichessBridge;

public sealed class LichessBridgeService(ILichessClient lichess, IChallengeFilter challengeFilter, IGameController gameController, IStatusDisplay statusDisplay, IQuitController quitController, IAutoChallengeController autoChallengeController, IHostApplicationLifetime applicationLifetime, IOptions<EngineOptions> engineOptions, IOptions<LichessOptions> lichessOptions, IOptions<AutoBotChallengeOptions> autoBotChallengeOptions, TimeProvider timeProvider, ILogger<LichessBridgeService> logger) : BackgroundService
{
    private readonly Lock _autoChallengeLock = new();
    private readonly SemaphoreSlim _historyFileLock = new(1, 1);
    private readonly SemaphoreSlim _autoChallengeSignal = new(0, 1);
    private readonly Dictionary<string, AutoChallengeHistoryValue> _opponentCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutoChallengeHistoryValue> _declineRestrictions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<OnlineBot>? _onlineBotsCache;
    private DateTimeOffset _onlineBotsCacheExpiresAt;
    private int _busy;
    private int _autoChallengeCreating;
    private Task? _activeGame;
    private LichessAccount? _account;
    private string? _outgoingChallengeId;
    private string? _activeOpponentId;
    private string? _activeOpponentName;
    private AutoChallengeAttempt? _autoChallengeAttempt;
    private DateTimeOffset _idleSince = timeProvider.GetUtcNow();
    private int _nextProfileIndex;
    private StatusHeader? _statusHeader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        quitController.Changed += HandleQuitChanged;
        autoChallengeController.Changed += HandleAutoChallengeChanged;
        _account = await lichess.GetAccountAsync(stoppingToken);
        logger.LogInformation("Connected to Lichess as {Username}", _account.Username);
        logger.LogInformation("Waiting for challenges...");
        var filter = lichessOptions.Value.ChallengeFilter;
        var dashboardRatings = _account.Ratings.Where(rating => IsDashboardRating(rating.Speed)).ToArray();
        _statusHeader = new StatusHeader(engineOptions.Value.Name, _account.Username, engineOptions.Value.Ponder, autoChallengeController.IsEnabled, filter.Modes, filter.Opponents, filter.Variants, filter.Speeds, dashboardRatings);
        statusDisplay.ShowHeader(_statusHeader);
        ResetIdleTimer();

        if (autoChallengeController.IsEnabled)
        {
            ShowAutoChallengeCountdown();
        }
        else
        {
            statusDisplay.Show("Waiting for challenges...");
        }

        await LoadAutoChallengeHistoryAsync(stoppingToken);
        var autoChallengeTask = RunAutoBotChallengeAsync(stoppingToken);

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
                await Task.Delay(retry, timeProvider, stoppingToken);
                retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, 30));
            }
        }

        if (_activeGame is not null)
        {
            await _activeGame.WaitAsync(CancellationToken.None);
        }

        await autoChallengeTask;
    }

    private void HandleQuitChanged(bool requested)
    {
        statusDisplay.ShowQuit(requested);

        if (requested && Volatile.Read(ref _busy) is 0)
        {
            statusDisplay.Show("Shutting down...");
            applicationLifetime.StopApplication();
        }
    }

    private void HandleAutoChallengeChanged(bool enabled)
    {
        if (_statusHeader is not { } header)
        {
            return;
        }

        _statusHeader = header with { AutoBotChallenge = enabled };
        statusDisplay.ShowHeader(_statusHeader);
        ResetIdleTimer();
        logger.LogInformation("Automatic bot challenges {State}", enabled ? "enabled" : "disabled");

        if (enabled)
        {
            ShowAutoChallengeCountdown();
            SignalAutoChallenge();
        }
        else if (Volatile.Read(ref _busy) is 0 && HasOutgoingChallenge() is false)
        {
            statusDisplay.Show("Waiting for challenges...");
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
                await HandleChallengeEndedAsync(ended, cancellationToken);
                break;
            case GameStartEvent started:
                Interlocked.Exchange(ref _busy, 1);

                lock (_autoChallengeLock)
                {
                    _outgoingChallengeId = null;
                    _autoChallengeAttempt = null;
                }

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

            lock (_autoChallengeLock)
            {
                _outgoingChallengeId = challenge.Id;

                if (_autoChallengeAttempt is { ChallengeId.Length: 0 } attempt && destination.Equals(attempt.Opponent.Username, StringComparison.OrdinalIgnoreCase))
                {
                    _autoChallengeAttempt = attempt with { ChallengeId = challenge.Id };
                }
            }

            statusDisplay.Show($"Challenging {destination}: {(challenge.Rated ? "rated" : "casual")} {challenge.Speed}{timeControl}...");

            return;
        }

        var challenger = challenge.ChallengerRating is { } rating ? $"{challenge.Challenger} ({rating})" : challenge.Challenger;
        logger.LogInformation("Challenge {ChallengeId} from {Challenger}: {Mode} {Variant} {Speed}{TimeControl}", challenge.Id, challenger, challenge.Rated ? "rated" : "casual", challenge.Variant, challenge.Speed, timeControl);
        statusDisplay.Show($"Challenge from {challenger}: {(challenge.Rated ? "rated" : "casual")} {challenge.Speed}{timeControl}");

        ResetIdleTimer();
        var isBusy = Volatile.Read(ref _busy) == 1 || Volatile.Read(ref _autoChallengeCreating) == 1 || HasOutgoingChallenge();
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

    private async Task HandleChallengeEndedAsync(ChallengeEndedEvent ended, CancellationToken cancellationToken)
    {
        AutoChallengeAttempt? attempt;
        var historyChanged = false;

        lock (_autoChallengeLock)
        {
            if (string.Equals(ended.ChallengeId, _outgoingChallengeId, StringComparison.OrdinalIgnoreCase) is false)
            {
                return;
            }

            attempt = _autoChallengeAttempt is { } current && current.ChallengeId.Equals(ended.ChallengeId, StringComparison.OrdinalIgnoreCase) ? current : null;

            if (attempt is not null && ended.Reason.Equals("declined", StringComparison.OrdinalIgnoreCase))
            {
                _opponentCooldowns.Remove(attempt.Opponent.Id);
                RememberDecline(attempt, ended.DeclineReasonKey);
                historyChanged = true;
            }

            _outgoingChallengeId = null;
            _autoChallengeAttempt = null;
        }

        if (historyChanged)
        {
            await SaveAutoChallengeHistoryAsync(cancellationToken);
        }

        logger.LogInformation("Outgoing challenge {ChallengeId} {Reason}: {DeclineReason}", ended.ChallengeId, ended.Reason, ended.DeclineReasonKey);
        statusDisplay.Show("Waiting for challenges...");
        ScheduleNextAutoChallenge();
    }

    private async Task RunAutoBotChallengeAsync(CancellationToken cancellationToken)
    {
        while (cancellationToken.IsCancellationRequested is false)
        {
            try
            {
                await WaitForAutoChallengeSignalAsync(cancellationToken);
                ShowAutoChallengeCountdown();

                if (GetExpiredAutoChallenge() is { } expired)
                {
                    await CancelExpiredChallengeAsync(expired, cancellationToken);

                    continue;
                }

                if (CanCreateAutoChallenge() is false || Interlocked.CompareExchange(ref _autoChallengeCreating, 1, 0) is not 0)
                {
                    continue;
                }

                try
                {
                    await CreateAutoChallengeAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Exchange(ref _autoChallengeCreating, 0);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Automatic bot challenge failed");
                ResetIdleTimer();
                ShowAutoChallengeCountdown();
            }
        }
    }

    private async Task CreateAutoChallengeAsync(CancellationToken cancellationToken)
    {
        if (CanCreateAutoChallenge() is false)
        {
            return;
        }

        var onlineBots = await GetOnlineBotsAsync(cancellationToken);
        var selection = SelectAutoChallenge(onlineBots, timeProvider.GetUtcNow());

        if (selection is null)
        {
            logger.LogDebug("No eligible online bot found for automatic challenge");
            ResetIdleTimer();
            ShowAutoChallengeCountdown();

            return;
        }

        var profile = selection.Profile;
        var opponent = selection.Opponent;
        var timeControl = GameController.FormatTimeControl(profile.InitialSeconds, profile.IncrementSeconds);
        statusDisplay.Show($"Challenging {opponent.Username}: {profile.Mode} {profile.Name} ({timeControl})...");
        var request = new BotChallengeRequest(profile.Mode.Equals("rated", StringComparison.OrdinalIgnoreCase), profile.Variant, profile.InitialSeconds, profile.IncrementSeconds);
        string challengeId;
        try
        {
            challengeId = await lichess.CreateChallengeAsync(opponent.Username, request, cancellationToken);
        }
        catch (ChallengeCreationException exception)
        {
            RememberChallengeRejection(opponent, exception.ApiMessage);
            await SaveAutoChallengeHistoryAsync(cancellationToken);
            logger.LogInformation("Automatic challenge to {Opponent} was rejected by Lichess: {Reason}", opponent.Username, exception.ApiMessage);
            statusDisplay.Show($"Challenge to {opponent.Username} rejected: {exception.ApiMessage}");
            ScheduleNextAutoChallenge();

            return;
        }

        logger.LogDebug("Lichess created automatic challenge {ChallengeId} to {Opponent}", challengeId, opponent.Username);
        var now = timeProvider.GetUtcNow();
        var gameStarted = false;

        lock (_autoChallengeLock)
        {
            _opponentCooldowns[opponent.Id] = new AutoChallengeHistoryValue(now.AddMinutes(autoBotChallengeOptions.Value.OpponentCooldownMinutes), $"Cooldown after challenging {opponent.Username} with profile {profile.Name}");
            _nextProfileIndex = (selection.ProfileIndex + 1) % autoBotChallengeOptions.Value.Profiles.Length;

            if (Volatile.Read(ref _busy) is 1)
            {
                gameStarted = true;
            }
            else
            {
                _autoChallengeAttempt = new AutoChallengeAttempt(challengeId, opponent, profile, now);
                _outgoingChallengeId = challengeId;
            }
        }

        await SaveAutoChallengeHistoryAsync(cancellationToken);

        if (gameStarted)
        {
            return;
        }

        logger.LogInformation("Automatically challenged {Opponent} with profile {Profile}: {Mode} {Variant} {TimeControl}", opponent.Username, profile.Name, profile.Mode, profile.Variant, timeControl);
    }

    private async Task<IReadOnlyList<OnlineBot>> GetOnlineBotsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (_onlineBotsCache is { } cached && _onlineBotsCacheExpiresAt > now)
        {
            logger.LogInformation("Using {Count} online bots from cache for automatic challenge selection; cache expires at {ExpiresAt}", cached.Count, StatusFormatter.FormatDateTime(_onlineBotsCacheExpiresAt));
            return cached;
        }

        var onlineBots = await lichess.GetOnlineBotsAsync(cancellationToken);
        _onlineBotsCache = onlineBots;
        _onlineBotsCacheExpiresAt = timeProvider.GetUtcNow().AddSeconds(autoBotChallengeOptions.Value.OnlineBotsCacheSeconds);
        logger.LogInformation("Fetched {Count} online bots from Lichess for automatic challenge selection; cached until {ExpiresAt}", onlineBots.Count, StatusFormatter.FormatDateTime(_onlineBotsCacheExpiresAt));

        return onlineBots;
    }

    private AutoChallengeSelection? SelectAutoChallenge(IReadOnlyList<OnlineBot> onlineBots, DateTimeOffset now)
    {
        var options = autoBotChallengeOptions.Value;

        for (var offset = 0; offset < options.Profiles.Length; offset++)
        {
            var profileIndex = (_nextProfileIndex + offset) % options.Profiles.Length;
            var profile = options.Profiles[profileIndex];

            if (profile.Enabled is false)
            {
                continue;
            }

            var evaluations = onlineBots.Select(bot => new AutoChallengeCandidate(bot, GetExclusionReason(bot, profile, now))).ToArray();
            var eligibleBots = evaluations.Where(candidate => candidate.ExclusionReason is null).Select(candidate => candidate.Bot).ToArray();
            var excludedCount = evaluations.Length - eligibleBots.Length;
            var exclusions = evaluations.Where(candidate => candidate.ExclusionReason is not null).GroupBy(candidate => candidate.ExclusionReason, StringComparer.OrdinalIgnoreCase).Select(group => $"{group.Key}={group.Count()}").ToArray();
            var exclusionSummary = exclusions.Length == 0 ? "none" : string.Join(", ", exclusions);
            var performance = GetPerformanceKey(profile);
            logger.LogInformation("Auto challenge profile {Profile} using {Performance} ratings: {EligibleCount} eligible and {ExcludedCount} filtered from {TotalCount} online bots. Filters: {Filters}", profile.Name, performance, eligibleBots.Length, excludedCount, onlineBots.Count, exclusionSummary);
            logger.LogDebug("Eligible bots for auto challenge profile {Profile}: {Candidates}", profile.Name, JoinBotNames(eligibleBots, performance));

            if (eligibleBots.Length > 0)
            {
                var opponent = SelectClosestRatedOpponent(eligibleBots, performance);
                logger.LogDebug("Selected {Opponent} for automatic challenge profile {Profile}", opponent.Username, profile.Name);

                return new AutoChallengeSelection(opponent, profile, profileIndex);
            }
        }

        return null;
    }

    private OnlineBot SelectClosestRatedOpponent(IReadOnlyList<OnlineBot> eligibleBots, string performance)
    {
        var accountRating = FindRating(_account?.Ratings ?? [], performance);

        if (accountRating is null)
        {
            return eligibleBots[Random.Shared.Next(eligibleBots.Count)];
        }

        var closest = eligibleBots
            .Select(bot => (Bot: bot, Rating: FindRating(bot.Ratings, performance)))
            .Where(candidate => candidate.Rating is not null)
            .GroupBy(candidate => Math.Abs(candidate.Rating!.Value - accountRating.Value))
            .OrderBy(group => group.Key)
            .FirstOrDefault()?
            .Select(candidate => candidate.Bot)
            .ToArray();

        return closest is { Length: > 0 }
            ? closest[Random.Shared.Next(closest.Length)]
            : eligibleBots[Random.Shared.Next(eligibleBots.Count)];
    }

    private string? GetExclusionReason(OnlineBot bot, AutoBotChallengeProfileOptions profile, DateTimeOffset now)
    {
        var account = _account ?? throw new InvalidOperationException("The Lichess account has not been loaded.");
        var options = autoBotChallengeOptions.Value;

        if (bot.Id.Equals(account.Id, StringComparison.OrdinalIgnoreCase))
        {
            return "own-account";
        }

        if (Matches(options.BlockList, bot))
        {
            return "block-list";
        }

        if (options.AllowList.Length > 0 && Matches(options.AllowList, bot) is false)
        {
            return "not-in-allow-list";
        }

        var performance = GetPerformanceKey(profile);
        var opponentRating = FindRating(bot.Ratings, performance);
        var minimumRating = profile.MinRating ?? options.MinRating;
        var maximumRating = profile.MaxRating ?? options.MaxRating;
        var maximumRatingDifference = profile.MaxRatingDifference ?? options.MaxRatingDifference;
        var ratingFilterEnabled = minimumRating is not null || maximumRating is not null || maximumRatingDifference is not null;

        if (ratingFilterEnabled && opponentRating is null)
        {
            return "rating-unavailable";
        }

        if (minimumRating is not null && opponentRating < minimumRating)
        {
            return "below-min-rating";
        }

        if (maximumRating is not null && opponentRating > maximumRating)
        {
            return "above-max-rating";
        }

        if (maximumRatingDifference is not null)
        {
            var botRating = FindRating(account.Ratings, performance);

            if (botRating is null)
            {
                return "bot-rating-unavailable";
            }

            if (Math.Abs(opponentRating.GetValueOrDefault() - botRating.Value) > maximumRatingDifference)
            {
                return "rating-difference";
            }
        }

        lock (_autoChallengeLock)
        {
            if (IsBlocked(_opponentCooldowns, bot.Id, now))
            {
                return "opponent-cooldown";
            }

            if (IsBlocked(_declineRestrictions, RestrictionKey(bot.Id, "opponent", string.Empty), now))
            {
                return "opponent-decline-retention";
            }

            if (IsBlocked(_declineRestrictions, RestrictionKey(bot.Id, "profile", profile.Name), now))
            {
                return "profile-decline-retention";
            }

            if (IsBlocked(_declineRestrictions, RestrictionKey(bot.Id, "mode", profile.Mode), now))
            {
                return "mode-decline-retention";
            }

            return IsBlocked(_declineRestrictions, RestrictionKey(bot.Id, "variant", profile.Variant), now) ? "variant-decline-retention" : null;
        }
    }

    private AutoChallengeAttempt? GetExpiredAutoChallenge()
    {
        lock (_autoChallengeLock)
        {
            if (_autoChallengeAttempt is not { } attempt || timeProvider.GetUtcNow() - attempt.CreatedAt < TimeSpan.FromSeconds(autoBotChallengeOptions.Value.ChallengeTimeoutSeconds))
            {
                return null;
            }

            _autoChallengeAttempt = null;
            _outgoingChallengeId = null;

            return attempt;
        }
    }

    private async Task CancelExpiredChallengeAsync(AutoChallengeAttempt attempt, CancellationToken cancellationToken)
    {
        await lichess.CancelChallengeAsync(attempt.ChallengeId, cancellationToken);
        logger.LogInformation("Automatic challenge {ChallengeId} to {Opponent} timed out and was canceled", attempt.ChallengeId, attempt.Opponent.Username);
        statusDisplay.Show("Waiting for challenges...");
        ScheduleNextAutoChallenge();
    }

    private bool CanCreateAutoChallenge()
    {
        if (autoChallengeController.IsEnabled is false || Volatile.Read(ref _busy) is 1 || quitController.IsRequested)
        {
            return false;
        }

        lock (_autoChallengeLock)
        {
            return _outgoingChallengeId is null && _autoChallengeAttempt is null && timeProvider.GetUtcNow() - _idleSince >= TimeSpan.FromSeconds(autoBotChallengeOptions.Value.IdleDelaySeconds);
        }
    }

    private bool HasOutgoingChallenge()
    {
        lock (_autoChallengeLock)
        {
            return _outgoingChallengeId is not null;
        }
    }

    private void ResetIdleTimer()
    {
        lock (_autoChallengeLock)
        {
            _idleSince = timeProvider.GetUtcNow();
        }
    }

    private void ScheduleNextAutoChallenge()
    {
        ResetIdleTimer();
        ShowAutoChallengeCountdown();
        SignalAutoChallenge();
    }

    private void SignalAutoChallenge()
    {
        if (_autoChallengeSignal.CurrentCount is 0)
        {
            _autoChallengeSignal.Release();
        }
    }

    private async Task WaitForAutoChallengeSignalAsync(CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = _autoChallengeSignal.WaitAsync(waitCancellation.Token);
        var poll = Task.Delay(TimeSpan.FromSeconds(1), timeProvider, waitCancellation.Token);
        var completed = await Task.WhenAny(signal, poll);
        await completed;
        await waitCancellation.CancelAsync();

        try
        {
            await (ReferenceEquals(completed, signal) ? poll : signal);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
        }
    }

    private void ShowAutoChallengeCountdown()
    {
        if (autoChallengeController.IsEnabled is false || Volatile.Read(ref _busy) is 1 || quitController.IsRequested)
        {
            return;
        }

        int remainingSeconds;
        lock (_autoChallengeLock)
        {
            if (_outgoingChallengeId is not null || _autoChallengeAttempt is not null)
            {
                return;
            }

            var delay = TimeSpan.FromSeconds(autoBotChallengeOptions.Value.IdleDelaySeconds);
            remainingSeconds = Math.Max(0, (int)Math.Ceiling((delay - (timeProvider.GetUtcNow() - _idleSince)).TotalSeconds));
        }

        if (remainingSeconds > 0)
        {
            statusDisplay.Show($"Waiting for challenges... Auto challenge in {remainingSeconds}s");
        }
    }

    private async Task LoadAutoChallengeHistoryAsync(CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(autoBotChallengeOptions.Value.AutoChallengeStatePath);

        if (File.Exists(path) is false)
        {
            return;
        }

        await _historyFileLock.WaitAsync(cancellationToken);
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var history = JsonSerializer.Deserialize<AutoChallengeHistory>(json);

            if (history is null)
            {
                return;
            }

            var now = timeProvider.GetUtcNow();

            lock (_autoChallengeLock)
            {
                foreach (var entry in history.OpponentCooldowns.Where(entry => entry.ExpiresAt > now))
                {
                    _opponentCooldowns[entry.Key] = new AutoChallengeHistoryValue(entry.ExpiresAt.ToUniversalTime(), entry.Reason);
                }

                foreach (var entry in history.DeclineRestrictions.Where(entry => entry.ExpiresAt > now))
                {
                    _declineRestrictions[entry.Key] = new AutoChallengeHistoryValue(entry.ExpiresAt.ToUniversalTime(), entry.Reason);
                }
            }

            logger.LogInformation("Loaded automatic challenge history from {Path}: {Cooldowns} opponent cooldowns and {Restrictions} decline restrictions", path, _opponentCooldowns.Count, _declineRestrictions.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load automatic challenge history from {Path}", path);
        }
        finally
        {
            _historyFileLock.Release();
        }
    }

    private async Task SaveAutoChallengeHistoryAsync(CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(autoBotChallengeOptions.Value.AutoChallengeStatePath);
        await _historyFileLock.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            AutoChallengeHistory history;

            lock (_autoChallengeLock)
            {
                RemoveExpired(_opponentCooldowns, now);
                RemoveExpired(_declineRestrictions, now);
                history = new AutoChallengeHistory(_opponentCooldowns.Select(entry => new AutoChallengeHistoryEntry(entry.Key, entry.Value.ExpiresAt.ToUniversalTime(), entry.Value.Reason)).ToArray(), _declineRestrictions.Select(entry => new AutoChallengeHistoryEntry(entry.Key, entry.Value.ExpiresAt.ToUniversalTime(), entry.Value.Reason)).ToArray());
            }

            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The automatic challenge history directory could not be determined.");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
            logger.LogDebug("Saved automatic challenge history to {Path}: {Cooldowns} opponent cooldowns and {Restrictions} decline restrictions", path, history.OpponentCooldowns.Count, history.DeclineRestrictions.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not save automatic challenge history to {Path}", path);
        }
        finally
        {
            _historyFileLock.Release();
        }
    }

    private static void RemoveExpired(Dictionary<string, AutoChallengeHistoryValue> entries, DateTimeOffset now)
    {
        foreach (var key in entries.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray())
        {
            entries.Remove(key);
        }
    }

    private void RememberDecline(AutoChallengeAttempt attempt, string reason)
    {
        var normalizedReason = reason.ToLowerInvariant();
        var restriction = normalizedReason switch
        {
            "timecontrol" or "toofast" or "tooslow" or "noincrement" => RestrictionKey(attempt.Opponent.Id, "profile", attempt.Profile.Name),
            "rated" or "casual" => RestrictionKey(attempt.Opponent.Id, "mode", attempt.Profile.Mode),
            "standard" or "variant" => RestrictionKey(attempt.Opponent.Id, "variant", attempt.Profile.Variant),
            _ => RestrictionKey(attempt.Opponent.Id, "opponent", string.Empty)
        };
        var expiresAt = timeProvider.GetUtcNow().AddHours(autoBotChallengeOptions.Value.DeclineHistoryRetentionHours);
        _declineRestrictions[restriction] = new AutoChallengeHistoryValue(expiresAt, $"{attempt.Opponent.Username} declined the challenge: {DescribeDeclineReason(reason)}");
        logger.LogInformation("Remembering automatic challenge decline from {Opponent}: reason {Reason}, profile {Profile}, mode {Mode}, variant {Variant}, until {ExpiresAt}", attempt.Opponent.Username, reason, attempt.Profile.Name, attempt.Profile.Mode, attempt.Profile.Variant, StatusFormatter.FormatDateTime(expiresAt));
    }

    private void RememberChallengeRejection(OnlineBot opponent, string reason)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAt = GetChallengeRejectionExpiry(reason, now, TimeSpan.FromHours(autoBotChallengeOptions.Value.DeclineHistoryRetentionHours));

        lock (_autoChallengeLock)
        {
            _opponentCooldowns.Remove(opponent.Id);
            _declineRestrictions[RestrictionKey(opponent.Id, "opponent", string.Empty)] = new AutoChallengeHistoryValue(expiresAt, $"{opponent.Username} rejected the challenge request: {reason}");
        }
    }

    internal static DateTimeOffset GetChallengeRejectionExpiry(string reason, DateTimeOffset now, TimeSpan fallbackRetention)
    {
        const string marker = "until ";
        var utcNow = now.ToUniversalTime();
        var markerIndex = reason.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (markerIndex >= 0)
        {
            var value = reason[(markerIndex + marker.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('.', ',');

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) && parsed > utcNow)
            {
                return parsed.ToUniversalTime();
            }
        }

        return utcNow.Add(fallbackRetention);
    }

    internal static string DescribeDeclineReason(string reason) => reason.ToLowerInvariant() switch
    {
        "casual" => "only accepts casual games",
        "rated" => "only accepts rated games",
        "toofast" => "the time control is too fast",
        "tooslow" => "the time control is too slow",
        "timecontrol" => "the time control is not accepted",
        "noincrement" => "only accepts games with an increment",
        "standard" => "only accepts standard chess",
        "variant" => "only accepts variant games",
        "nobot" => "challenges from bots are not accepted",
        "onlybot" => "only challenges from bots are accepted",
        "later" => "the opponent wants to play later",
        "generic" or "" => "no specific reason was provided",
        _ => $"Lichess reported '{reason}'"
    };

    private static bool IsBlocked(IReadOnlyDictionary<string, AutoChallengeHistoryValue> restrictions, string key, DateTimeOffset now) => restrictions.TryGetValue(key, out var restriction) && restriction.ExpiresAt > now;

    private static bool Matches(IEnumerable<string> users, OnlineBot bot) => users.Any(user => user.Equals(bot.Id, StringComparison.OrdinalIgnoreCase) || user.Equals(bot.Username, StringComparison.OrdinalIgnoreCase));

    private static int? FindRating(IEnumerable<LichessRating> ratings, string performance) => ratings.FirstOrDefault(rating => rating.Speed.Equals(performance, StringComparison.OrdinalIgnoreCase))?.Rating;

    internal static string GetPerformanceKey(AutoBotChallengeProfileOptions profile)
    {
        if (profile.Variant.Equals("standard", StringComparison.OrdinalIgnoreCase) is false)
        {
            return profile.Variant;
        }

        var estimatedDuration = profile.InitialSeconds + profile.IncrementSeconds * 40;

        return estimatedDuration switch
        {
            < 30 => "ultraBullet",
            < 180 => "bullet",
            < 480 => "blitz",
            < 1500 => "rapid",
            _ => "classical"
        };
    }

    private static bool IsDashboardRating(string speed) => speed.Equals("bullet", StringComparison.OrdinalIgnoreCase) || speed.Equals("blitz", StringComparison.OrdinalIgnoreCase) || speed.Equals("rapid", StringComparison.OrdinalIgnoreCase);

    private static string JoinBotNames(IEnumerable<OnlineBot> bots, string? performance = null) => string.Join(", ", bots.Select(bot => performance is null ? $"{bot.Username} ({bot.Id})" : $"{bot.Username} ({bot.Id}, {FindRating(bot.Ratings, performance)?.ToString() ?? "unrated"})"));

    private static string RestrictionKey(string opponentId, string type, string value) => $"{opponentId}|{type}|{value}";

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
            await RefreshAccountAsync(cancellationToken);
            Interlocked.Exchange(ref _busy, 0);
            _activeOpponentId = null;
            _activeOpponentName = null;
            ResetIdleTimer();
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

                if (autoChallengeController.IsEnabled)
                {
                    ShowAutoChallengeCountdown();
                    SignalAutoChallenge();
                }
                else
                {
                    statusDisplay.Show("Waiting for challenges...");
                }
            }
        }
    }

    private async Task RefreshAccountAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var account = await lichess.GetAccountAsync(cancellationToken);
            _account = account;

            if (_statusHeader is { } header)
            {
                var ratings = account.Ratings.Where(rating => IsDashboardRating(rating.Speed)).ToArray();
                _statusHeader = header with { AccountName = account.Username, Ratings = ratings };
                statusDisplay.ShowHeader(_statusHeader);
            }

            logger.LogInformation("Refreshed Lichess ratings after game completion");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not refresh Lichess ratings after game completion");
        }
    }

    private sealed record AutoChallengeAttempt(string ChallengeId, OnlineBot Opponent, AutoBotChallengeProfileOptions Profile, DateTimeOffset CreatedAt);
    private sealed record AutoChallengeSelection(OnlineBot Opponent, AutoBotChallengeProfileOptions Profile, int ProfileIndex);
    private sealed record AutoChallengeCandidate(OnlineBot Bot, string? ExclusionReason);
    private sealed record AutoChallengeHistory(IReadOnlyList<AutoChallengeHistoryEntry> OpponentCooldowns, IReadOnlyList<AutoChallengeHistoryEntry> DeclineRestrictions);
    private sealed record AutoChallengeHistoryEntry(string Key, DateTimeOffset ExpiresAt, string Reason = "Legacy history entry");
    private sealed record AutoChallengeHistoryValue(DateTimeOffset ExpiresAt, string Reason);
}
