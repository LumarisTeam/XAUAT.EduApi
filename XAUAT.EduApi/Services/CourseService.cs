using EduApi.Data.Models;
using Newtonsoft.Json;
using Polly;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Extensions;

namespace XAUAT.EduApi.Services;

public interface ICourseService
{
    Task<List<CourseActivity>> GetCoursesAsync(string studentId, string cookie, string language = "zh");
}

public class CourseService(
    IHttpClientFactory httpClientFactory,
    ILogger<CourseService> logger,
    IExamService examService,
    ICacheService cacheService,
    IInfoService infoService,
    ITestAccountResolver? testAccountResolver = null,
    ITestDataProvider? testDataProvider = null,
    IStudentRateLimitExecutor? rateLimitExecutor = null)
    : ICourseService
{
    private readonly IStudentRateLimitExecutor _rateLimitExecutor =
        rateLimitExecutor ?? NoOpStudentRateLimitExecutor.Instance;

    public async Task<List<CourseActivity>> GetCoursesAsync(string studentId, string cookie, string language = "zh")
    {
        if (testAccountResolver?.IsTestAccount(cookie: cookie, studentId: studentId) == true)
        {
            logger.LogInformation("测试账号命中课程测试数据，studentId: {StudentId}", studentId);
            return await testDataProvider!.GetCoursesAsync();
        }

        if (infoService.IsGreatThanStart(2))
        {
            // 使用缓存，Key 包含 studentId，过期时间设为1天
            return await cacheService.GetOrCreateAsync(
                CacheKeys.Courses(studentId),
                async () => await FetchCoursesFromRemoteAsync(studentId, cookie, language),
                TimeSpan.FromDays(1));
        }

        return await FetchCoursesFromRemoteAsync(studentId, cookie, language);
    }

    private async Task<List<CourseActivity>> FetchCoursesFromRemoteAsync(string studentId, string cookie, string language)
    {
        logger.LogInformation("开始抓取课程");

        if (string.IsNullOrEmpty(studentId) || string.IsNullOrEmpty(cookie))
        {
            throw new Exception("学号或Cookie不能为空");
        }

        var split = studentId.Split(',');

        var semester = await _rateLimitExecutor.ExecuteAsync(split, () => examService.GetThisSemester(cookie, language));

        if (string.IsNullOrEmpty(semester.Value))
        {
            throw new InvalidOperationException("无法获取当前学期信息");
        }

        // 使用 Task.WhenAll 并行获取所有学生的课程，通过节流器控制并发数
        var tasks = split.Select(a => EduSystemThrottler.ThrottleAsync(
            () => FetchCourseForStudent(semester.Value, a, cookie, language))).ToArray();
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        // 合并结果
        var courses = allResults.SelectMany(r => r).ToList();

        foreach (var item in courses)
        {
            item.WeekIndexes = item.WeekIndexes.OrderBy(x => x).ToList();
            item.Room = string.IsNullOrEmpty(item.Room) ? "未知" : item.Room.Replace("*", "");
        }

        return courses;
    }

    /// <summary>
    /// 获取单个学生的课程数据
    /// </summary>
    private async Task<List<CourseActivity>> FetchCourseForStudent(string semesterValue,
        string studentId, string cookie, string language)
    {
        // 为每个请求创建新的 HttpClient 以避免并发问题
        using var newClient = httpClientFactory.CreateClient();
        newClient.SetRealisticHeaders();
        newClient.DefaultRequestHeaders.Add("Cookie", cookie);
        newClient.Timeout = HttpTimeouts.EduSystem;

        var retryPolicy = Policy
            .Handle<Exceptions.RateLimitException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(6 * Math.Pow(2, retryAttempt - 1)));

        return await _rateLimitExecutor.ExecuteAsync([studentId], () =>
            retryPolicy.ExecuteAsync(async () =>
            {
                var response = await newClient.GetAsync(
                    $"https://swjw.xauat.edu.cn/student/for-std/course-table/semester/{semesterValue}/print-data/{studentId}");

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"获取课程数据失败，状态码: {response.StatusCode}");
                }

                var jsonString = await response.Content.ReadAsStringAsync();
                jsonString.ThrowIfAuthOrRateLimited();

                var jsonResponse = JsonConvert.DeserializeObject<CourseResponse>(jsonString);

                return jsonResponse?.StudentTableVm == null
                    ? throw new InvalidOperationException("未找到课程数据")
                    : jsonResponse.StudentTableVm.Activities;
            }));
    }
}
