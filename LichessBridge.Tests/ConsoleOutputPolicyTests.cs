using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DotNetLab.LichessBridge.Tests;

public sealed class ConsoleOutputPolicyTests
{
    [TestCase(ChessColor.White)]
    [TestCase(ChessColor.Black)]
    public void EntersQuietModeWhenOurClockCountsDownWithoutServerEvents(ChessColor color)
    {
        var time = new FakeTimeProvider();
        var policy = CreatePolicy(time);
        policy.Update(new GameClockStatus("game", color, new ChessClock(12000, 12000), color, false));

        Assert.That(policy.IsQuiet, Is.False);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.That(policy.IsQuiet, Is.True);
    }

    [Test]
    public void PreservesQuietModeUntilClockExceedsResumeThreshold()
    {
        var time = new FakeTimeProvider();
        var policy = CreatePolicy(time);
        policy.Update(Clock(10_000));
        Assert.That(policy.IsQuiet, Is.True);
        policy.Update(Clock(12_000));
        Assert.That(policy.IsQuiet, Is.True);
        policy.Update(Clock(15_000));
        Assert.That(policy.IsQuiet, Is.True);
        policy.Update(Clock(15_001));

        Assert.That(policy.IsQuiet, Is.False);
    }

    [Test]
    public void DoesNotCountDownOurClockDuringOpponentsTurn()
    {
        var time = new FakeTimeProvider();
        var policy = CreatePolicy(time);
        policy.Update(Clock(12_000) with { ActiveColor = ChessColor.Black });
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.That(policy.IsQuiet, Is.False);
    }

    [Test]
    public void AccountsForEventAgeAndDoesNotLoseUnobservedQuietTransition()
    {
        var time = new FakeTimeProvider();
        var policy = CreatePolicy(time);
        policy.Update(Clock(12_000) with { ReceivedAgo = TimeSpan.FromSeconds(1) });
        time.Advance(TimeSpan.FromSeconds(1));
        policy.Update(Clock(12_000));

        Assert.That(policy.IsQuiet, Is.True);
    }

    [Test]
    public void ResumesWhenGameFinishesOrObservationIsCleared()
    {
        var policy = CreatePolicy(new FakeTimeProvider());
        policy.Update(Clock(1_000));
        Assert.That(policy.IsQuiet, Is.True);
        policy.Update(Clock(1_000) with { IsFinished = true });
        Assert.That(policy.IsQuiet, Is.False);
        policy.Update(Clock(1_000));
        policy.Update(null);

        Assert.That(policy.IsQuiet, Is.False);
    }

    [Test]
    public void DoesNotCarryHysteresisIntoAnotherGame()
    {
        var policy = CreatePolicy(new FakeTimeProvider());
        policy.Update(Clock(1000));
        policy.Update(Clock(12000) with { GameId = "new-game" });

        Assert.That(policy.IsQuiet, Is.False);
    }

    [Test]
    public void AllowsDisablingQuietMode()
    {
        var policy = new ConsoleOutputPolicy(Options.Create(new ConsoleOutputOptions { ConsoleQuietBelowMilliseconds = 0 }), new FakeTimeProvider());
        policy.Update(Clock(0));

        Assert.That(policy.IsQuiet, Is.False);
    }

    private static ConsoleOutputPolicy CreatePolicy(TimeProvider time) => new(Options.Create(new ConsoleOutputOptions()), time);
    private static GameClockStatus Clock(int milliseconds) => new("game", ChessColor.White, new ChessClock(milliseconds, 60_000), ChessColor.White, false);
}
