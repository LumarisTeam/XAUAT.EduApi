namespace XAUAT.EduApi.Exceptions;

/// <summary>
/// 账号封禁异常类
/// 当登录服务（XAUAT.LoginApi）判定该账号已被封禁时抛出。
/// </summary>
/// <remarks>
/// 与 <see cref="RateLimitException"/> 刻意区分开：那个表达的是"教务系统对请求限流"
/// （上游压力，与调用方是谁无关），而本异常表达的是"这个账号本身被封"（用户行为）。
/// 两者的封禁理由与解封时刻必须能带到日志与 Retry-After 里，混用会丢掉这些信息。
/// </remarks>
public class AccountBannedException(string message, string? reason = null, long? unbanAt = null)
    : Exception(message)
{
    /// <summary>
    /// 封禁理由（LoginApi 的 <c>ban_reason</c>，如"连续触发登录限流"）。
    /// </summary>
    public string? Reason { get; } = reason;

    /// <summary>
    /// 解封时刻（LoginApi 的 <c>ban_until</c>，unix 秒）。
    /// </summary>
    public long? UnbanAt { get; } = unbanAt;

    /// <summary>
    /// 距解封还有多少秒，供 Retry-After 头使用；上游没给解封时刻时为 null。
    /// </summary>
    /// <remarks>
    /// 下限取 60 秒：解封时刻已过（时钟漂移或读到陈旧数据）时，回一个负数或 0
    /// 会让客户端立刻重试，不如让它至少等一分钟。
    /// </remarks>
    public int? RetryAfterSeconds => UnbanAt is null
        ? null
        : (int)Math.Max(60, UnbanAt.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}
