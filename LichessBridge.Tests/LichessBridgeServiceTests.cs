using System.Runtime.CompilerServices;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Engine;
using LichessBridge.Game;
using LichessBridge.Lichess;
using LichessBridge.Status;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace LichessBridge.Tests;

public sealed class LichessBridgeServiceTests
{
    [TestCase(29, 0, "ultraBullet")]
    [TestCase(30, 0, "bullet")]
    [TestCase(120, 1, "bullet")]
    [TestCase(120, 2, "blitz")]
    [TestCase(179, 0, "bullet")]
    [TestCase(180, 0, "blitz")]
    [TestCase(479, 0, "blitz")]
    [TestCase(480, 0, "rapid")]
    [TestCase(1499, 0, "rapid")]
    [TestCase(1500, 0, "classical")]
    public void DerivesLichessPerformanceFromEstimatedGameDuration(int initialSeconds, int incrementSeconds, string expected)
    {
        var profile = new AutoBotChallengeProfileOptions { InitialSeconds = initialSeconds, IncrementSeconds = incrementSeconds };

        Assert.That(LichessBridgeService.GetPerformanceKey(profile), Is.EqualTo(expected));
    }

    [Test]
    public void UsesLichessWaitUntilAsChallengeRejectionExpiry()
    {
        var now = new DateTimeOffset(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);
        var expected = new DateTimeOffset(2026, 8, 10, 6, 39, 44, 221, TimeSpan.Zero);
        const string reason = "bfiedler-bot played 100 games against other bots today, please wait until 2026-08-10T06:39:44.221Z to challenge them.";

        var actual = LichessBridgeService.GetChallengeRejectionExpiry(reason, now, TimeSpan.FromHours(24));

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void UsesConfiguredRetentionWhenChallengeRejectionHasNoWaitUntil()
    {
        var now = new DateTimeOffset(2026, 8, 9, 22, 0, 0, TimeSpan.FromHours(2));

        var actual = LichessBridgeService.GetChallengeRejectionExpiry("Challenges from bots are not accepted", now, TimeSpan.FromHours(12));

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
            Assert.That(actual.Offset, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public void UsesDeclineRetentionForOpponentFirstMoveTimeout()
    {
        var now = new DateTimeOffset(2026, 8, 15, 2, 0, 0, TimeSpan.Zero);

        var history = LichessBridgeService.CreateOpponentFirstMoveTimeoutHistory("bhonduesque", "Rated Blitz", 60, now, 24);

        Assert.Multiple(() =>
        {
            Assert.That(history.ExpiresAt, Is.EqualTo(new DateTimeOffset(2026, 8, 16, 2, 0, 0, TimeSpan.Zero)));
            Assert.That(history.Reason, Is.EqualTo("bhonduesque did not make the first move within 60 seconds after automatic challenge with profile Rated Blitz"));
        });
    }

    [Test]
    public async Task AcceptsSupportedChallengeAndStartsGame()
    {
        var statusDisplay = new RecordingStatusDisplay();
        var lichess = new ServiceLichessClient([
            new ChallengeEvent(new LichessChallenge("c1", "Benny", false, "standard", "blitz", ChallengerRating: 2135)),
            new GameStartEvent("g1"),
            new GameFinishEvent("g1"),
            new UnknownAccountEvent("challengeCanceled")
        ]);
        var games = new RecordingGameController();
        var service = CreateService(lichess, games, statusDisplay: statusDisplay);

        await service.StartAsync(CancellationToken.None);
        await games.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.That(statusDisplay.Statuses, Does.Contain("Challenge from Benny (2135): casual blitz"));

        Assert.Multiple(() =>
        {
            Assert.That(lichess.Accepted, Is.EqualTo(new[] { "c1" }));
            Assert.That(lichess.Declined, Is.Empty);
            Assert.That(games.GameIds, Is.EqualTo(new[] { "g1" }));
            Assert.That(games.FinishedGameIds, Is.EqualTo(new[] { "g1" }));
        });
    }

    [Test]
    public async Task RefreshesDashboardRatingsAfterGameCompletes()
    {
        var statusDisplay = new RecordingStatusDisplay();
        var lichess = new ServiceLichessClient([new GameStartEvent("g1")])
        {
            AccountResponses = new Queue<LichessAccount>(
            [
                new LichessAccount("alarm-bot", "AlarmBot") { Ratings = [new LichessRating("rapid", 2200, 70)] },
                new LichessAccount("alarm-bot", "AlarmBot") { Ratings = [new LichessRating("rapid", 2212, 71)] }
            ])
        };
        var service = CreateService(lichess, new RecordingGameController(), statusDisplay: statusDisplay);

        await service.StartAsync(CancellationToken.None);
        await statusDisplay.HeaderUpdated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.That(statusDisplay.Headers[^1].Ratings, Is.EqualTo(new[] { new LichessRating("rapid", 2212, 71) }));
    }

    [Test]
    public async Task RestartsAutoChallengeCountdownAfterGameCompletes()
    {
        var statusDisplay = new RecordingStatusDisplay();
        var lichess = new ServiceLichessClient([new GameStartEvent("g1")]);
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 10,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Rated Blitz" }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options, statusDisplay);

        await service.StartAsync(CancellationToken.None);
        await statusDisplay.CountdownShown.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.That(statusDisplay.Statuses[^1], Does.StartWith("Waiting for challenges... Auto challenge in "));
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
            new ChallengeEvent(new LichessChallenge("c1", "AlarmBot", false, "standard", "bullet", "alarm-bot", "Iceeman", "1+0")),
            new ChallengeEndedEvent("c1", "canceled")
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

    [Test]
    public async Task AutomaticallyChallengesEligibleOnlineBotWithConfiguredProfile()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var lichess = new ServiceLichessClient([]) { OnlineBots = [new OnlineBot("alarm-bot", "AlarmBot"), new OnlineBot("opponent", "OpponentBot")] };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 1,
            AutoChallengeStatePath = historyPath,
            Profiles =
            [
                new AutoBotChallengeProfileOptions { Name = "Disabled Bullet", Mode = "casual", Variant = "standard", InitialSeconds = 60, IncrementSeconds = 1 },
                new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz", Mode = "casual", Variant = "standard", InitialSeconds = 180, IncrementSeconds = 2 }
            ]
        };
        var service = CreateService(lichess, new RecordingGameController(), options);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.ChallengeCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForFileAsync(historyPath);
            await service.StopAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(lichess.CreatedChallenges, Is.EqualTo(new[]
                {
                    ("OpponentBot", new BotChallengeRequest(false, "standard", 180, 2))
                }));
                Assert.That(File.ReadAllText(historyPath), Does.Contain("opponent"));
                Assert.That(File.ReadAllText(historyPath), Does.Contain("Cooldown after challenging OpponentBot with profile Casual Blitz"));
            });
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    [TestCase("casual", "only accepts casual games")]
    [TestCase("rated", "only accepts rated games")]
    [TestCase("tooFast", "the time control is too fast")]
    [TestCase("noBot", "challenges from bots are not accepted")]
    [TestCase("somethingNew", "Lichess reported 'somethingNew'")]
    public void DescribesAutomaticChallengeDeclineReason(string reason, string expected)
    {
        Assert.That(LichessBridgeService.DescribeDeclineReason(reason), Is.EqualTo(expected));
    }

    [Test]
    public async Task LoadsPersistentOpponentCooldownBeforeMatchmakingStarts()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero));
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var expiresAt = timeProvider.GetUtcNow().AddHours(1).ToString("O");
        await File.WriteAllTextAsync(historyPath, $"{{\"OpponentCooldowns\":[{{\"Key\":\"opponent\",\"ExpiresAt\":\"{expiresAt}\"}}],\"DeclineRestrictions\":[]}}");
        var lichess = new ServiceLichessClient([]) { OnlineBots = [new OnlineBot("opponent", "OpponentBot")] };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 1,
            AutoChallengeStatePath = historyPath,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz" }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options, timeProvider: timeProvider);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.AllEventsRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await AdvanceAsync(timeProvider, TimeSpan.FromSeconds(2));
            await service.StopAsync(CancellationToken.None);

            Assert.That(lichess.CreatedChallenges, Is.Empty);
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    [Test]
    public async Task AutomaticallyChallengesOnlyBotMatchingConfiguredRatingLimits()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var blitz = "blitz";
        var lichess = new ServiceLichessClient([])
        {
            Account = new LichessAccount("alarm-bot", "AlarmBot") { Ratings = [new LichessRating(blitz, 1700)] },
            OnlineBots =
            [
                new OnlineBot("below", "Below") { Ratings = [new LichessRating(blitz, 1400)] },
                new OnlineBot("above", "Above") { Ratings = [new LichessRating(blitz, 2100)] },
                new OnlineBot("different", "Different") { Ratings = [new LichessRating(blitz, 1950)] },
                new OnlineBot("unrated", "Unrated"),
                new OnlineBot("eligible", "Eligible") { Ratings = [new LichessRating(blitz, 1800)] }
            ]
        };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 1,
            AutoChallengeStatePath = historyPath,
            MinRating = 1500,
            MaxRating = 2000,
            MaxRatingDifference = 200,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz", InitialSeconds = 180, IncrementSeconds = 2 }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.ChallengeCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.StopAsync(CancellationToken.None);

            Assert.That(lichess.CreatedChallenges.Select(challenge => challenge.Username), Is.EqualTo(new[] { "Eligible" }));
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    [Test]
    public async Task AutomaticallyChallengesBotClosestToOwnRatingForProfileTimeControl()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var lichess = new ServiceLichessClient([])
        {
            Account = new LichessAccount("alarm-bot", "AlarmBot")
            {
                Ratings = [new LichessRating("blitz", 1700), new LichessRating("rapid", 2200)]
            },
            OnlineBots =
            [
                new OnlineBot("closest-blitz", "ClosestBlitz") { Ratings = [new LichessRating("blitz", 1701), new LichessRating("rapid", 1800)] },
                new OnlineBot("closest-rapid", "ClosestRapid") { Ratings = [new LichessRating("blitz", 1900), new LichessRating("rapid", 2210)] },
                new OnlineBot("unrated", "Unrated")
            ]
        };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 1,
            AutoChallengeStatePath = historyPath,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Rated Rapid", Mode = "rated", InitialSeconds = 600 }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.ChallengeCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.StopAsync(CancellationToken.None);

            Assert.That(lichess.CreatedChallenges.Select(challenge => challenge.Username), Is.EqualTo(new[] { "ClosestRapid" }));
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    [Test]
    public async Task ProfileRatingLimitOverridesGlobalLimit()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var lichess = new ServiceLichessClient([])
        {
            OnlineBots = [new OnlineBot("eligible", "Eligible") { Ratings = [new LichessRating("blitz", 1800)] }]
        };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 1,
            AutoChallengeStatePath = historyPath,
            MinRating = 1900,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz", InitialSeconds = 180, IncrementSeconds = 2, MinRating = 1700 }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.ChallengeCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.StopAsync(CancellationToken.None);

            Assert.That(lichess.CreatedChallenges.Select(challenge => challenge.Username), Is.EqualTo(new[] { "Eligible" }));
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    [Test]
    public async Task ShowsCountdownUntilAutomaticChallengeSearchStarts()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var statusDisplay = new RecordingStatusDisplay();
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 10,
            AutoChallengeStatePath = historyPath,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz" }]
        };
        var service = CreateService(new ServiceLichessClient([]), new RecordingGameController(), options, statusDisplay);

        await service.StartAsync(CancellationToken.None);
        await Task.Yield();
        await service.StopAsync(CancellationToken.None);

        Assert.That(statusDisplay.Statuses, Does.Contain("Waiting for challenges... Auto challenge in 10s"));
    }

    [Test]
    public async Task CachesOnlineBotsBetweenAutomaticChallengeSearches()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero));
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var lichess = new ServiceLichessClient([])
        {
            OnlineBots = [new OnlineBot("alarm-bot", "AlarmBot")]
        };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 1,
            OnlineBotsCacheSeconds = 60,
            AutoChallengeStatePath = historyPath,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz" }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options, timeProvider: timeProvider);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.AllEventsRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await AdvanceAsync(timeProvider, TimeSpan.FromSeconds(3));
            await service.StopAsync(CancellationToken.None);

            Assert.That(lichess.OnlineBotsRequestCount, Is.EqualTo(1));
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    [Test]
    public async Task PersistsImmediateChallengeRejectionWithoutFailingMatchmakingLoop()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var lichess = new ServiceLichessClient([])
        {
            OnlineBots = [new OnlineBot("opponent", "OpponentBot")],
            ChallengeException = new ChallengeCreationException(System.Net.HttpStatusCode.BadRequest, "This player does not accept casual challenges")
        };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 1,
            AutoChallengeStatePath = historyPath,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz" }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.ChallengeCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForFileAsync(historyPath);
            await service.StopAsync(CancellationToken.None);

            Assert.That(File.ReadAllText(historyPath), Does.Contain("OpponentBot rejected the challenge request: This player does not accept casual challenges"));
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    [TestCase("declined")]
    [TestCase("canceled")]
    public async Task WaitsForIdleDelayAfterOutgoingChallengeEnds(string reason)
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero));
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var lichess = new ServiceLichessClient([
            new ChallengeEvent(new LichessChallenge("declined", "AlarmBot", false, "standard", "blitz", "alarm-bot", "FirstBot")),
            new ChallengeEndedEvent("declined", reason, reason == "declined" ? "rated" : string.Empty)
        ])
        {
            OnlineBots = [new OnlineBot("next", "NextBot")]
        };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 30,
            AutoChallengeStatePath = historyPath,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz" }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options, timeProvider: timeProvider);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.AllEventsRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.That(lichess.CreatedChallenges, Is.Empty);

            timeProvider.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(20);
            Assert.That(lichess.CreatedChallenges, Is.Empty);

            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await lichess.ChallengeCreated.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await service.StopAsync(CancellationToken.None);

            Assert.That(lichess.CreatedChallenges.Select(challenge => challenge.Username), Is.EqualTo(new[] { "NextBot" }));
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    [Test]
    public async Task WaitsForIdleDelayAfterTimeout()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero));
        var historyPath = Path.Combine(Path.GetTempPath(), $"auto-bot-challenge-{Guid.NewGuid():N}.json");
        var lichess = new ServiceLichessClient([])
        {
            OnlineBots = [new OnlineBot("first", "FirstBot"), new OnlineBot("second", "SecondBot")]
        };
        var options = new AutoBotChallengeOptions
        {
            Enabled = true,
            IdleDelaySeconds = 3,
            ChallengeTimeoutSeconds = 5,
            OnlineBotsCacheSeconds = 60,
            AutoChallengeStatePath = historyPath,
            Profiles = [new AutoBotChallengeProfileOptions { Enabled = true, Name = "Casual Blitz" }]
        };
        var service = CreateService(lichess, new RecordingGameController(), options, timeProvider: timeProvider);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await lichess.AllEventsRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
            timeProvider.Advance(TimeSpan.FromSeconds(3));
            await lichess.ChallengeCreated.Task.WaitAsync(TimeSpan.FromSeconds(1));

            for (var second = 0; second < 10 && lichess.TwoChallengesCreated.Task.IsCompleted is false; second++)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10);
            }

            await lichess.TwoChallengesCreated.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await service.StopAsync(CancellationToken.None);

            Assert.That(lichess.CreatedChallenges.Select(challenge => challenge.Username).Distinct().Count(), Is.EqualTo(2));
        }
        finally
        {
            File.Delete(historyPath);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(2);

        while (File.Exists(path) is false && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(20);
        }

        Assert.That(File.Exists(path), Is.True);
    }

    private static async Task AdvanceAsync(FakeTimeProvider timeProvider, TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromSeconds(1))
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
    }

    private static LichessBridgeService CreateService(ServiceLichessClient lichess, IGameController gameController, AutoBotChallengeOptions? autoBotChallenge = null, RecordingStatusDisplay? statusDisplay = null, TimeProvider? timeProvider = null)
    {
        var filter = new ChallengeFilter(Options.Create(new LichessOptions
        {
            ChallengeFilter = new ChallengeFilterOptions
            {
                Modes = ["casual"],
                Variants = ["standard"],
                Speeds = ["blitz", "rapid"]
            }
        }));
        var engineOptions = Options.Create(new EngineOptions { Path = "fake-engine", Name = "Alarm", Ponder = true });
        var lichessOptions = Options.Create(new LichessOptions
        {
            ChallengeFilter = new ChallengeFilterOptions
            {
                Modes = ["casual"],
                Variants = ["standard"],
                Speeds = ["blitz", "rapid"]
            }
        });

        var autoChallengeOptions = autoBotChallenge ?? new AutoBotChallengeOptions();

        return new LichessBridgeService(lichess, filter, gameController, statusDisplay ?? new RecordingStatusDisplay(), new QuitController(), new AutoChallengeController(autoChallengeOptions.Enabled), new RecordingApplicationLifetime(), engineOptions, lichessOptions, Options.Create(autoChallengeOptions), timeProvider ?? TimeProvider.System, NullLogger<LichessBridgeService>.Instance);
    }

    private sealed class ServiceLichessClient(IReadOnlyList<AccountEvent> events) : ILichessClient
    {
        public List<string> Accepted { get; } = [];
        public List<(string Id, string Reason)> Declined { get; } = [];
        public IReadOnlyList<OnlineBot> OnlineBots { get; init; } = [];
        public int OnlineBotsRequestCount { get; private set; }
        public LichessAccount Account { get; init; } = new("alarm-bot", "AlarmBot");
        public Queue<LichessAccount>? AccountResponses { get; init; }
        public List<(string Username, BotChallengeRequest Challenge)> CreatedChallenges { get; } = [];
        public TaskCompletionSource DeclinedCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllEventsRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChallengeCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoChallengesCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ChallengeException { get; init; }

        public Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken) => Task.FromResult(AccountResponses is { Count: > 0 } ? AccountResponses.Dequeue() : Account);

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

        public Task SendChatMessageAsync(string gameId, string message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AbortGameAsync(string gameId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OnlineBot>> GetOnlineBotsAsync(CancellationToken cancellationToken)
        {
            OnlineBotsRequestCount++;
            return Task.FromResult(OnlineBots);
        }

        public Task<string> CreateChallengeAsync(string username, BotChallengeRequest challenge, CancellationToken cancellationToken)
        {
            CreatedChallenges.Add((username, challenge));
            ChallengeCreated.TrySetResult();

            if (CreatedChallenges.Count >= 2)
            {
                TwoChallengesCreated.TrySetResult();
            }

            if (ChallengeException is not null)
            {
                return Task.FromException<string>(ChallengeException);
            }

            return Task.FromResult("auto-challenge");
        }

        public Task CancelChallengeAsync(string challengeId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingGameController : IGameController
    {
        public List<string> GameIds { get; } = [];
        public List<string> FinishedGameIds { get; } = [];
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GameCompletion Completion { get; init; } = new();

        public Task<GameCompletion> PlayAsync(string gameId, LichessAccount account, CancellationToken cancellationToken)
        {
            GameIds.Add(gameId);
            Called.TrySetResult();
            return Task.FromResult(Completion);
        }

        public bool NotifyGameFinished(string gameId)
        {
            FinishedGameIds.Add(gameId);

            return true;
        }
    }

    private sealed class RecordingStatusDisplay : IStatusDisplay
    {
        public List<string> Statuses { get; } = [];
        public List<StatusHeader> Headers { get; } = [];
        public TaskCompletionSource HeaderUpdated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CountdownShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ShowHeader(StatusHeader header)
        {
            Headers.Add(header);

            if (Headers.Count >= 2)
            {
                HeaderUpdated.TrySetResult();
            }
        }

        public void ShowGame(GameStatus game)
        {
        }

        public void ShowAnalysis(EngineAnalysis? analysis)
        {
        }

        public void ShowSearchStatus(EngineSearchStatus? searchStatus)
        {
        }

        public void ShowHint(string? hint)
        {
        }

        public void ClearGame()
        {
        }

        public void Show(string status)
        {
            Statuses.Add(status);

            if (status.StartsWith("Waiting for challenges... Auto challenge in ", StringComparison.Ordinal))
            {
                CountdownShown.TrySetResult();
            }
        }

        public void ShowQuit(bool requested)
        {
        }

        public void ShowLogs(IReadOnlyList<DashboardLogEntry> entries)
        {
        }
    }

    private sealed class RecordingApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication()
        {
            stopping.Cancel();
        }
    }
}
