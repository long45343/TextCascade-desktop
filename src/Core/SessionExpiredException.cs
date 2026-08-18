namespace TextCascadeSharp.Core;

// WebSocket 升级返回 401/403 时抛出：表示 Bearer token 已失效，
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

// WebSocket 子协议协商失败（HTTP 400）：服务端不支持 textcascade.v1，
// 视为致命错误，重连无意义。
public sealed class FatalProtocolException : Exception
{
    public FatalProtocolException(string message) : base(message)
    {
    }
}

// 用户名/密码被服务端明确拒绝（401 invalid_credentials）。与网络故障、
// 服务端暂不可用区分，会话恢复流程遇到该异常时不应继续重试。
public sealed class InvalidCredentialException : Exception
{
    public InvalidCredentialException(string message) : base(message)
    {
    }
}

// 登录触发服务端限流（429 rate_limited）。自动重登的退避必须 ≥ 30s。
public sealed class RateLimitedException : Exception
{
    public RateLimitedException(string message) : base(message)
    {
    }
}

// 服务端协议版本高于本客户端支持版本。不建立 WebSocket，提示用户升级。
public sealed class ProtocolVersionNotSupportedException : Exception
{
    public ProtocolVersionNotSupportedException(int serverVersion, int supportedVersion)
        : base($"Server protocol version {serverVersion} is higher than supported {supportedVersion}.")
    {
        ServerVersion = serverVersion;
        SupportedVersion = supportedVersion;
    }

    public int ServerVersion { get; }

    public int SupportedVersion { get; }
}
