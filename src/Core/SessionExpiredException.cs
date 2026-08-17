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

// 用户名/密码被服务端明确拒绝。与网络故障、服务端暂不可用区分，
// 会话恢复流程遇到该异常时不应继续重试。
public sealed class InvalidCredentialException : Exception
{
    public InvalidCredentialException(string message) : base(message)
    {
    }
}
