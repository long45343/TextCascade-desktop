namespace TextCascadeSharp.Core;

// Core 层通用领域异常：携带领域错误码（ErrorCodes）与可选技术性 detail。
// Message 为英文技术性描述，不含本地化文案；本地化映射统一由 UI 层（FormatError）完成。
public class CoreException : Exception
{
    public CoreException(string errorCode, string? detail = null)
        : base(detail is null ? errorCode : $"{errorCode}: {detail}")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}