namespace XAUAT.EduApi.Interfaces;

/// <summary>
/// ICS 日历订阅生成。
/// <para>
/// 从 XAUAT.LoginApi 迁移而来。与 LoginApi 版本的区别在于**数据来源**：那边自带一套
/// 教务取数与解析（<c>XauatAcademicClient</c> + <c>XauatScheduleParser</c>），
/// 这里改为复用本服务既有的 <c>ICourseService</c> 与 <c>IExamService</c>，
/// 同一个教务系统不再被解析两遍。
/// </para>
/// </summary>
public interface ICalendarService
{
    /// <summary>
    /// 生成整份 ICS 日历。
    /// </summary>
    /// <param name="username">学号。</param>
    /// <param name="password">教务密码。</param>
    /// <param name="filter">仅 <c>future</c> 有效：只保留结束时间在当下之后的事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>UTF-8 编码的 ICS 正文（无 BOM，CRLF 行尾）。</returns>
    /// <remarks>
    /// 失败时沿用本服务既有的异常语义，由控制器映射状态码：
    /// <c>LoginFailedException</c> → 401、<c>AccountBannedException</c> → 429 + Retry-After、
    /// <c>RateLimitException</c> → 429、其余 → 500。
    /// </remarks>
    Task<byte[]> GenerateAsync(
        string username, string password, string? filter, CancellationToken cancellationToken = default);
}
