using System.Net;
using System.Text;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Lichess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LichessBridge.Tests;

public sealed class LichessClientTests
{
    [Test]
    public async Task GetsAuthenticatedBotAccount()
    {
        var handler = new ScriptedHandler(Json("{\"id\":\"alarm-bot\",\"username\":\"AlarmBot\"}"));
        var client = CreateClient(handler);

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(account, Is.EqualTo(new LichessAccount("alarm-bot", "AlarmBot")));
            Assert.That(handler.Requests[0].Path, Is.EqualTo("/api/account"));
            Assert.That(handler.Requests[0].Authorization, Is.EqualTo("Bearer test-token"));
            Assert.That(handler.Requests[0].UserAgent, Does.Contain("LichessBridge/1.0"));
        });
    }

    [Test]
    public async Task AccountStreamParsesKnownAndUnknownEventsAndIgnoresKeepAlive()
    {
        const string body = "\n" +
            "{\"type\":\"challenge\",\"challenge\":{\"id\":\"c1\",\"rated\":false,\"speed\":\"blitz\",\"variant\":{\"key\":\"standard\"},\"challenger\":{\"name\":\"Benny\"}}}\n" +
            "{\"type\":\"gameStart\",\"game\":{\"id\":\"g1\"}}\n" +
            "{\"type\":\"gameFinish\",\"game\":{\"id\":\"g1\"}}\n" +
            "{\"type\":\"challengeCanceled\"}\n\n";
        var client = CreateClient(new ScriptedHandler(Ndjson(body)));

        var events = await ReadAllAsync(client.StreamAccountEventsAsync(CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(4));
            Assert.That(events[0], Is.TypeOf<ChallengeEvent>());
            Assert.That(((ChallengeEvent)events[0]).Challenge.Challenger, Is.EqualTo("Benny"));
            Assert.That(events[1], Is.EqualTo(new GameStartEvent("g1")));
            Assert.That(events[2], Is.EqualTo(new GameFinishEvent("g1")));
            Assert.That(events[3], Is.EqualTo(new UnknownAccountEvent("challengeCanceled")));
        });
    }

    [Test]
    public async Task GameStreamParsesFullStateUpdatesAndUnknownEvents()
    {
        const string body =
            "{\"type\":\"gameFull\",\"id\":\"g1\",\"initialFen\":\"startpos\",\"white\":{\"id\":\"alarm-bot\",\"name\":\"AlarmBot\"},\"black\":{\"id\":\"opponent\",\"name\":\"Opponent\"},\"clock\":{\"initial\":300,\"increment\":3},\"state\":{\"moves\":\"e2e4\",\"wtime\":299000,\"btime\":300000,\"status\":\"started\"}}\n" +
            "{\"type\":\"gameState\",\"moves\":\"e2e4 e7e5\",\"wtime\":299000,\"btime\":298000,\"status\":\"mate\",\"winner\":\"white\"}\n" +
            "{\"type\":\"chatLine\",\"username\":\"Opponent\",\"text\":\"gg\"}\n";
        var client = CreateClient(new ScriptedHandler(Ndjson(body)));

        var events = await ReadAllAsync(client.StreamGameAsync("g1", CancellationToken.None));
        var full = (GameFullEvent)events[0];
        var update = (GameStateEvent)events[1];

        Assert.Multiple(() =>
        {
            Assert.That(full.WhiteId, Is.EqualTo("alarm-bot"));
            Assert.That(full.State.Moves.Select(move => move.Value), Is.EqualTo(new[] { "e2e4" }));
            Assert.That(update.State.Winner, Is.EqualTo(ChessColor.White));
            Assert.That(update.State.IsFinished, Is.True);
            Assert.That(events[2], Is.EqualTo(new UnknownGameEvent("chatLine")));
        });
    }

    [Test]
    public async Task SendsChallengeMoveAndPgnRequestsWithExpectedContracts()
    {
        var pgnResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[Result \"1-0\"]", Encoding.UTF8, "application/x-chess-pgn") };
        var handler = new ScriptedHandler(Empty(), Empty(), Empty(), pgnResponse);
        var client = CreateClient(handler);

        await client.AcceptChallengeAsync("challenge 1", CancellationToken.None);
        await client.DeclineChallengeAsync("challenge 2", "later", CancellationToken.None);
        await client.PlayMoveAsync("game 1", new ChessMove("e2e4"), CancellationToken.None);
        var pgn = await client.ExportPgnAsync("game 1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests.Select(request => request.Method), Is.EqualTo(new[] { "POST", "POST", "POST", "GET" }));
            Assert.That(handler.Requests[0].Path, Is.EqualTo("/api/challenge/challenge%201/accept"));
            Assert.That(handler.Requests[1].Body, Is.EqualTo("reason=later"));
            Assert.That(handler.Requests[2].Path, Is.EqualTo("/api/bot/game/game%201/move/e2e4"));
            Assert.That(handler.Requests[3].PathAndQuery, Does.StartWith("/game/export/game%201?"));
            Assert.That(pgn, Is.EqualTo("[Result \"1-0\"]"));
        });
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
