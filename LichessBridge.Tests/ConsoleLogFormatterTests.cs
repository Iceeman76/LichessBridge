using Bridge;

namespace LichessBridge.Tests;

public sealed class ConsoleLogFormatterTests
{
    [TestCase("Game against TempoCore finished: 0-1 [GameId: 23xmyC1j]", "Game against TempoCore finished: 0-1")]
    [TestCase("Automatic challenge to TempoCore timed out and was canceled [ChallengeId: ZTylp1Vh]", "Automatic challenge to TempoCore timed out and was canceled")]
    [TestCase("Lichess stream '/api/bot/game/stream/23xmyC1j' was silent", "Lichess stream '/api/bot/game/stream/…' was silent")]
    [TestCase("POST /api/bot/game/23xmyC1j/resign failed", "POST /api/bot/game/…/resign failed")]
    [TestCase("POST /api/challenge/ZTylp1Vh/cancel failed", "POST /api/challenge/…/cancel failed")]
    [TestCase("GET /api/game/export/23xmyC1j failed", "GET /api/game/export/… failed")]
    [TestCase("Game '23xmyC1j' is already active.", "Game is already active.")]
    [TestCase("Bot account 'Alarm2001' is not a player in game '23xmyC1j'.", "Bot account 'Alarm2001' is not a player in game.")]
    [TestCase("Game against TempoCore: Alarm2001 plays e2e4 after 00:00:01", "Game against TempoCore: Alarm2001 plays e2e4 after 00:00:01")]
    public void RemovesIdentifiersFromConsoleText(string message, string expected)
    {
        Assert.That(ConsoleLogFormatter.Format(message), Is.EqualTo(expected));
    }
}
