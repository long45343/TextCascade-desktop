using System.Net;
using System.Net.WebSockets;
using System.Text;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

public class StompClientTests
{
    private static string SentText(FakeWebSocketTransport transport, int index)
    {
        return Encoding.UTF8.GetString(transport.Sent[index]);
    }

    private static async Task<StompClient> ConnectAsync(
        FakeWebSocketTransport transport,
        TestStompListener listener,
        ManualTimeProvider? time = null)
    {
        var client = new StompClient(
            "ws://localhost:8080/clipsocket",
            "JSESSIONID=test",
            listener,
            () => transport,
            time);
        await client.ConnectAsync(CancellationToken.None);
        return client;
    }

    [Fact]
    public async Task Connect_SendsConnectFrame_WithHeartbeatHeader()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);

        await TestHelpers.WaitUntil(() => transport.Sent.Count > 0);

        var connect = SentText(transport, 0);
        Assert.StartsWith("CONNECT\n", connect, StringComparison.Ordinal);
        Assert.Contains("heart-beat:0,20000\n", connect, StringComparison.Ordinal);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Subscribe_UsesIncrementingIds()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);

        await client.SubscribeAsync("/user/queue/cliptext", CancellationToken.None);
        await client.SubscribeAsync("/user/queue/cliptext", CancellationToken.None);

        var frames = transport.Sent.Select(static bytes => Encoding.UTF8.GetString(bytes)).ToArray();
        Assert.Contains(frames, static f => f.Contains("id:sub-1\n", StringComparison.Ordinal));
        Assert.Contains(frames, static f => f.Contains("id:sub-2\n", StringComparison.Ordinal));

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Receive_FragmentedFrame_AcrossMessages_Parses()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);

        transport.Enqueue("MESSAGE\ndestination:/user/queue/cliptext\ncontent-length:5\n\nhel");
        transport.Enqueue("lo\0");

        await listener.MessageCount(1);
        Assert.Equal("hello", listener.Messages[0]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Receive_MultipleFrames_InSingleMessage_AllDispatched()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);

        transport.Enqueue("MESSAGE\ncontent-length:2\n\nhi\0MESSAGE\ncontent-length:3\n\nyo!\0");

        await TestHelpers.WaitUntil(() => listener.Messages.Count == 2);
        Assert.Equal(new[] { "hi", "yo!" }, listener.Messages);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Receive_HeartbeatOnlyMessage_NoEcho()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);

        transport.Enqueue("\n");
        await Task.Delay(50);

        Assert.Single(transport.Sent);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Send_SerializesConcurrentSends()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            client.SendAsync("/app/cliptext", "message-" + i, CancellationToken.None)));

        var sends = transport.Sent.Skip(1).Select(static bytes => Encoding.UTF8.GetString(bytes)).ToArray();
        Assert.Equal(50, sends.Length);
        Assert.All(sends, static frame =>
        {
            Assert.StartsWith("SEND\n", frame, StringComparison.Ordinal);
            Assert.EndsWith("\0", frame, StringComparison.Ordinal);
        });

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Watchdog_SilenceBeyondThreshold_AbortsOnce()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var time = new ManualTimeProvider();
        var client = await ConnectAsync(transport, listener, time);

        transport.Enqueue("CONNECTED\nheart-beat:10000,10000\n\n\0");
        await listener.ConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, transport.AbortCount);

        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(1, transport.AbortCount);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Watchdog_RegularHeartbeats_NeverAborts()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var time = new ManualTimeProvider();
        var client = await ConnectAsync(transport, listener, time);

        transport.Enqueue("CONNECTED\nheart-beat:10000,10000\n\n\0");
        await listener.ConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 15; i++)
        {
            transport.Enqueue("\n");
            await Task.Delay(10);
            time.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, transport.AbortCount);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Watchdog_NoHeartbeatHeader_UsesFallbackTimeout()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var time = new ManualTimeProvider();
        var client = await ConnectAsync(transport, listener, time);

        transport.Enqueue("CONNECTED\n\n\0");
        await listener.ConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(110));
        Assert.Equal(0, transport.AbortCount);

        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, transport.AbortCount);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task HeartbeatFrame_NoEchoSent()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);

        transport.Enqueue("\r\n");
        await Task.Delay(50);

        Assert.Single(transport.Sent);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Connect_Timeout_RaisesOperationCanceled()
    {
        var transport = new FakeWebSocketTransport { BlockConnect = true };
        var listener = new TestStompListener();
        var originalTimeout = StompClient.HandshakeTimeout;
        StompClient.HandshakeTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            var client = new StompClient("ws://localhost/clipsocket", "cookie", listener, () => transport);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(CancellationToken.None));
        }
        finally
        {
            StompClient.HandshakeTimeout = originalTimeout;
        }
    }

    [Fact]
    public void ConnectedFrame_ParsesServerHeartbeatHeader()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(20_000),
            StompClient.ComputeServerHeartbeatInterval(new Dictionary<string, string>
            {
                ["heart-beat"] = "10000,10000"
            }));
        Assert.Null(StompClient.ComputeServerHeartbeatInterval(new Dictionary<string, string>
        {
            ["heart-beat"] = "0,0"
        }));
        Assert.Null(StompClient.ComputeServerHeartbeatInterval(new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Handshake_401_403_ThrowsSessionExpired(HttpStatusCode status)
    {
        var transport = new FakeWebSocketTransport(status);
        var listener = new TestStompListener();
        var client = new StompClient("ws://localhost/clipsocket", "cookie", listener, () => transport);

        await Assert.ThrowsAsync<SessionExpiredException>(() => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Handshake500_FallsBackToGenericError()
    {
        var transport = new FakeWebSocketTransport(HttpStatusCode.InternalServerError);
        var listener = new TestStompListener();
        var client = new StompClient("ws://localhost/clipsocket", "cookie", listener, () => transport);

        await Assert.ThrowsAnyAsync<WebSocketException>(() => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MalformedFrame_Skipped_ValidFrameStillDispatched()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);

        transport.Enqueue("BOGUS\ncontent-length:-2\n\n\0");
        transport.Enqueue("MESSAGE\ncontent-length:2\n\nhi\0");

        await listener.MessageCount(1);
        Assert.Equal(0, listener.OnErrorCount);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task OversizeMessage_ConnectionClosed_NoOom()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);
        var original = StompClient.MaxWebSocketMessageBytes;
        StompClient.MaxWebSocketMessageBytes = 100;
        try
        {
            transport.Enqueue(new string('x', 200));
            var error = await listener.ErrorTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("size cap", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            StompClient.MaxWebSocketMessageBytes = original;
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnterminatedFrameBuffer_ExceedsCap_ConnectionClosed()
    {
        var transport = new FakeWebSocketTransport();
        var listener = new TestStompListener();
        var client = await ConnectAsync(transport, listener);
        var original = StompClient.MaxReceiveBufferChars;
        StompClient.MaxReceiveBufferChars = 20;
        try
        {
            transport.Enqueue("abcdefghij");
            transport.Enqueue("klmnopqrst");
            transport.Enqueue("uvwxyz");

            var error = await listener.ErrorTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("buffer exceeded", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            StompClient.MaxReceiveBufferChars = original;
            await client.DisposeAsync();
        }
    }
}

internal static class StompListenerExtensions
{
    public static async Task MessageCount(this TestStompListener listener, int count)
    {
        await TestHelpers.WaitUntil(() => listener.Messages.Count >= count);
    }
}
