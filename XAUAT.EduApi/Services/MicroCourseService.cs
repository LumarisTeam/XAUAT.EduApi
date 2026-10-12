using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

public interface IMicroCourseService
{
    Task<List<CourseActivity>> GetMicroCoursesAsync(
        string? studentId,
        string cookie,
        string? semesterId = null,
        string bizTypeId = "2",
        string language = "zh");
}

/// <summary>
/// 微专业课表抓取：/student/for-std/adminclass-course-table/print-data（XML）。
/// 学号支持逗号分隔（Cookie 服务可能返回 "68551,84795"），逐个抓取后合并。
/// 不传学号时用 CookieCodeService 从 Cookie 解析。
/// </summary>
public class MicroCourseService(
    IHttpClientFactory httpClientFactory,
    ILogger<MicroCourseService> logger,
    IExamService examService,
    ICookieCodeService cookieCodeService,
    IStudentRateLimitExecutor? rateLimitExecutor = null)
    : IMicroCourseService
{
    private const string MicroTableUrl =
        "https://swjw.xauat.edu.cn/student/for-std/adminclass-course-table/print-data";

    private readonly IStudentRateLimitExecutor _rateLimitExecutor =
        rateLimitExecutor ?? NoOpStudentRateLimitExecutor.Instance;

    public async Task<List<CourseActivity>> GetMicroCoursesAsync(
        string? studentId,
        string cookie,
        string? semesterId = null,
        string bizTypeId = "2",
        string language = "zh")
    {
        logger.LogInformation("开始抓取微专业课表");

        if (string.IsNullOrEmpty(cookie))
        {
            throw new UnAuthenticationError();
        }

        if (string.IsNullOrWhiteSpace(studentId))
        {
            logger.LogInformation("未提供学号，使用 Cookie 服务解析");
            studentId = await cookieCodeService.GetCode(cookie);
            if (string.IsNullOrEmpty(studentId) || studentId == "/student/login")
            {
                throw new UnAuthenticationError();
            }
        }

        var split = studentId.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 0)
        {
            throw new InvalidOperationException("学号不能为空");
        }

        var semester = string.IsNullOrWhiteSpace(semesterId)
            ? await _rateLimitExecutor.ExecuteAsync(split, () => examService.GetThisSemester(cookie, language))
            : new SemesterItem { Value = semesterId, Text = semesterId };

        if (string.IsNullOrWhiteSpace(semester.Value))
        {
            throw new InvalidOperationException("无法获取当前学期信息");
        }

        // 多个学号逐个抓取后合并，与 CourseService 的处理方式一致
        var tasks = split.Select(id => EduSystemThrottler.ThrottleAsync(
            () => FetchMicroCourseForStudent(semester.Value, id, cookie, bizTypeId))).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var courses = results.SelectMany(result => result).ToList();

        foreach (var item in courses)
        {
            item.WeekIndexes = item.WeekIndexes.OrderBy(index => index).ToList();
            item.Room = string.IsNullOrEmpty(item.Room) ? "未知" : item.Room.Replace("*", "");
        }

        logger.LogInformation("微专业课表抓取完成，共 {Count} 条", courses.Count);
        return courses;
    }

    private async Task<List<CourseActivity>> FetchMicroCourseForStudent(
        string semesterId,
        string studentId,
        string cookie,
        string bizTypeId)
    {
        // 为每个请求创建新的 HttpClient 以避免并发问题
        using var client = httpClientFactory.CreateClient();
        client.SetRealisticHeaders();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        // 该接口按 Accept 做内容协商：不带 Accept 会返回 JSON，显式要 XML。
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "application/xml,text/xml;q=0.9,*/*;q=0.8");
        client.Timeout = HttpTimeouts.EduSystem;

        var url = $"{MicroTableUrl}?studentId={Uri.EscapeDataString(studentId)}"
            + $"&semesterId={Uri.EscapeDataString(semesterId)}"
            + $"&bizTypeId={Uri.EscapeDataString(bizTypeId)}";

        using var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"获取微专业课表失败，状态码: {response.StatusCode}");
        }

        var xml = await response.Content.ReadAsStringAsync();
        xml.ThrowIfAuthOrRateLimited();

        var courses = MicroCourseTableParser.Parse(xml);
        logger.LogInformation("studentId {StudentId} 微专业课表 {Count} 条", studentId, courses.Count);
        return courses;
    }
}
