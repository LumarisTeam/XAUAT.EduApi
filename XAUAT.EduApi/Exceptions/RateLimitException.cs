namespace XAUAT.EduApi.Exceptions;

/// <summary>
/// 教务系统限流异常
/// 当请求被教务系统限流（系统限流中）时抛出
/// </summary>
public class RateLimitException(int? retryAfterSeconds = null) : Exception("教务系统限流，请求被拒绝，请稍后重试")
{
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}