namespace XAUAT.EduApi.Exceptions;

/// <summary>同一 MFA 会话正在被另一个请求处理，或扫码轮询过快。</summary>
public class MfaBusyException(int retryAfterSeconds = 1) : Exception("二次认证正在处理中，请稍后重试")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
