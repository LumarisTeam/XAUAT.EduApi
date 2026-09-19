using System.Text;
using System.Text.Json.Serialization;
using Polly;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace XAUAT.EduApi.Services;

public interface IProgramService
{
    public Task<List<PlanCourse>> GetAllTrainProgram(string cookie, string id, string language = "zh");

    public Task<Dictionary<string, List<PlanCourse>>> GetAllTrainPrograms(string cookie, string id, string language = "zh");
}

public class ProgramService(
    IHttpClientFactory httpClientFactory,
    ICacheService cacheService,
    ILogger<ProgramService>? logger = null,
    ITestAccountResolver? testAccountResolver = null,
    ITestDataProvider? testDataProvider = null,
    IStudentRateLimitExecutor? rateLimitExecutor = null)
    : IProgramService
{
    private const string BaseUrl = "https://swjw.xauat.edu.cn";
    private readonly ILogger<ProgramService>? _logger = logger;
    private readonly IStudentRateLimitExecutor _rateLimitExecutor =
        rateLimitExecutor ?? NoOpStudentRateLimitExecutor.Instance;

    public async Task<List<PlanCourse>> GetAllTrainProgram(string cookie, string id, string language = "zh")
    {
        var ids = id.Split(",");
        var tasks = ids.Select(item => GetAllTrainProgramByOneId(cookie, item, language));
        var results = await Task.WhenAll(tasks);

        return results.SelectMany(x => x).ToList();
    }

    private async Task<List<PlanCourse>> GetAllTrainProgramByOneId(string cookie, string id, string language)
    {
        if (testAccountResolver?.IsTestAccount(cookie: cookie, studentId: id) == true)
        {
            _logger?.LogInformation("测试账号命中培养方案测试数据，studentId: {StudentId}", id);
            return await testDataProvider!.GetProgramAsync();
        }

        return await cacheService.GetOrCreateAsync(
            CacheKeys.TrainProgram(id),
            async () =>
            {
                var retryPolicy = Policy
                    .Handle<HttpRequestException>()
                    .Or<TaskCanceledException>()
                    .Or<RateLimitException>()
                    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(6 * Math.Pow(2, retryAttempt - 1)));

                return await _rateLimitExecutor.ExecuteAsync([id], () =>
                    retryPolicy.ExecuteAsync(async () =>
                    {
                        var request =
                            new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/student/for-std/program/root-module-json/{id}")
                                .WithCookie(cookie);

                        using var httpClient = httpClientFactory.CreateClient();
                        httpClient.Timeout = TimeSpan.FromSeconds(5);

                        var response = await httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode) return [];
                        var content = await response.Content.ReadAsStringAsync();

                        content.ThrowIfAuthOrRateLimited();

                        var result = JsonSerializer.Deserialize<ProgramModel>(content) ?? new ProgramModel();

                        return GetPlanCourses(result);
                    }));
            },
            TimeSpan.FromDays(1));
    }

    private static List<PlanCourse> GetPlanCourses(ProgramModel result)
    {
        var list = new List<PlanCourse>();

        foreach (var item in result.Children)
        {
            list.AddRange(item.PlanCourses.Select(x => x.To()));
            list.AddRange(GetPlanCourses(item));
        }

        return list;
    }

    public async Task<Dictionary<string, List<PlanCourse>>> GetAllTrainPrograms(string cookie, string id, string language = "zh")
    {
        var result = await GetAllTrainProgram(cookie, id, language);

        return result.GroupBy(x => x.TermStr.Contains(',') ? "特殊分组" : x.TermStr)
            .OrderBy(x => x.Key)
            .ToDictionary(x => x.Key, x => x.ToList());
    }
}

public class ProgramModel
{
    [JsonPropertyName("children")] public List<ProgramModel> Children { get; init; } = [];
    [JsonPropertyName("planCourses")] public List<PlanCourses> PlanCourses { get; init; } = [];
}

[Serializable]
public class PlanCourses
{
    [JsonPropertyName("readableTerms")] public string[] ReadableTerms { get; set; } = [];
    [JsonPropertyName("course")] public CourseItem Course { get; set; } = new();

    public PlanCourse To()
    {
        var termStr = new StringBuilder();
        var termList = ReadableTerms;
        for (var index = 0; index < termList.Length; index++)
        {
            termStr.Append(termList[index]);
            if (index != termList.Length - 1)
            {
                termStr.Append(',');
            }

            if (index != 0 && index % 3 == 0)
            {
                termStr.Append('\n');
            }
        }

        return new PlanCourse()
        {
            Name = Course.NameZh,
            LessonType = Course.LessonType,
            ExamMode = Course.DefaultExamMode.Name,
            CourseTypeName = Course.CourseType.Name,
            Credits = Course.Credits,
            TermStr = termStr.ToString()
        };
    }
}

[Serializable]
public class CourseItem
{
    [JsonPropertyName("nameZh")] public string NameZh { get; set; } = "";
    [JsonPropertyName("lessonType")] public string LessonType { get; set; } = "";
    [JsonPropertyName("defaultExamMode")] public DefaultExamMode DefaultExamMode { get; set; } = new();

    [JsonPropertyName("courseType")] public CourseType CourseType { get; set; } = new();
    [JsonPropertyName("credits")] public double Credits { get; set; }
}

[Serializable]
public class DefaultExamMode
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

[Serializable]
public class CourseType
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

[Serializable]
public class PlanCourse
{
    public string Name { get; init; } = "";
    public string LessonType { get; set; } = "";
    public string ExamMode { get; set; } = "";
    public string CourseTypeName { get; set; } = "";
    public double Credits { get; set; }
    public string TermStr { get; init; } = "";
}
