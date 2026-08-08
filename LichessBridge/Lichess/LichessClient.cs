using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LichessBridge.Lichess;

public sealed class LichessClient(
    HttpClient httpClient,
    IOptions<LichessOptions> options,
    ILogger<LichessClient> logger) : ILichessClient
{
    private readonly LichessOptions _options = options.Value;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private DateTimeOffset _rateLimitedUntil = DateTimeOffset.MinValue;

    public async Task<LichessAccount> GetAccountAsync(CancellationToken cancellationToken)
    {
        using var response = await SendGetWithRateLimitAsync("/api/account", "application/json", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new LichessAccount(
            RequiredString(root, "id"),
            RequiredString(root, "username"));
    }

    public async IAsyncEnumerable<AccountEvent> StreamAccountEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var json in ReadNdjsonAsync("/api/stream/event", cancellationToken))
            yield return ParseAccountEvent(json);
    }

    public async IAsyncEnumerable<GameStreamEvent> StreamGameAsync(
        string gameId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var json in ReadNdjsonAsync($"/api/bot/game/stream/{Uri.EscapeDataString(gameId)}", cancellationToken))
            yield return ParseGameEvent(json);
    }

    public Task AcceptChallengeAsync(string challengeId, CancellationToken cancellationToken) =>
        SendMutationAsync($"/api/challenge/{Uri.EscapeDataString(challengeId)}/accept", null, cancellationToken);

    public Task DeclineChallengeAsync(string challengeId, string reason, CancellationToken cancellationToken) =>
        SendMutationAsync(
            $"/api/challenge/{Uri.EscapeDataString(challengeId)}/decline",
            new Dictionary<string, string> { ["reason"] = reason },
            cancellationToken);

    public Task PlayMoveAsync(string gameId, ChessMove move, CancellationToken cancellationToken) =>
        SendMutationAsync(
            $"/api/bot/game/{Uri.EscapeDataString(gameId)}/move/{move.Value}",
            null,
            cancellationToken);

    public async Task<string> ExportPgnAsync(string gameId, CancellationToken cancellationToken)
    {
        using var response = await SendGetWithRateLimitAsync(
            $"/game/export/{Uri.EscapeDataString(gameId)}?literate=0&clocks=1",
            "application/x-chess-pgn",
            cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async IAsyncEnumerable<JsonElement> ReadNdjsonAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            RegisterRateLimit(response);
            await WaitForRateLimitAsync(cancellationToken);
            throw new HttpRequestException("Lichess stream was rate limited.", null, response.StatusCode);
        }
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private async Task SendMutationAsync(
        string path,
        IReadOnlyDictionary<string, string>? form,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitForRateLimitAsync(cancellationToken);
            await _mutationLock.WaitAsync(cancellationToken);
            try
            {
                using var request = CreateRequest(HttpMethod.Post, path);
                if (form is not null)
                    request.Content = new FormUrlEncodedContent(form);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    RegisterRateLimit(response);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return;
            }
            finally
            {
                _mutationLock.Release();
            }
        }
    }

    private async Task<HttpResponseMessage> SendGetWithRateLimitAsync(
        string path,
        string accept,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitForRateLimitAsync(cancellationToken);
            using var request = CreateRequest(HttpMethod.Get, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                response.EnsureSuccessStatusCode();
                return response;
            }

            RegisterRateLimit(response);
            response.Dispose();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_options.BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        request.Headers.UserAgent.ParseAdd("LichessBridge/1.0");
        return request;
    }

    private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        var delay = _rateLimitedUntil - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
    }

    private void RegisterRateLimit(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
        if (retryAfter < TimeSpan.FromMinutes(1))
            retryAfter = TimeSpan.FromMinutes(1);
        _rateLimitedUntil = DateTimeOffset.UtcNow.Add(retryAfter);
        logger.LogWarning("Lichess rate limit reached; pausing API calls for {Delay}", retryAfter);
    }

    private static AccountEvent ParseAccountEvent(JsonElement root)
    {
        var type = RequiredString(root, "type");
        return type switch
        {
            "challenge" => new ChallengeEvent(ParseChallenge(root.GetProperty("challenge"))),
            "gameStart" => new GameStartEvent(RequiredString(root.GetProperty("game"), "id")),
            "gameFinish" => new GameFinishEvent(RequiredString(root.GetProperty("game"), "id")),
            _ => new UnknownAccountEvent(type)
        };
    }

    private static LichessChallenge ParseChallenge(JsonElement challenge) => new(
        RequiredString(challenge, "id"),
        OptionalString(challenge.GetProperty("challenger"), "name") ?? "Anonymous",
        challenge.GetProperty("rated").GetBoolean(),
        RequiredString(challenge.GetProperty("variant"), "key"),
        RequiredString(challenge, "speed"));

    private static GameStreamEvent ParseGameEvent(JsonElement root)
    {
        return RequiredString(root, "type") switch
        {
            "gameFull" => new GameFullEvent(
                RequiredString(root, "id"),
                PlayerId(root.GetProperty("white")),
                PlayerName(root.GetProperty("white")),
                PlayerId(root.GetProperty("black")),
                PlayerName(root.GetProperty("black")),
                OptionalString(root, "initialFen") ?? "startpos",
                root.GetProperty("clock").GetProperty("initial").GetInt32(),
                root.GetProperty("clock").GetProperty("increment").GetInt32(),
                ParseGameState(root.GetProperty("state"))),
            "gameState" => new GameStateEvent(ParseGameState(root)),
            var type => new UnknownGameEvent(type)
        };
    }

    private static GameState ParseGameState(JsonElement root)
    {
        var moves = (OptionalString(root, "moves") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => new ChessMove(value))
            .ToArray();
        ChessColor? winner = OptionalString(root, "winner") switch
        {
            "white" => ChessColor.White,
            "black" => ChessColor.Black,
            _ => null
        };
        return new GameState(
            moves,
            root.GetProperty("wtime").GetInt32(),
            root.GetProperty("btime").GetInt32(),
            RequiredString(root, "status"),
            winner);
    }

    private static string PlayerId(JsonElement player) =>
        OptionalString(player, "id") ??
        (player.TryGetProperty("user", out var user) ? OptionalString(user, "id") : null) ??
        string.Empty;

    private static string PlayerName(JsonElement player) =>
        OptionalString(player, "name") ??
        (player.TryGetProperty("user", out var user) ? OptionalString(user, "name") : null) ??
        "Anonymous";

    private static string RequiredString(JsonElement element, string property) =>
        OptionalString(element, property) ?? throw new JsonException($"Missing property '{property}'.");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
