namespace TextCascadeSharp.Core;

// Core 层通用领域异常：携带领域错误码（ErrorCodes）与可选技术性 detail。
// Message 为英文技术性描述，不含本地化文案；本地化映射统一由 UI 层（FormatError）完成。
public class CoreException : Exception
{
    public CoreException(string errorCode, string? detail = null)
        : base(detail is null ? errorCode : $"{errorCode}: {detail}")
    {
        ErrorCode = errorCode;
        Detail = detail;
    }

    public string ErrorCode { get; }

    // 原始技术性 detail（如 "HTTP 500"）。UI 层据此做本地化映射，
    // 不必再从 Message 的拼接格式里反向拆分。
    public string? Detail { get; }
}
