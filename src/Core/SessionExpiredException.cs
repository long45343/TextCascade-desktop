namespace TextCascadeSharp.Core;

// WebSocket 握手返回 401/403 时抛出：表示 JSESSIONID 已失效，
// 上层应停止自动重连并进入重新登录流程。
public sealed class SessionExpiredException : Exception
{
    public SessionExpiredException(System.Net.HttpStatusCode statusCode)
        : base($"WebSocket session expired (HTTP {(int)statusCode}).")
    {
        StatusCode = statusCode;
    }

    public System.Net.HttpStatusCode StatusCode { get; }
}
