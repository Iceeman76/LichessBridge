using System.Net;
using System.Diagnostics;
using System.Text;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Lichess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DotNetLab.LichessBridge.Tests;

public sealed class LichessClientTests
{
    [Test]
    public async Task TimestampsGameEventsBeforeLoggingAndKeepsReceivingWhileConsumerIsBusy()
    {
        const string body = "{\"type\":\"gameState\",\"moves\":\"\",\"wtime\":1000,\"btime\":2000,\"winc\":100,\"binc\":200,\"status\":\"started\"}\n{\"type\":\"gameState\",\"moves\":\"e2e4\",\"wtime\":1050,\"btime\":2000,\"status\":\"started\"}\n";
        using var http = new HttpClient(new ScriptedHandler(Ndjson(body)));
        var logger = new ReceiveLogger();
        var client = new LichessClient(http, Options.Create(new LichessOptions { BaseUri = new Uri("https://test.invalid"), Token = "test-token" }), logger);
        await using var events = client.StreamGameAsync("local", CancellationToken.None).GetAsyncEnumerator();
        Assert.That(await events.MoveNextAsync(), Is.True);
        var first = (GameStateEvent)events.Current;
        await logger.SecondPayload.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var consumerResumed = Stopwatch.GetTimestamp();
        Assert.That(await events.MoveNextAsync(), Is.True);
        var second = events.Current;

        Assert.Multiple(() =>
        {
            Assert.That(first.ReceivedTimestamp, Is.LessThanOrEqualTo(logger.PayloadTimestamps[0]));
            Assert.That(second.ReceivedTimestamp, Is.LessThanOrEqualTo(logger.PayloadTimestamps[1]));
            Assert.That(second.ReceivedTimestamp, Is.LessThanOrEqualTo(consumerResumed));
            Assert.That(first.ReceivedUtc.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(first.State.WhiteIncrementMilliseconds, Is.EqualTo(100));
            Assert.That(first.State.BlackIncrementMilliseconds, Is.EqualTo(200));
        });
    }

    [TestCase(0, 62)]
    [TestCase(600, 600)]
    public async Task PausesRequestsAfterRateLimitAndHonorsLongerServerDelay(int serverDelaySeconds, int expectedDelaySeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(serverDelaySeconds));
        var handler = new ScriptedHandler(response);
        using var httpClient = new HttpClient(handler);
        var logger = new RateLimitLogger();
        var client = new LichessClient(httpClient, Options.Create(new LichessOptions { Token = "test-token", BaseUri = new Uri("https://test.invalid") }), logger);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = client.GetAccountAsync(cancellation.Token);

        try
        {
            var delay = await logger.Delay.Task.WaitAsync(cancellation.Token);

            Assert.Multiple(() =>
            {
                Assert.That(delay, Is.EqualTo(TimeSpan.FromSeconds(expectedDelaySeconds)));
                Assert.That(handler.Requests, Has.Count.EqualTo(1));
                Assert.That(request.IsCompleted, Is.False);
            });
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await request);
        }
    }

    [Test]
    public async Task GetsAuthenticatedBotAccount()
    {
        var handler = new ScriptedHandler(Json("{\"id\":\"alarm-bot\",\"username\":\"AlarmBot\",\"perfs\":{\"bullet\":{\"games\":78,\"rating\":1542,\"prov\":true},\"blitz\":{\"rating\":1687},\"chess960\":{\"rating\":1800}}}"));
        var client = CreateClient(handler);

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(account.Id, Is.EqualTo("alarm-bot"));
            Assert.That(account.Username, Is.EqualTo("AlarmBot"));
            Assert.That(account.Ratings, Is.EqualTo([new LichessRating("bullet", 1542, 78), new LichessRating("blitz", 1687), new LichessRating("chess960", 1800)]));
            Assert.That(handler.Requests[0].Path, Is.EqualTo("/api/account"));
            Assert.That(handler.Requests[0].Authorization, Is.EqualTo("Bearer test-token"));
            Assert.That(handler.Requests[0].UserAgent, Does.Contain("LichessBridge/1.0"));
        });
    }

    [Test]
    public async Task AccountStreamParsesKnownAndUnknownEventsAndIgnoresKeepAlive()
    {
        const string body = "\n" +
            "{\"type\":\"challenge\",\"challenge\":{\"id\":\"c1\",\"rated\":false,\"speed\":\"blitz\",\"variant\":{\"key\":\"standard\"},\"timeControl\":{\"type\":\"clock\",\"limit\":180,\"increment\":2,\"show\":\"3+2\"},\"challenger\":{\"id\":\"benny\",\"name\":\"Benny\",\"title\":\"BOT\",\"rating\":1812},\"destUser\":{\"id\":\"alarm-bot\",\"name\":\"AlarmBot\"}}}\n" +
            "{\"type\":\"gameStart\",\"game\":{\"id\":\"g1\"}}\n" +
            "{\"type\":\"gameFinish\",\"game\":{\"id\":\"g1\"}}\n" +
            "{\"type\":\"challengeDeclined\",\"challenge\":{\"id\":\"c1\",\"declineReasonKey\":\"timeControl\"}}\n\n";
        var client = CreateClient(new ScriptedHandler(Ndjson(body)));

        var events = await ReadAllAsync(client.StreamAccountEventsAsync(CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(4));
            Assert.That(events[0], Is.TypeOf<ChallengeEvent>());
            Assert.That(((ChallengeEvent)events[0]).Challenge.Challenger, Is.EqualTo("Benny"));
            Assert.That(((ChallengeEvent)events[0]).Challenge.ChallengerId, Is.EqualTo("benny"));
            Assert.That(((ChallengeEvent)events[0]).Challenge.Destination, Is.EqualTo("AlarmBot"));
            Assert.That(((ChallengeEvent)events[0]).Challenge.TimeControl, Is.EqualTo("3+2"));
            Assert.That(((ChallengeEvent)events[0]).Challenge.ChallengerIsBot, Is.True);
            Assert.That(((ChallengeEvent)events[0]).Challenge.InitialSeconds, Is.EqualTo(180));
            Assert.That(((ChallengeEvent)events[0]).Challenge.IncrementSeconds, Is.EqualTo(2));
            Assert.That(((ChallengeEvent)events[0]).Challenge.ChallengerRating, Is.EqualTo(1812));
            Assert.That(events[1], Is.EqualTo(new GameStartEvent("g1")));
            Assert.That(events[2], Is.EqualTo(new GameFinishEvent("g1")));
            Assert.That(events[3], Is.EqualTo(new ChallengeEndedEvent("c1", "declined", "timeControl")));
        });
    }

    [Test]
    public async Task GameStreamParsesFullStateUpdatesAndUnknownEvents()
    {
        const string body =
            "{\"type\":\"gameFull\",\"id\":\"g1\",\"rated\":true,\"speed\":\"blitz\",\"initialFen\":\"startpos\",\"white\":{\"id\":\"alarm-bot\",\"name\":\"AlarmBot\",\"title\":\"BOT\",\"rating\":1687},\"black\":{\"id\":\"opponent\",\"name\":\"Opponent\",\"title\":\"GM\",\"rating\":1750},\"clock\":{\"initial\":300000,\"increment\":3000},\"state\":{\"moves\":\"e2e4\",\"wtime\":299000,\"btime\":300000,\"status\":\"started\"}}\n" +
            "{\"type\":\"gameState\",\"moves\":\"e2e4 e7e5\",\"wtime\":299000,\"btime\":298000,\"status\":\"mate\",\"winner\":\"white\"}\n" +
            "{\"type\":\"chatLine\",\"username\":\"Opponent\",\"text\":\"gg\"}\n";
        var client = CreateClient(new ScriptedHandler(Ndjson(body)));

        var events = await ReadAllAsync(client.StreamGameAsync("g1", CancellationToken.None));
        var full = (GameFullEvent)events[0];
        var update = (GameStateEvent)events[1];

        Assert.Multiple(() =>
        {
            Assert.That(full.WhiteId, Is.EqualTo("alarm-bot"));
            Assert.That(full.InitialSeconds, Is.EqualTo(300));
            Assert.That(full.IncrementSeconds, Is.EqualTo(3));
            Assert.That(full.Rated, Is.True);
            Assert.That(full.Speed, Is.EqualTo("blitz"));
            Assert.That(full.WhiteRating, Is.EqualTo(1687));
            Assert.That(full.BlackRating, Is.EqualTo(1750));
            Assert.That(full.WhiteTitle, Is.EqualTo("BOT"));
            Assert.That(full.BlackTitle, Is.EqualTo("GM"));
            Assert.That(full.State.Moves.Select(move => move.Value), Is.EqualTo(["e2e4"]));
            Assert.That(update.State.Winner, Is.EqualTo(ChessColor.White));
            Assert.That(update.State.IsFinished, Is.True);
            Assert.That(events[2], Is.TypeOf<UnknownGameEvent>().And.Property(nameof(UnknownGameEvent.Type)).EqualTo("chatLine"));
        });
    }

    [Test]
    public async Task SendsChallengeMoveAndPgnRequestsWithExpectedContracts()
    {
        var pgnResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[Result \"1-0\"]", Encoding.UTF8, "application/x-chess-pgn") };
        var handler = new ScriptedHandler(Empty(), Empty(), Empty(), Empty(), Empty(), Empty(), pgnResponse);
        var client = CreateClient(handler);

        await client.AcceptChallengeAsync("challenge 1", CancellationToken.None);
        await client.DeclineChallengeAsync("challenge 2", "later", CancellationToken.None);
        await client.PlayMoveAsync("game 1", new ChessMove("e2e4"), CancellationToken.None);
        await client.AbortGameAsync("game 1", CancellationToken.None);
        await client.ResignGameAsync("game 1", CancellationToken.None);
        await client.SendChatMessageAsync("game 1", "Good luck!", CancellationToken.None);
        var pgn = await client.ExportPgnAsync("game 1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests.Select(request => request.Method), Is.EqualTo(["POST", "POST", "POST", "POST", "POST", "POST", "GET"]));
            Assert.That(handler.Requests[0].Path, Is.EqualTo("/api/challenge/challenge%201/accept"));
            Assert.That(handler.Requests[1].Body, Is.EqualTo("reason=later"));
            Assert.That(handler.Requests[2].Path, Is.EqualTo("/api/bot/game/game%201/move/e2e4"));
            Assert.That(handler.Requests[3].Path, Is.EqualTo("/api/bot/game/game%201/abort"));
            Assert.That(handler.Requests[4].Path, Is.EqualTo("/api/bot/game/game%201/resign"));
            Assert.That(handler.Requests[5].Path, Is.EqualTo("/api/bot/game/game%201/chat"));
            Assert.That(handler.Requests[5].Body, Is.EqualTo("room=player&text=Good+luck%21"));
            Assert.That(handler.Requests[6].PathAndQuery, Does.StartWith("/game/export/game%201?"));
            Assert.That(pgn, Is.EqualTo("[Result \"1-0\"]"));
        });
    }

    [Test]
    public async Task GetsOnlineBotsAndCreatesAndCancelsChallenge()
    {
        var onlineBots = Ndjson("{\"id\":\"bot-one\",\"username\":\"BotOne\",\"perfs\":{\"blitz\":{\"rating\":1725}}}\n{\"id\":\"bot-two\",\"username\":\"BotTwo\"}\n");
        var handler = new ScriptedHandler(onlineBots, Json("{\"id\":\"challenge-id\"}"), Empty());
        var client = CreateClient(handler);

        var bots = await client.GetOnlineBotsAsync(CancellationToken.None);
        var challengeId = await client.CreateChallengeAsync("Bot One", new BotChallengeRequest(false, "standard", 180, 2), CancellationToken.None);
        await client.CancelChallengeAsync(challengeId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(bots.Select(bot => bot.Id), Is.EqualTo(["bot-one", "bot-two"]));
            Assert.That(bots.Select(bot => bot.Username), Is.EqualTo(["BotOne", "BotTwo"]));
            Assert.That(bots[0].Ratings, Is.EqualTo([new LichessRating("blitz", 1725)]));
            Assert.That(bots[1].Ratings, Is.Empty);
            Assert.That(challengeId, Is.EqualTo("challenge-id"));
            Assert.That(handler.Requests[0].PathAndQuery, Is.EqualTo("/api/bot/online?nb=512"));
            Assert.That(handler.Requests[1].Path, Is.EqualTo("/api/challenge/Bot%20One"));
            Assert.That(handler.Requests[1].Body, Does.Contain("rated=false"));
            Assert.That(handler.Requests[1].Body, Does.Contain("clock.limit=180"));
            Assert.That(handler.Requests[1].Body, Does.Contain("clock.increment=2"));
            Assert.That(handler.Requests[2].Path, Is.EqualTo("/api/challenge/challenge-id/cancel"));
        });
    }

    [Test]
    public async Task ChallengeCreationIncludesLichessErrorDetails()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"This player does not accept casual challenges\"}", Encoding.UTF8, "application/json") };
        var client = CreateClient(new ScriptedHandler(response));

        var exception = await Assert.ThrowsAsync<ChallengeCreationException>(() => client.CreateChallengeAsync("Bot", new BotChallengeRequest(false, "standard", 180, 2), CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception?.ApiMessage, Is.EqualTo("This player does not accept casual challenges"));
        });
    }

    [Test]
    public async Task ChallengeRateLimitFailsPromptlyAndPreventsChallengeRequestsDuringCooldown()
    {
        var timeProvider = new FakeTimeProvider();
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("challenge limit") };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        var handler = new ScriptedHandler(response);
        using var httpClient = new HttpClient(handler);
        var logger = new RateLimitLogger();
        var client = new LichessClient(httpClient, Options.Create(new LichessOptions { Token = "test-token", BaseUri = new Uri("https://test.invalid") }), logger, timeProvider);
        var challenge = new BotChallengeRequest(false, "standard", 180, 2);
        var retryAt = timeProvider.GetUtcNow().AddMinutes(5);
        var exception = await Assert.ThrowsAsync<LichessRateLimitException>(() => client.CreateChallengeAsync("Bot", challenge, CancellationToken.None));
        await Assert.ThrowsAsync<LichessRateLimitException>(() => client.CreateChallengeAsync("OtherBot", challenge, CancellationToken.None));
        await Assert.ThrowsAsync<LichessRateLimitException>(() => client.AcceptChallengeAsync("incoming", CancellationToken.None));
        await Assert.ThrowsAsync<LichessRateLimitException>(() => client.DeclineChallengeAsync("incoming", "later", CancellationToken.None));
        await Assert.ThrowsAsync<LichessRateLimitException>(() => client.CancelChallengeAsync("outgoing", CancellationToken.None));
        await Assert.ThrowsAsync<LichessRateLimitException>(() => client.GetOnlineBotsAsync(CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception?.RetryAt, Is.EqualTo(retryAt));
            Assert.That(handler.Requests, Has.Count.EqualTo(1));
            Assert.That(logger.Messages.Last(), Does.Contain("/api/challenge/Bot").And.Contain("challenge limit").And.Contain("Retry-After: 300"));
        });
    }

    [Test]
    public async Task UnrelatedSuccessDoesNotResetChallengeBackoffAndSuccessfulCreationDoes()
    {
        var timeProvider = new FakeTimeProvider();
        var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests), Json("{\"id\":\"bot\",\"username\":\"Bot\"}"), new HttpResponseMessage(HttpStatusCode.TooManyRequests), Json("{\"id\":\"created\"}"), new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var httpClient = new HttpClient(handler);
        var client = new LichessClient(httpClient, Options.Create(new LichessOptions { Token = "test-token", BaseUri = new Uri("https://test.invalid") }), NullLogger<LichessClient>.Instance, timeProvider);
        var challenge = new BotChallengeRequest(false, "standard", 180, 2);
        await Assert.ThrowsAsync<LichessRateLimitException>(() => client.CreateChallengeAsync("FirstBot", challenge, CancellationToken.None));
        timeProvider.Advance(TimeSpan.FromSeconds(62));
        await client.GetAccountAsync(CancellationToken.None);
        var repeated = await Assert.ThrowsAsync<LichessRateLimitException>(() => client.CreateChallengeAsync("SecondBot", challenge, CancellationToken.None));
        Assert.That(repeated?.RetryAt, Is.EqualTo(timeProvider.GetUtcNow().AddSeconds(122)));
        timeProvider.Advance(TimeSpan.FromSeconds(122));
        await client.CreateChallengeAsync("ThirdBot", challenge, CancellationToken.None);
        var recovered = await Assert.ThrowsAsync<LichessRateLimitException>(() => client.CreateChallengeAsync("FourthBot", challenge, CancellationToken.None));

        Assert.That(recovered?.RetryAt, Is.EqualTo(timeProvider.GetUtcNow().AddSeconds(62)));
    }

    [Test]
    public async Task AcceptsChallengeAndPlaysAfterCooldownExpires()
    {
        var timeProvider = new FakeTimeProvider();
        var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests), Empty(), Empty());
        using var httpClient = new HttpClient(handler);
        var client = new LichessClient(httpClient, Options.Create(new LichessOptions { Token = "test-token", BaseUri = new Uri("https://test.invalid") }), NullLogger<LichessClient>.Instance, timeProvider);
        await Assert.ThrowsAsync<LichessRateLimitException>(() => client.CreateChallengeAsync("Bot", new BotChallengeRequest(false, "standard", 180, 2), CancellationToken.None));
        timeProvider.Advance(TimeSpan.FromSeconds(62));
        await client.AcceptChallengeAsync("incoming", CancellationToken.None);
        await client.PlayMoveAsync("game", new ChessMove("e2e4"), CancellationToken.None);

        Assert.That(handler.Requests.Select(request => request.Path), Is.EqualTo(["/api/challenge/Bot", "/api/challenge/incoming/accept", "/api/bot/game/game/move/e2e4"]));
    }

    private static LichessClient CreateClient(HttpMessageHandler handler) => new(new HttpClient(handler), Options.Create(new LichessOptions { Token = "test-token", BaseUri = new Uri("https://test.invalid") }), NullLogger<LichessClient>.Instance);

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Ndjson(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
    };

    private static HttpResponseMessage Empty() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Empty)
    };

    private sealed record RecordedRequest(string Method, string Path, string PathAndQuery, string? Authorization, string UserAgent, string? Body);

    private sealed class RateLimitLogger : ILogger<LichessClient>
    {
        public TaskCompletionSource<TimeSpan> Delay { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Messages { get; } = [];

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));

            if (state is IEnumerable<KeyValuePair<string, object?>> properties && properties.FirstOrDefault(property => property.Key == "Delay").Value is TimeSpan delay)
            {
                Delay.TrySetResult(delay);
            }
        }
    }

    private sealed class ReceiveLogger : ILogger<LichessClient>
    {
        public List<long> PayloadTimestamps { get; } = [];
        public TaskCompletionSource SecondPayload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties && properties.Any(property => property.Key is "Payload"))
            {
                PayloadTimestamps.Add(Stopwatch.GetTimestamp());

                if (PayloadTimestamps.Count is 2)
                {
                    SecondPayload.TrySetResult();
                }
            }
        }
    }

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var requestUri = request.RequestUri ?? throw new InvalidOperationException("The request URI is required.");
            Requests.Add(new RecordedRequest(request.Method.Method, requestUri.AbsolutePath, requestUri.PathAndQuery, request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString(), body));

            return _responses.Dequeue();
        }
    }
}
