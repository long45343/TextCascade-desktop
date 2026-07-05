using System.Net.WebSockets;
using System.Text;

namespace TextCascadeSharp.Core;

public sealed class StompClient : IAsyncDisposable
{
    private const int ReceiveChunkBytes = 16 * 1024;
    private const int MaxRetainedReceiveChars = 64 * 1024;
    private const int MaxRetainedMessageBytes = 64 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private static readonly byte[] HeartbeatBytes = "\n"u8.ToArray();
    private readonly string _websocketUrl;
    private readonly string _cookieHeader;
    private readonly IStompListener _listener;
    private readonly StringBuilder _receiveBuffer = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private int _subscriptionCounter;
    private CancellationTokenSource? _cts;

    public StompClient(string websocketUrl, string cookieHeader, IStompListener listener)
    {
        _websocketUrl = websocketUrl;
        _cookieHeader = cookieHeader;
        _listener = listener;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_cookieHeader))
        {
            socket.Options.SetRequestHeader("Cookie", _cookieHeader);
        }
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket = socket;

        await socket.ConnectAsync(new Uri(_websocketUrl), cancellationToken).ConfigureAwait(false);
        await SendFrameAsync("CONNECT", new Dictionary<string, string>
        {
            ["host"] = _websocketUrl,
            ["accept-version"] = "1.0,1.1",
            ["heart-beat"] = "0,20000"
        }, string.Empty, cancellationToken).ConfigureAwait(false);
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public Task SubscribeAsync(string destination, CancellationToken cancellationToken)
    {
        return SendFrameAsync("SUBSCRIBE", new Dictionary<string, string>
        {
            ["id"] = "sub-" + Interlocked.Increment(ref _subscriptionCounter),
            ["destination"] = destination
        }, string.Empty, cancellationToken);
    }

    public Task SendAsync(string destination, string body, CancellationToken cancellationToken)
    {
        return SendFrameAsync("SEND", new Dictionary<string, string>
        {
            ["destination"] = destination
        }, body, cancellationToken);
    }

    public async Task CloseAsync()
    {
        _cts?.Cancel();
        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(CloseTimeout);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", closeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Abort();
            }
        }
        socket?.Dispose();
        _socket = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _cts?.Dispose();
    }

    private async Task SendFrameAsync(
        string command,
        Dictionary<string, string> headers,
        string body,
        CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        var text = new StompFrame(command, headers, body).Marshall();
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var buffer = new byte[ReceiveChunkBytes];
        using var message = new MemoryStream(ReceiveChunkBytes);
        var listenerNotified = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        listenerNotified = true;
                        await _listener.OnClosedAsync("remote close").ConfigureAwait(false);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                await HandleTextAsync(text).ConfigureAwait(false);
                ResetMessageBuffer(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            listenerNotified = true;
            await _listener.OnErrorAsync(error).ConfigureAwait(false);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !listenerNotified)
            {
                await _listener.OnClosedAsync("socket closed").ConfigureAwait(false);
            }
        }
    }

    private async Task HandleTextAsync(string text)
    {
        if (!string.IsNullOrEmpty(text) && text.All(static c => c is '\n' or '\r'))
        {
            await SendHeartbeatAsync().ConfigureAwait(false);
            return;
        }

        List<StompFrame> frames = [];
        lock (_receiveBuffer)
        {
            _receiveBuffer.Append(text);
            while (true)
            {
                var end = FindFrameTerminator(_receiveBuffer);
                if (end < 0)
                {
                    break;
                }
                var rawFrame = _receiveBuffer.ToString(0, end);
                _receiveBuffer.Remove(0, end + 1);
                if (!string.IsNullOrWhiteSpace(rawFrame))
                {
                    frames.Add(StompFrame.Parse(rawFrame));
                }
            }
        }
        TrimReceiveBuffer();

        foreach (var frame in frames)
        {
            switch (frame.Command)
            {
                case "CONNECTED":
                    await _listener.OnConnectedAsync().ConfigureAwait(false);
                    break;
                case "MESSAGE":
                    await _listener.OnMessageAsync(frame.Body).ConfigureAwait(false);
                    break;
                case "ERROR":
                    await _listener.OnErrorAsync(new InvalidOperationException(string.IsNullOrWhiteSpace(frame.Body) ? "STOMP error." : frame.Body)).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static int FindFrameTerminator(StringBuilder builder)
    {
        for (var index = 0; index < builder.Length; index++)
        {
            if (builder[index] == '\0')
            {
                return index;
            }
        }
        return -1;
    }

    private void TrimReceiveBuffer()
    {
        if (_receiveBuffer.Length == 0 && _receiveBuffer.Capacity > MaxRetainedReceiveChars)
        {
            _receiveBuffer.Capacity = MaxRetainedReceiveChars;
        }
    }

    private static void ResetMessageBuffer(MemoryStream message)
    {
        message.SetLength(0);
        if (message.Capacity > MaxRetainedMessageBytes)
        {
            message.Capacity = MaxRetainedMessageBytes;
        }
    }

    private Task SendHeartbeatAsync()
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || _cts?.IsCancellationRequested == true)
        {
            return Task.CompletedTask;
        }
        return socket.SendAsync(HeartbeatBytes, WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, _cts?.Token ?? CancellationToken.None).AsTask();
    }
}

public interface IStompListener
{
    Task OnConnectedAsync();

    Task OnMessageAsync(string body);

    Task OnClosedAsync(string reason);

    Task OnErrorAsync(Exception error);
}
