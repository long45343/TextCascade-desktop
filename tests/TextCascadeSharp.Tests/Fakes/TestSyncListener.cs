using System.Net.WebSockets;
using TextCascadeSharp.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TextCascadeSharp.Tests.Fakes;

// ISyncListener 的测试实现：记录回调并暴露 TaskCompletionSource 供等待
internal sealed class TestSyncListener : ISyncListener
{
    private readonly object _gate = new();
    private readonly List<string> _events = new();

    public TaskCompletionSource WelcomeTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<Exception> TransportErrorTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ClosedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<InboundClipMessage> Clips { get; } = [];
    public List<ClipAckMessage> Acks { get; } = [];
    public List<PingMessage> Pings { get; } = [];
    public List<ByeMessage> Byes { get; } = [];
    public List<ErrorMessage> Errors { get; } = [];

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public Task OnWelcomeAsync(WelcomeMessage welcome)
    {
        lock (_gate)
        {
            _events.Add($"welcome:{welcome.Latest?.Version.ToString() ?? "null"}");
        }
        WelcomeTcs.TrySetResult();
        return Task.CompletedTask;
    }

    public Task OnClipAsync(InboundClipMessage message)
    {
        lock (_gate)
        {
            _events.Add($"clip:{message.Version}");
            Clips.Add(message);
        }
        return Task.CompletedTask;
    }

    public Task OnClipAckAsync(ClipAckMessage ack)
    {
        lock (_gate)
        {
            _events.Add($"clip_ack:{ack.Id}:{ack.Version}");
            Acks.Add(ack);
        }
        return Task.CompletedTask;
    }

    public Task OnPingAsync(PingMessage ping)
    {
        lock (_gate)
        {
            _events.Add("ping");
            Pings.Add(ping);
        }
        return Task.CompletedTask;
    }

    public Task OnByeAsync(ByeMessage bye)
    {
        lock (_gate)
        {
            _events.Add($"bye:{bye.Reason ?? "-"}");
            Byes.Add(bye);
        }
        return Task.CompletedTask;
    }

    public Task OnErrorFrameAsync(ErrorMessage error)
    {
        lock (_gate)
        {
            _events.Add($"error:{error.Code}");
            Errors.Add(error);
        }
        return Task.CompletedTask;
    }

    public Task OnClosedAsync(string reason, WebSocketCloseStatus? closeStatus)
    {
        lock (_gate)
        {
            _events.Add($"closed:{reason}:{closeStatus?.ToString() ?? "-"}");
        }
        ClosedTcs.TrySetResult();
        return Task.CompletedTask;
    }

    public Task OnTransportErrorAsync(Exception error)
    {
        lock (_gate)
        {
            _events.Add($"transport_error:{error.Message}");
        }
        TransportErrorTcs.TrySetResult(error);
        return Task.CompletedTask;
    }
}

internal static class TestHelpers
{
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}

// textcascade.v1 契约样本镜像。
// 同源要求（spec“客户端测试”）：本样本逐字镜像 lightweight-text-server-spec.md
// §4-§7 的 JSON 示例，并与 Android 端保持同一份内容，避免三端漂移；
// 后续应迁移到服务端 §10.4 约定的共享样本目录。
internal static class ContractSamples
{
    // POST /api/v1/login 成功响应（§4.1，expiresAtUtc 为无毫秒 Z 格式）
    public const string LoginSuccess =
        """
        {"token":"tok-123","expiresAtUtc":"2026-09-17T00:00:00Z","protocolVersion":1,"maxTextBytes":524288,"helloTimeoutSeconds":5,"heartbeatIntervalSeconds":30,"heartbeatTimeoutSeconds":60}
        """;

    // 401 错误响应（§4.1：错误码字段名为 error）
    public const string LoginInvalidCredentials =
        """{"error":"invalid_credentials","message":"Invalid username or password."}""";

    // 429 错误响应（§4.1：rate_limited）
    public const string LoginRateLimited =
        """{"error":"rate_limited","message":"Too many login attempts."}""";

    // hello（§5.2 示例：带 snapshot）
    public const string HelloWithSnapshot =
        """{"type":"hello","clientId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","clientName":"Windows-Desktop","lastServerVersion":7,"snapshot":{"payload":"hello","encrypted":false,"hash":"af63dc4c8601ec8c","localModifiedAtUtc":"2026-08-18T08:00:00Z"}}""";

    // hello（无 snapshot）
    public const string HelloWithoutSnapshot =
        """{"type":"hello","clientId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","clientName":"Windows-Desktop","lastServerVersion":0}""";

    // welcome（§5.3 示例结构：latest 为 null）
    public const string WelcomeEmpty =
        """{"type":"welcome","protocolVersion":1,"latest":null}""";

    // welcome（§5.3 示例结构：latest 有值，含 fromClientId/updatedAtUtc）
    public const string WelcomeWithLatest =
        """{"type":"welcome","protocolVersion":1,"latest":{"version":9,"payload":"hello","encrypted":false,"hash":"af63dc4c8601ec8c","fromClientId":"android-a","updatedAtUtc":"2026-08-18T07:59:58Z"}}""";

    // clip 广播（§5.4 示例结构：含 id/fromClientId/fromClientName/updatedAtUtc）
    public const string ClipBroadcast =
        """{"type":"clip","version":10,"id":"client-generated-unique-id","payload":"world","encrypted":false,"hash":"3d58dee72d4e0c97","fromClientId":"windows-a","fromClientName":"Windows-Desktop","updatedAtUtc":"2026-08-18T08:01:00Z"}""";

    // clip_ack（§5.4 示例结构：含 updatedAtUtc）
    public const string ClipAck =
        """{"type":"clip_ack","id":"clip-0001","version":11,"updatedAtUtc":"2026-08-18T08:01:00Z"}""";

    // ping（§5.5 示例）
    public const string Ping =
        """{"type":"ping","serverTimeUtc":"2026-08-18T08:02:00Z"}""";

    // pong（§5.5 示例）
    public const string Pong =
        """{"type":"pong","clientTimeUtc":"2026-08-18T08:02:00Z"}""";

    // bye（§7 示例）
    public const string Bye =
        """{"type":"bye","reason":"server_shutdown"}""";

    // error（§5.6 示例结构：含 referenceId）
    public const string ErrorTextTooLarge =
        """{"type":"error","code":"text_too_large","message":"Text exceeds maxTextBytes.","referenceId":"clip-0001"}""";

    // error（rate_limited）
    public const string ErrorRateLimited =
        """{"type":"error","code":"rate_limited","message":"Clip rate limit exceeded."}""";
}
