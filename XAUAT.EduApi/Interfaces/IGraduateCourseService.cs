using EduApi.Data.Models;

namespace XAUAT.EduApi.Interfaces;

/// <summary>
/// 研究生教务系统课表。由请求头 <c>x-loginMode: 1</c> 触发。
/// <para>
/// 与本科生 <see cref="ICourseService"/> 返回同一种 <see cref="CourseActivity"/>，
/// 客户端不需要为两个系统准备两套渲染逻辑。
/// </para>
/// </summary>
public interface IGraduateCourseService
{
    /// <param name="studentId">学号；仅用于缓存键，课表由 cookie 对应的会话决定。</param>
    /// <param name="cookie">研究生系统会话 cookie（登录接口返回的 <c>cookie</c>）。</param>
    /// <exception cref="Exceptions.UnAuthenticationError">cookie 失效或上游把请求重定向到登录页 → 401。</exception>
    /// <exception cref="HttpRequestException">研究生系统不可达或返回非 2xx → 502。</exception>
    Task<List<CourseActivity>> GetCoursesAsync(
        string studentId, string cookie, CancellationToken cancellationToken = default);
}
