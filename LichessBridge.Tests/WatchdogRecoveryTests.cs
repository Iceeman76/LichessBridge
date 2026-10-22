using System.Net;
using LichessBridge.Configuration;
using LichessBridge.Domain;
using LichessBridge.Engine;
using LichessBridge.Game;
using LichessBridge.Lichess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LichessBridge.Tests;

public sealed class WatchdogRecoveryTests
{
    [Test]
    public void EngineMoveTimeoutLeavesTimeForEmergencyMoveAndNetwork()
    {
        var timeout = WinBoardEngine.CalculateMoveTimeout(new ChessClock(300_000, 120_000), ChessColor.White, 500);

        Assert.That(timeout, Is.EqualTo(TimeSpan.FromMilliseconds(297_750)));
    }

    [TestCase(HttpStatusCode.BadGateway, true)]
    [TestCase(HttpStatusCode.ServiceUnavailable, true)]
    [TestCase(HttpStatusCode.GatewayTimeout, true)]
    [TestCase(HttpStatusCode.RequestTimeout, true)]
    [TestCase(HttpStatusCode.TooManyRequests, true)]
    [TestCase(HttpStatusCode.BadRequest, false)]
    [TestCase(HttpStatusCode.Unauthorized, false)]
    public void ClassifiesTransientLichessHttpFailures(HttpStatusCode statusCode, bool expected)
    {
        var exception = new HttpRequestException("test", null, statusCode);

        Assert.That(GameController.IsTransientLichessFailure(exception), Is.EqualTo(expected));
    }

    [Test]
    public void NetworkFailureWithoutStatusCodeIsTransient()
    {
        var exception = new HttpRequestException("connection reset");

        Assert.That(GameController.IsTransientLichessFailure(exception), Is.True);
    }

    [Test]
    public async Task SilentAccountStreamTriggersWatchdog()
    {
        using var httpClient = new HttpClient(new SilentStreamHandler());
        var options = Options.Create(new LichessOptions
        {
            Token = "test-token",
            BaseUri = new Uri("https://test.invalid"),
            StreamSilenceTimeoutSeconds = 1
        });
        var client = new LichessClient(httpClient, options, NullLogger<LichessClient>.Instance);

        var exception = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in client.StreamAccountEventsAsync(CancellationToken.None))
            {
            }
        });

        Assert.That(exception?.Message, Does.Contain("/api/stream/event"));
    }

    private sealed class SilentStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SilentStream())
            });
    }

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
