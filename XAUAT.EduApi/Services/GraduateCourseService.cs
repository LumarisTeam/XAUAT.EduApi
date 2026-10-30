using EduApi.Data.Models;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Login.Unified;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 研究生教务系统课表抓取：<c>gmis.xauat.edu.cn/pyxx/pygl/xskbcx</c> → HTML → <see cref="CourseActivity"/>。
/// <para>
/// 抓取逻辑移植自仓库外的 <c>src/get_data.py</c>；页面解析见 <see cref="GraduateTimetableParser"/>。
/// </para>
/// <para>
/// <b>传输层必须复用 <c>UnifiedAuthClient</c></b>，不要换成 <c>CreateClient()</c>：
/// <c>gmis</c> 的每条响应都带非法头名 <c>SELINUX=Server</c>，
/// <see cref="System.Net.Http.SocketsHttpHandler"/> 会在解析响应头的阶段直接抛
/// <c>InvalidResponse</c>，任何设置都放宽不了——只有
/// <see cref="ProtocolFallbackHandler"/> 的容错通道能读进来。该命名客户端还已经
/// 关掉自动重定向（cookie 失效时我们靠 302 判定未认证）与自动 cookie（并发不串号）。
/// </para>
/// </summary>
public sealed class GraduateCourseService(
    IHttpClientFactory httpClientFactory,
    ILogger<GraduateCourseService> logger,
    ICacheService cacheService,
    ITestAccountResolver? testAccountResolver = null,
    ITestDataProvider? testDataProvider = null) : IGraduateCourseService
{
    public const string TimetableUrl = "https://gmis.xauat.edu.cn/pyxx/pygl/xskbcx";

    /// <summary>见类注释：必须是挂了协议回退与建连闸门的那个命名客户端。</summary>
    private const string ClientName = UnifiedSsoClient.ClientName;

    public async Task<List<CourseActivity>> GetCoursesAsync(
        string studentId, string cookie, CancellationToken cancellationToken = default)
    {
        if (testAccountResolver?.IsTestAccount(cookie: cookie, studentId: studentId) == true)
        {
            logger.LogInformation("测试账号命中研究生课程测试数据，studentId: {StudentId}", studentId);
            return await testDataProvider!.GetCoursesAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(studentId))
        {
            throw new ArgumentNullException(nameof(studentId), "学号不能为空");
        }

        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentNullException(nameof(cookie), "Cookie 不能为空");
        }

        return await cacheService.GetOrCreateAsync(
            CacheKeys.GraduateCourses(studentId),
            () => FetchAsync(studentId, cookie, cancellationToken),
            TimeSpan.FromHours(8),
            cancellationToken: cancellationToken);
    }

    private async Task<List<CourseActivity>> FetchAsync(
        string studentId, string cookie, CancellationToken cancellationToken)
    {
        logger.LogInformation("开始抓取研究生课程，studentId: {StudentId}", studentId);

        var client = httpClientFactory.CreateClient(ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, TimetableUrl);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var status = (int)response.StatusCode;

        // 未认证有两种表现：直接 401/403，或 302 回 authserver 登录页。
        // 命名客户端关了自动重定向，所以这里能看到 302 本身。
        if (status is >= 300 and < 400 ||
            response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnAuthenticationError();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"获取研究生课表失败，状态码: {status}");
        }

        var html = await GraduateHtmlDecoder.DecodeAsync(response, cancellationToken);
        var courses = GraduateTimetableParser.Parse(html);

        logger.LogInformation("研究生课程抓取完成，studentId: {StudentId}，课程安排 {Count} 条",
            studentId, courses.Count);

        return courses;
    }
}
