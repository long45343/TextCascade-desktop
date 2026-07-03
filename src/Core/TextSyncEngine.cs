using System.Text;
using System.Windows.Forms;

namespace TextCascadeSharp.Core;

public sealed class TextSyncEngine : IStompListener, IAsyncDisposable
{
    private readonly ClipConfig _config;
    private readonly SynchronizationContext _uiContext;
    private readonly Action<string> _onStatus;
    private readonly Action<string> _onRemoteTextApplied;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();
    private StompClient? _stompClient;
    private bool _stopped = true;
    private bool _connected;
    private long _firstDisconnectTicks;
    private ulong? _previousHash;
    private bool _suppressNextLocal;

    public TextSyncEngine(
        ClipConfig config,
        SynchronizationContext uiContext,
        Action<string> onStatus,
        Action<string> onRemoteTextApplied)
    {
        _config = config;
        _uiContext = uiContext;
        _onStatus = onStatus;
        _onRemoteTextApplied = onRemoteTextApplied;
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (!_stopped)
            {
                return;
            }
            _stopped = false;
        }
        _ = ConnectAsync();
    }

    public async Task StopAsync()
    {
        lock (_stateLock)
        {
            _stopped = true;
            _connected = false;
        }
        _cts.Cancel();
        var client = Interlocked.Exchange(ref _stompClient, null);
        if (client is not null)
        {
            await client.CloseAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void SendLocalText(string text, string source)
    {
        _ = Task.Run(() => SendLocalTextAsync(text, source, _cts.Token));
    }

    public async Task OnConnectedAsync()
    {
        lock (_stateLock)
        {
            _connected = true;
            _firstDisconnectTicks = 0;
        }
        Status(UiText.Connected);
        var client = _stompClient;
        if (client is not null)
        {
            await client.SubscribeAsync("/user/queue/cliptext", _cts.Token).ConfigureAwait(false);
        }
    }

    public async Task OnMessageAsync(string body)
    {
        try
        {
            var message = JsonUtil.ParseClipMessage(body);
            if (!message.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var text = message.Payload;
            if (_config.CipherEnabled)
            {
                text = CryptoManager.Decrypt(JsonUtil.ParseEncryptedPayload(text), _config.HashedPasswordBase64);
            }

            var hash = HashUtil.Fnv1A64(text);
            lock (_stateLock)
            {
                if (_previousHash == hash)
                {
                    return;
                }
                _previousHash = hash;
                _suppressNextLocal = true;
            }

            if (!IsWithinLimits(text, UiText.DirectionInbound))
            {
                return;
            }

            await InvokeUiAsync(() =>
            {
                try
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    _onRemoteTextApplied(text);
                }
                catch (Exception error)
                {
                    Status(UiText.ClipboardWriteFailed(error.Message));
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Status(UiText.InboundError(error.Message));
        }
    }

    public Task OnClosedAsync(string reason)
    {
        MarkDisconnected();
        Status(UiText.Disconnected(reason));
        ScheduleReconnect();
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception error)
    {
        MarkDisconnected();
        Status(UiText.WebSocketError(error.Message));
        ScheduleReconnect();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task ConnectAsync()
    {
        if (IsStopped())
        {
            return;
        }

        Status(UiText.Connecting);
        try
        {
            var oldClient = Interlocked.Exchange(ref _stompClient, null);
            if (oldClient is not null)
            {
                await oldClient.CloseAsync().ConfigureAwait(false);
                await oldClient.DisposeAsync().ConfigureAwait(false);
            }

            var client = new StompClient(_config.WebsocketUrl, _config.CookieHeader, this);
            _stompClient = client;
            await client.ConnectAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            await OnErrorAsync(error).ConfigureAwait(false);
        }
    }

    private async Task SendLocalTextAsync(string text, string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_suppressNextLocal)
            {
                _suppressNextLocal = false;
                return;
            }
            if (!_connected)
            {
                Status(UiText.IgnoredNotConnected(source));
                return;
            }
        }

        if (!IsWithinLimits(text, UiText.DirectionOutbound))
        {
            return;
        }

        var hash = HashUtil.Fnv1A64(text);
        lock (_stateLock)
        {
            if (_previousHash == hash)
            {
                return;
            }
            _previousHash = hash;
        }

        var payload = text;
        if (_config.CipherEnabled)
        {
            payload = JsonUtil.EncryptedPayload(CryptoManager.Encrypt(text, _config.HashedPasswordBase64));
        }

        var client = _stompClient;
        if (client is not null)
        {
            await client.SendAsync("/app/cliptext", JsonUtil.ClipMessage(payload, "text"), cancellationToken).ConfigureAwait(false);
            Status(UiText.Broadcasting);
        }
    }

    private bool IsWithinLimits(string text, string direction)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var localLimit = _config.LocalMaxClipboardBytes > 0 ? _config.LocalMaxClipboardBytes : _config.MaxSizeBytes;
        var ok = bytes <= _config.MaxSizeBytes && bytes <= localLimit;
        if (!ok)
        {
            Status(UiText.ClipboardTooLarge(direction, bytes));
        }
        return ok;
    }

    private void MarkDisconnected()
    {
        lock (_stateLock)
        {
            _connected = false;
            if (_firstDisconnectTicks == 0)
            {
                _firstDisconnectTicks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
    }

    private void ScheduleReconnect()
    {
        if (IsStopped())
        {
            return;
        }

        var delay = ReconnectDelay();
        Status(UiText.Connecting);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                await ConnectAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private TimeSpan ReconnectDelay()
    {
        long firstDisconnect;
        lock (_stateLock)
        {
            firstDisconnect = _firstDisconnectTicks;
        }
        if (firstDisconnect == 0)
        {
            return TimeSpan.FromSeconds(10);
        }

        var elapsed = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - firstDisconnect) / 1000;
        return elapsed switch
        {
            < 600 => TimeSpan.FromSeconds(10),
            < 1800 => TimeSpan.FromSeconds(60),
            < 3600 => TimeSpan.FromSeconds(180),
            _ => TimeSpan.FromSeconds(300)
        };
    }

    private bool IsStopped()
    {
        lock (_stateLock)
        {
            return _stopped || _cts.IsCancellationRequested;
        }
    }

    private void Status(string message)
    {
        if (_uiContext == SynchronizationContext.Current)
        {
            _onStatus(message);
            return;
        }
        _uiContext.Post(static state =>
        {
            var (callback, value) = ((Action<string>, string))state!;
            callback(value);
        }, (_onStatus, message));
    }

    private Task InvokeUiAsync(Action action)
    {
        if (_uiContext == SynchronizationContext.Current)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(static state =>
        {
            var (work, completion) = ((Action, TaskCompletionSource))state!;
            try
            {
                work();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }, (action, tcs));
        return tcs.Task;
    }
}
