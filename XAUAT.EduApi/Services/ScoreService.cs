using System.Text.RegularExpressions;
using EduApi.Data.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Queues;
using XAUAT.EduApi.Repos;

namespace XAUAT.EduApi.Services;

public interface IScoreService
{
    Task<List<ScoreResponse>> GetScoresAsync(string studentId, string semester, string cookie, string language = "zh");
    Task<SemesterResult> ParseSemesterAsync(string? studentId, string cookie, string language = "zh");
    Task<SemesterItem> GetThisSemesterAsync(string cookie, string language = "zh", string? studentId = null);
}

public class ScoreService(
    IHttpClientFactory httpClientFactory,
    ILogger<ScoreService> logger,
    IExamService examService,
    ICacheService cacheService,
    IScoreRepository scoreRepository,
    IScorePersistenceQueue? scorePersistenceQueue = null,
    ITestAccountResolver? testAccountResolver = null,
    ITestDataProvider? testDataProvider = null,
    IStudentRateLimitExecutor? rateLimitExecutor = null)
    : IScoreService
{
    private readonly IScorePersistenceQueue _scorePersistenceQueue =
        scorePersistenceQueue ?? NullScorePersistenceQueue.Instance;

    private readonly IStudentRateLimitExecutor _rateLimitExecutor =
        rateLimitExecutor ?? NoOpStudentRateLimitExecutor.Instance;

    public async Task<List<ScoreResponse>> GetScoresAsync(string studentId, string semester, string cookie,
        string language = "zh")
    {
        if (testAccountResolver?.IsTestAccount(cookie: cookie, studentId: studentId) == true)
        {
            logger.LogInformation("测试账号命中成绩测试数据，studentId: {StudentId}, semester: {Semester}", studentId, semester);
            return await testDataProvider!.GetScoresAsync(semester);
        }

        logger.LogInformation("开始获取考试分数");

        if (string.IsNullOrEmpty(studentId) || string.IsNullOrEmpty(cookie))
        {
            throw new ArgumentNullException(nameof(studentId));
        }

        return await cacheService.GetOrCreateAsync(
            CacheKeys.Scores(studentId, semester),
            async () =>
            {
                var split = HttpContextStudentExtensions.ParseStudentIds(studentId);

                // 使用 Task.WhenAll 并行获取所有学生的成绩，解决 N+1 问题
                var tasks = split.Select((s, index) => GetScoreResponseWithIndex(s, semester, cookie, language, index))
                    .ToArray();
                var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

                // 合并结果
                return allResults.SelectMany(r => r).ToList();
            },
            TimeSpan.FromHours(1));
    }

    /// <summary>
    /// 获取成绩并标记是否为辅修
    /// </summary>
    private async Task<List<ScoreResponse>> GetScoreResponseWithIndex(string studentId, string semester, string cookie,
        string language, int index)
    {
        var scoreResponse = await GetScoreResponse(studentId, semester, cookie, language).ConfigureAwait(false);
        var scoreResponses = scoreResponse.ToList();

        // 非第一个学号的成绩标记为辅修
        if (index != 0)
        {
            foreach (var t in scoreResponses)
            {
                t.IsMinor = true;
            }
        }

        return scoreResponses;
    }

    public async Task<SemesterResult> ParseSemesterAsync(string? studentId, string cookie, string language = "zh")
    {
        if (testAccountResolver?.IsTestAccount(cookie: cookie, studentId: studentId) == true)
        {
            logger.LogInformation("测试账号命中学期列表测试数据，studentId: {StudentId}", studentId);
            return await testDataProvider!.GetSemesterResultAsync();
        }

        logger.LogInformation("开始解析学期数据");

        return await cacheService.GetOrCreateAsync(
            CacheKeys.SemesterResult(studentId),
            async () =>
            {
                return await _rateLimitExecutor.ExecuteAsync(
                    HttpContextStudentExtensions.ParseStudentIds(studentId),
                    async () =>
                    {
                        using var client = httpClientFactory.CreateClient();
                        client.ConfigureForEduSystem(cookie, HttpTimeouts.EduSystem);

                        var url = "https://swjw.xauat.edu.cn/student/for-std/grade/sheet";
                        if (!string.IsNullOrEmpty(studentId))
                        {
                            var split = HttpContextStudentExtensions.ParseStudentIds(studentId);
                            var firstId = split.FirstOrDefault();
                            url += $"/semester-index/{firstId}";
                        }

                        var html = await client.GetStringAsync(url).ConfigureAwait(false);

                        html.ThrowIfAuthOrRateLimited();

                        var result = new SemesterResult();
                        result.Parse(html);
                        return result;
                    });
            },
            TimeSpan.FromHours(1));
    }

    public async Task<SemesterItem> GetThisSemesterAsync(string cookie, string language = "zh",
        string? studentId = null)
    {
        return await _rateLimitExecutor.ExecuteAsync(
            HttpContextStudentExtensions.ParseStudentIds(studentId),
            () => examService.GetThisSemester(cookie, language));
    }

    private async Task<IEnumerable<ScoreResponse>> GetScoreResponse(string studentId, string semester, string cookie,
        string language)
    {
        // 判断是否为当前学期
        var thisSemester = await _rateLimitExecutor.ExecuteAsync(
            [studentId],
            () => examService.GetThisSemester(cookie, language)).ConfigureAwait(false);
        var isCurrentSemester = thisSemester != null! && thisSemester.Value == semester;

        if (!isCurrentSemester && thisSemester != null)
        {
            if (int.TryParse(semester, out var theSemesterInt) &&
                int.TryParse(thisSemester.Value, out var thisSemesterInt))
            {
                isCurrentSemester = Math.Abs(thisSemesterInt - theSemesterInt) <= 60;
            }
        }

        if (!isCurrentSemester)
        {
            // 从数据库查询
            var dbScores = await scoreRepository.GetByUserIdAsync(studentId).ConfigureAwait(false);
            dbScores = dbScores.Where(s => s.Semester == semester);

            // 如果数据库中有数据，直接返回
            var enumerable = dbScores as ScoreResponse[] ?? dbScores.ToArray();
            var scoreResponses = dbScores as ScoreResponse[] ?? enumerable.ToArray();
            if (scoreResponses.Length != 0)
            {
                return scoreResponses;
            }
        }

        var crawledScores = await CrawlScores(studentId, semester, cookie, language).ConfigureAwait(false);
        var scoresToSave = crawledScores.Select(score =>
        {
            // 为每个成绩项生成唯一键值
            score.Key = $"{studentId}_{semester}_{score.LessonCode}_{score.Name}".GetHashCode().ToString();
            // 设置外键关联
            score.UserId = studentId;
            score.Semester = semester;
            return score;
        }).ToList();

        if (isCurrentSemester)
        {
            return crawledScores;
        }

        try
        {
            await _scorePersistenceQueue
                .QueueAsync(new ScoreCrawledEvent(scoresToSave))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发布成绩持久化事件时出错");
            // 即使入队失败也返回爬取的数据
        }

        return crawledScores;
    }

    private async Task<List<ScoreResponse>> CrawlScores(string studentId, string semester, string cookie,
        string language)
    {
        var client = httpClientFactory.CreateClient();
        client.ConfigureForEduSystem(cookie, HttpTimeouts.EduSystem);

        var retryPolicy = Policy
            .Handle<Exceptions.RateLimitException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(6 * Math.Pow(2, retryAttempt - 1)));

        return await _rateLimitExecutor.ExecuteAsync([studentId], () =>
            retryPolicy.ExecuteAsync(async () =>
            {
                var response = await client.GetAsync(
                        $"https://swjw.xauat.edu.cn/student/for-std/grade/sheet/info/{studentId}?semester={semester}")
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("获取成绩数据失败，HTTP状态码: {StatusCode}", response.StatusCode);
                    return [];
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                content.ThrowIfRateLimited();

                if (content.StartsWith('<'))
                {
                    logger.LogWarning("获取成绩数据失败，返回了HTML内容而非JSON，可能Cookie已过期");
                    return content.Contains("登入页面") ? throw new Exceptions.UnAuthenticationError() : [];
                }

                JObject json;
                try
                {
                    json = JObject.Parse(content);
                }
                catch (JsonReaderException ex)
                {
                    logger.LogError(ex, "解析成绩JSON数据失败，原始内容: {Content}", content);
                    return [];
                }

                var list = (json["semesterId2studentGrades"]?[semester]!).Select(item =>
                {
                    var gradeDetail = string.Join("; ",
                        Regex.Matches(item["gradeDetail"]!.ToString(), @"<span[^>]*>([^<]+)<\/span>")
                            .Select(m => m.Groups[1].Value.Trim()));

                    return new ScoreResponse()
                    {
                        Name = Truncate(item["course"]!["nameZh"]!.ToString(), 256),
                        Credit = item["course"]!["credits"]!.ToString(),
                        LessonCode = item["lessonCode"]!.ToString(),
                        LessonName = Truncate(item["lessonNameZh"]!.ToString(), 256),
                        Grade = Truncate(item["gaGrade"]!.ToString(), 256),
                        Gpa = item["gp"]!.ToString(),
                        GradeDetail = Truncate(gradeDetail, 512),
                    };
                }).ToList();

                if (list.Count == 0) return list;

                var thisSemesterCache = await cacheService.GetAsync<string>(CacheKeys.ThisSemester);
                if (string.IsNullOrEmpty(thisSemesterCache))
                {
                    await _rateLimitExecutor
                        .ExecuteAsync([studentId], () => examService.GetThisSemester(cookie, language))
                        .ConfigureAwait(false);
                }

                return list;
            }));
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}