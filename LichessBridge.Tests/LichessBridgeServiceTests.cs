using System.Runtime.CompilerServices;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Game;
using LichessBridge.Lichess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LichessBridge.Tests;

public sealed class LichessBridgeServiceTests
{
    [Test]
    public async Task AcceptsSupportedChallengeAndStartsGame()
    {
        var lichess = new ServiceLichessClient([
            new ChallengeEvent(new LichessChallenge("c1", "Benny", false, "standard", "blitz")),
            new GameStartEvent("g1"),
            new GameFinishEvent("g1"),
            new UnknownAccountEvent("challengeCanceled")
        ]);
        var games = new RecordingGameController();
        var service = CreateService(lichess, games);

        await service.StartAsync(CancellationToken.None);
        await games.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(lichess.Accepted, Is.EqualTo(new[] { "c1" }));
            Assert.That(lichess.Declined, Is.Empty);
            Assert.That(games.GameIds, Is.EqualTo(new[] { "g1" }));
        });
    }

    [Test]
    public async Task DeclinesUnsupportedChallenge()
    {
        var lichess = new ServiceLichessClient([
            new ChallengeEvent(new LichessChallenge("c1", "Benny", true, "standard", "blitz"))
        ]);
        var service = CreateService(lichess, new RecordingGameController());

        await service.StartAsync(CancellationToken.None);
        await lichess.DeclinedCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.That(lichess.Declined, Is.EqualTo(new[] { ("c1", "casual") }));
    }

    [Test]
    public async Task IgnoresOutgoingChallenge()
    {
        var lichess = new ServiceLichessClient([
            new ChallengeEvent(new LichessChallenge("c1", "AlarmBot", false, "standard", "bullet", "alarm-bot"))
        ]);
        var service = CreateService(lichess, new RecordingGameController());

        await service.StartAsync(CancellationToken.None);
        await lichess.AllEventsRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(lichess.Accepted, Is.Empty);
            Assert.That(lichess.Declined, Is.Empty);
        });
    }

    private static LichessBridgeService CreateService(ServiceLichessClient lichess, IGameController gameController)
    {
        var filter = new ChallengeFilter(Options.Create(new LichessOptions
        {
            ChallengeFilter = new ChallengeFilterOptions
            {
                Rated = false,
                Variants = ["standard"],
                Speeds = ["blitz", "rapid"]
            }
        }));
        return new LichessBridgeService(lichess, filter, gameController, NullLogger<LichessBridgeService>.Instance);
    }

    private sealed class ServiceLichessClient(IReadOnlyList<AccountEvent> events) : ILichessClient
    {
        public List<string> Accepted { get; } = [];
        public List<(string Id, string Reason)> Declined { get; } = [];
        public TaskCompletionSource DeclinedCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllEventsRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LichessAccount("alarm-bot", "AlarmBot"));

        public async IAsyncEnumerable<AccountEvent> StreamAccountEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var accountEvent in events)
            {
                yield return accountEvent;
            }

            AllEventsRead.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public IAsyncEnumerable<GameStreamEvent> StreamGameAsync(string gameId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AcceptChallengeAsync(string challengeId, CancellationToken cancellationToken)
        {
            Accepted.Add(challengeId);
            return Task.CompletedTask;
        }

        public Task DeclineChallengeAsync(string challengeId, string reason, CancellationToken cancellationToken)
        {
            Declined.Add((challengeId, reason));
            DeclinedCalled.TrySetResult();
            return Task.CompletedTask;
        }

        public Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingGameController : IGameController
    {
        public List<string> GameIds { get; } = [];
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PlayAsync(string gameId, LichessAccount account, CancellationToken cancellationToken)
        {
            GameIds.Add(gameId);
            Called.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
