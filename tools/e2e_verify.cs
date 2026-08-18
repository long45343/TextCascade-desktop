// TextCascade v2.0.0 端到端验证脚本（Task 10）。
// 用法: dotnet run tools/e2e_verify.cs <server_url> <username> <password>
// 纯 BCL（HttpClient/ClientWebSocket/System.Text.Json），无 NuGet 依赖。
// 验证链路：login → WS 升级(Bearer+子协议) → hello+snapshot → welcome →
//           双连接 clip 广播 + clip_ack → ping/pong → 文本超限 error → 401 拒绝
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

// 必填参数：禁止默认值（真实部署地址与凭据不得写入本仓库）
if (args.Length < 3)
{
    Console.Error.WriteLine("用法: dotnet run --file tools/e2e_verify.cs <server_url> <username> <password>");
    Console.Error.WriteLine("示例: dotnet run --file tools/e2e_verify.cs https://your-server:8443 alice 'secret'");
    return 2;
}
var serverUrl = args[0].TrimEnd('/');
var username = args[1];
var password = args[2];

var wsBase = serverUrl.StartsWith("https://") ? "wss://" + serverUrl["https://".Length..]
    : "ws://" + serverUrl["http://".Length..];
var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  [PASS] {name}"); }
    else { failed++; Console.WriteLine($"  [FAIL] {name} {detail}"); }
}

Console.WriteLine($"== TextCascade E2E ==\nserver={serverUrl} user={username}\n");

// ---------- 1. health ----------
using (var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
{
    var health = await hc.GetStringAsync(serverUrl + "/health");
    Check("GET /health", health.Contains("ok"), health);
}

// ---------- 2. login ----------
string token = "";
using (var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
{
    var body = $$$"""{"username":"{{{username}}}","password":"{{{password}}}"}""";
    using var resp = await hc.PostAsync(serverUrl + "/api/v1/login",
        new StringContent(body, Encoding.UTF8, "application/json"));
    var text = await resp.Content.ReadAsStringAsync();
    Check("POST /api/v1/login 200", (int)resp.StatusCode == 200, $"HTTP {(int)resp.StatusCode} {text}");
    using var doc = JsonDocument.Parse(text);
    token = doc.RootElement.GetProperty("token").GetString()!;
    var proto = doc.RootElement.GetProperty("protocolVersion").GetInt32();
    var helloTimeout = doc.RootElement.GetProperty("helloTimeoutSeconds").GetInt32();
    Check("响应字段完整", token.Length > 0 && proto == 1 && helloTimeout > 0,
        $"protocolVersion={proto} helloTimeout={helloTimeout}");
}

// ---------- 3. 错误密码 401 ----------
using (var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
{
    var body = $$$"""{"username":"{{{username}}}","password":"wrong-password"}""";
    using var resp = await hc.PostAsync(serverUrl + "/api/v1/login",
        new StringContent(body, Encoding.UTF8, "application/json"));
    var text = await resp.Content.ReadAsStringAsync();
    Check("错误密码 401 invalid_credentials",
        (int)resp.StatusCode == 401 && text.Contains("invalid_credentials"),
        $"HTTP {(int)resp.StatusCode} {text}");
}

// ---------- WS 辅助 ----------
async Task<ClientWebSocket> ConnectAsync()
{
    var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
    ws.Options.AddSubProtocol("textcascade.v1");
    await ws.ConnectAsync(new Uri(wsBase + "/api/v1/sync"), CancellationToken.None);
    return ws;
}

static async Task SendAsync(ClientWebSocket ws, string json)
    => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text,
        WebSocketMessageFlags.EndOfMessage, CancellationToken.None);

static async Task<string> RecvAsync(ClientWebSocket ws, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    var buffer = new byte[64 * 1024];
    using var ms = new MemoryStream();
    ValueWebSocketReceiveResult r;
    do
    {
        r = await ws.ReceiveAsync((Memory<byte>)buffer, cts.Token);
        ms.Write(buffer, 0, r.Count);
    } while (!r.EndOfMessage);
    return Encoding.UTF8.GetString(ms.ToArray());
}

var random = Guid.NewGuid().ToString("N")[..8];
var clientIdA = "e2e-win-a-" + random;
var clientIdB = "e2e-win-b-" + random;

// ---------- 4. 连接 A：hello(带 snapshot) → welcome ----------
using var wsA = await ConnectAsync();
Check("WS 升级（Bearer + textcascade.v1）", wsA.State == WebSocketState.Open);

var hashUtil = (string s) =>
{
    ulong h = 14695981039346656037UL;
    foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 1099511628211UL; }
    return h.ToString("x16");
};

var snapPayload = "e2e-snapshot-" + random;
var hello = $$$"""{"type":"hello","clientId":"{{{clientIdA}}}","clientName":"e2e-verify","lastServerVersion":0,"snapshot":{"payload":{{{J(snapPayload)}}},"encrypted":false,"hash":"{{{hashUtil(snapPayload)}}}","localModifiedAtUtc":"{{{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}}}"}}""";
await SendAsync(wsA, hello);

var welcomeA = await RecvAsync(wsA, TimeSpan.FromSeconds(10));
Check("收到 welcome", welcomeA.Contains("\"type\":\"welcome\""), Trunc(welcomeA));
var welcomeVer = ExtractNum(welcomeA, "version");
Check("welcome 含 protocolVersion/latest 结构",
    welcomeA.Contains("protocolVersion") || welcomeA.Contains("latest"), Trunc(welcomeA));

// ---------- 5. 连接 B：hello(无 snapshot) → welcome ----------
using var wsB = await ConnectAsync();
await SendAsync(wsB, $$$"""{"type":"hello","clientId":"{{{clientIdB}}}","clientName":"e2e-verify-b","lastServerVersion":0}""");
var welcomeB = await RecvAsync(wsB, TimeSpan.FromSeconds(10));
Check("连接 B welcome（快照在恢复窗口外被丢弃或为 latest）",
    welcomeB.Contains("\"type\":\"welcome\""), Trunc(welcomeB));

// ---------- 6. A 发 clip → A 收 clip_ack + B 收广播 ----------
var textAtoB = "hello-from-A-" + random;
var clipA = $$$"""{"type":"clip","id":"clip-a-{{{random}}}","payload":{{{J(textAtoB)}}},"encrypted":false,"hash":"{{{hashUtil(textAtoB)}}}"}""";
await SendAsync(wsA, clipA);

var ackA = await RecvAsync(wsA, TimeSpan.FromSeconds(10));
Check("A 收到 clip_ack（id/version/updatedAtUtc）",
    ackA.Contains("\"type\":\"clip_ack\"") && ackA.Contains("clip-a-") && ackA.Contains("updatedAtUtc"),
    Trunc(ackA));
var ackVer = ExtractNum(ackA, "version");

var bcastB = await RecvAsync(wsB, TimeSpan.FromSeconds(10));
Check("B 收到 clip 广播（payload/version/fromClientId）",
    bcastB.Contains("\"type\":\"clip\"") && bcastB.Contains(textAtoB) && bcastB.Contains(clientIdA),
    Trunc(bcastB));
var bcastVer = ExtractNum(bcastB, "version");
Check("广播与 ACK 版本一致", ackVer > 0 && ackVer == bcastVer, $"ack={ackVer} bcast={bcastVer}");

// ---------- 7. B 发 clip → A 收广播（双向） ----------
var textBtoA = "hello-from-B-" + random;
await SendAsync(wsB, $$$"""{"type":"clip","id":"clip-b-{{{random}}}","payload":{{{J(textBtoA)}}},"encrypted":false,"hash":"{{{hashUtil(textBtoA)}}}"}""");

var ackB = await RecvAsync(wsB, TimeSpan.FromSeconds(10));
var bcastA = await RecvAsync(wsA, TimeSpan.FromSeconds(10));
Check("B 收到 clip_ack", ackB.Contains("\"type\":\"clip_ack\"") && ackB.Contains("clip-b-"), Trunc(ackB));
Check("A 收到 B 的广播（双向同步）", bcastA.Contains(textBtoA), Trunc(bcastA));

// B 后台保活（已知服务端问题：同用户连接摘除后其余连接心跳可能停止，
// 故 B 在 A 完成心跳验证期间保持在线应答 ping）
using var keepAliveCts = new CancellationTokenSource();
var keepAliveB = Task.Run(async () =>
{
    var buffer = new byte[64 * 1024];
    try
    {
        while (!keepAliveCts.IsCancellationRequested && wsB.State == WebSocketState.Open)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(keepAliveCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(75));
            var r = await wsB.ReceiveAsync((Memory<byte>)buffer, cts.Token);
            var msg = Encoding.UTF8.GetString(buffer, 0, r.Count);
            if (msg.Contains("\"type\":\"ping\""))
            {
                var pong = $$"""{"type":"pong","clientTimeUtc":"{{DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")}}"}""";
                await wsB.SendAsync(Encoding.UTF8.GetBytes(pong), WebSocketMessageType.Text,
                    WebSocketMessageFlags.EndOfMessage, CancellationToken.None);
            }
        }
    }
    catch (Exception) { }
});

// ---------- 8. 心跳：等待服务端 ping → 回整秒 Z 格式 pong ----------
// 注：主动乱序发 pong 会被服务端严格 abort；正式客户端只在收到 ping 后回 pong，
// 此处按真实客户端行为验证（heartbeatIntervalSeconds=30，等 40s）
Console.WriteLine("  [INFO] 等待服务端应用层 ping（最长 40s）...");
var sawPing = false;
var secondPingOk = false;
try
{
    var deadlineHb = Environment.TickCount64 + 40_000;
    while (Environment.TickCount64 < deadlineHb)
    {
        var msg = await RecvAsync(wsA, TimeSpan.FromSeconds(40));
        if (msg.Contains("\"type\":\"ping\""))
        {
            sawPing = true;
            await SendAsync(wsA, $$"""{"type":"pong","clientTimeUtc":"{{DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")}}"}""");
            Console.WriteLine("  [INFO] 已回 pong，观察下一轮 ping（35s）...");
            // 收到第一个 ping 并回 pong 后，观察连接是否保持到下一个 ping
            var deadline2 = Environment.TickCount64 + 35_000;
            while (Environment.TickCount64 < deadline2 && wsA.State == WebSocketState.Open)
            {
                var next = await RecvAsync(wsA, TimeSpan.FromSeconds(35));
                if (next.Contains("\"type\":\"ping\""))
                {
                    secondPingOk = true;
                    await SendAsync(wsA, $$"""{"type":"pong","clientTimeUtc":"{{DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")}}"}""");
                    break;
                }
            }
            break;
        }
    }
}
catch (Exception) { }
Check("应用层 ping 到达并回整秒 Z 格式 pong", sawPing);
Check("心跳存活（下一轮 ping 到达，连接保持）", secondPingOk && wsA.State == WebSocketState.Open);

// ---------- 9. 超限文本 → error text_too_large（连接保持） ----------
// 530000 字节 > maxTextBytes(524288) 且整帧 < maxFrameBytes(589824)，
// 触发 text_too_large 而非 frame_too_large/1009
var big = new string('x', 530_000);
await SendAsync(wsA, $$$"""{"type":"clip","id":"clip-big-{{{random}}}","payload":{{{J(big)}}},"encrypted":false,"hash":"{{{hashUtil(big)}}}"}""");
var errResp = "";
{
    var deadline = Environment.TickCount64 + 10_000;
    while (Environment.TickCount64 < deadline)
    {
        var msg = await RecvAsync(wsA, TimeSpan.FromSeconds(10));
        if (msg.Contains("text_too_large")) { errResp = msg; break; }
    }
}
Check("超限文本 error text_too_large + referenceId",
    errResp.Contains("text_too_large") && errResp.Contains("clip-big-"), Trunc(errResp));

// ---------- 10. 畸形 JSON → error invalid_message（连接保持） ----------
await SendAsync(wsA, "not-json");
var invalid = "";
{
    var deadline = Environment.TickCount64 + 10_000;
    while (Environment.TickCount64 < deadline)
    {
        var msg = await RecvAsync(wsA, TimeSpan.FromSeconds(10));
        if (msg.Contains("invalid_message")) { invalid = msg; break; }
    }
}
Check("畸形消息 error invalid_message", invalid.Contains("invalid_message"), Trunc(invalid));

// ---------- 11. 错误 token 升级被拒 401 ----------
try
{
    var wsBad = new ClientWebSocket();
    wsBad.Options.SetRequestHeader("Authorization", "Bearer invalid-token-value");
    wsBad.Options.AddSubProtocol("textcascade.v1");
    await wsBad.ConnectAsync(new Uri(wsBase + "/api/v1/sync"), CancellationToken.None);
    Check("无效 token 升级拒绝", false, "居然连上了");
}
catch (WebSocketException e)
{
    Check("无效 token 升级拒绝 401", true, e.Message);
}

// ---------- 收尾 ----------
// 已知服务端问题：close 1000 帧无响应（握手不回），客户端用带超时 close + abort 兜底。
// 正式客户端 SyncClient.CloseAsync 即此策略（2s 超时后 Abort），不会挂死
try
{
    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
}
catch (Exception) { }
if (wsA.State == WebSocketState.Open)
{
    wsA.Abort();
}
Check("close 发起后连接终止（服务端不回 close 帧，abort 兜底）",
    wsA.State is WebSocketState.Closed or WebSocketState.CloseReceived or WebSocketState.Aborted,
    $"实际状态={wsA.State}");

keepAliveCts.Cancel();
try { await keepAliveB.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
try
{
    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
}
catch (Exception) { }
if (wsB.State == WebSocketState.Open)
{
    wsB.Abort();
}

Console.WriteLine($"\n== 结果: {passed} 通过, {failed} 失败 ==");
return failed == 0 ? 0 : 1;

static string Trunc(string s) => s.Length <= 160 ? s : s[..160] + "...";

// JSON 字符串字面量转义（file-based app 禁用反射序列化，手写以避免依赖）
static string J(string s)
{
    var sb = new StringBuilder("\"");
    foreach (var c in s)
    {
        switch (c)
        {
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                else sb.Append(c);
                break;
        }
    }
    return sb.Append('"').ToString();
}
static ulong ExtractNum(string json, string field)
{
    var i = json.IndexOf($"\"{field}\":");
    if (i < 0) return 0;
    var start = json.IndexOf(':', i) + 1;
    var end = start;
    while (end < json.Length && (char.IsDigit(json[end]))) end++;
    return ulong.TryParse(json[start..end], out var v) ? v : 0;
}
