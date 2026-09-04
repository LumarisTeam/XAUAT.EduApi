using System.Text.Json;
using EduApi.Data.Models;
using Microsoft.Extensions.Options;
using XAUAT.EduApi.Configuration;
using XAUAT.EduApi.Controllers;
using XAUAT.EduApi.Controllers.Old;

namespace XAUAT.EduApi.Services;

public interface ITestDataProvider
{
    Task<List<CourseActivity>> GetCoursesAsync(CancellationToken cancellationToken = default);
    Task<SemesterResult> GetSemesterResultAsync(CancellationToken cancellationToken = default);
    Task<SemesterItem> GetCurrentSemesterAsync(CancellationToken cancellationToken = default);
    Task<List<ScoreResponse>> GetScoresAsync(string semester, CancellationToken cancellationToken = default);
    Task<ExamResponse> GetExamResponseAsync(CancellationToken cancellationToken = default);
    Task<List<PlanCourse>> GetProgramAsync(CancellationToken cancellationToken = default);
    Task<List<StudyModule>> GetCompletionAsync(CancellationToken cancellationToken = default);
    Task<string> GetPaymentTokenAsync(CancellationToken cancellationToken = default);
    Task<List<PaymentModel>> GetPaymentTurnoverAsync(CancellationToken cancellationToken = default);
    Task<double> GetPaymentBalanceAsync(CancellationToken cancellationToken = default);
}

public class TestDataProvider(
    IOptions<TestAccountOptions> options,
    IHostEnvironment hostEnvironment,
    ILogger<TestDataProvider> logger) : ITestDataProvider
{
    private readonly TestAccountOptions _options = options.Value;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<List<CourseActivity>> GetCoursesAsync(CancellationToken cancellationToken = default)
        => ReadFixtureAsync<List<CourseActivity>>("courses.json", cancellationToken);

    public async Task<SemesterResult> GetSemesterResultAsync(CancellationToken cancellationToken = default)
    {
        var items = await ReadFixtureAsync<List<SemesterItem>>("semesters.json", cancellationToken);
        var result = new SemesterResult();
        foreach (var item in items)
        {
            result.Data.Add(item);
        }

        return result;
    }

    public Task<SemesterItem> GetCurrentSemesterAsync(CancellationToken cancellationToken = default)
        => ReadFixtureAsync<SemesterItem>("current-semester.json", cancellationToken);

    public async Task<List<ScoreResponse>> GetScoresAsync(string semester, CancellationToken cancellationToken = default)
    {
        var fixtures = await ReadFixtureAsync<List<TestScoreFixtureItem>>("scores.json", cancellationToken);
        return fixtures
            .Where(item => string.Equals(item.Semester, semester, StringComparison.Ordinal))
            .Select(item => new ScoreResponse
            {
                Name = item.Name,
                LessonCode = item.LessonCode,
                LessonName = item.LessonName,
                Grade = item.Grade,
                Gpa = item.Gpa,
                GradeDetail = item.GradeDetail,
                Credit = item.Credit,
                IsMinor = item.IsMinor,
                UserId = _options.StudentId,
                Semester = item.Semester,
                Key = $"{_options.StudentId}_{item.Semester}_{item.LessonCode}_{item.Name}".GetHashCode().ToString()
            })
            .ToList();
    }

    public async Task<ExamResponse> GetExamResponseAsync(CancellationToken cancellationToken = default)
    {
        var exams = await ReadFixtureAsync<List<ExamInfo>>("exams.json", cancellationToken);
        return new ExamResponse
        {
            Exams = exams,
            CanClick = exams.Count != 0
        };
    }

    public Task<List<PlanCourse>> GetProgramAsync(CancellationToken cancellationToken = default)
        => ReadFixtureAsync<List<PlanCourse>>("program.json", cancellationToken);

    public Task<List<StudyModule>> GetCompletionAsync(CancellationToken cancellationToken = default)
        => ReadFixtureAsync<List<StudyModule>>("completion.json", cancellationToken);

    public Task<string> GetPaymentTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"test-token-{_options.StudentId}");
    }

    public async Task<List<PaymentModel>> GetPaymentTurnoverAsync(CancellationToken cancellationToken = default)
    {
        var records = await ReadFixtureAsync<List<PaymentTurnoverFixtureItem>>("payment-turnover.json", cancellationToken);
        return records
            .Select(item => new PaymentModel(item.TurnoverType, item.DatetimeStr, item.Resume, item.Tranamt))
            .ToList();
    }

    public async Task<double> GetPaymentBalanceAsync(CancellationToken cancellationToken = default)
    {
        var fixture = await ReadFixtureAsync<PaymentBalanceFixture>("payment-balance.json", cancellationToken);
        return fixture.Total;
    }

    private async Task<T> ReadFixtureAsync<T>(string fileName, CancellationToken cancellationToken)
    {
        var filePath = GetFixturePath(fileName);

        if (!File.Exists(filePath))
        {
            logger.LogError("测试数据夹具文件不存在: {FilePath}", filePath);
            throw new FileNotFoundException($"测试数据夹具文件不存在: {filePath}", filePath);
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var result = await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken);
            if (result == null)
            {
                throw new InvalidOperationException($"测试数据夹具为空或结构无效: {filePath}");
            }

            return result;
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            logger.LogError(ex, "读取测试数据夹具失败: {FilePath}", filePath);
            throw;
        }
    }

    private string GetFixturePath(string fileName)
    {
        var fixtureRoot = string.IsNullOrWhiteSpace(_options.FixturePath) ? "TestFixtures" : _options.FixturePath;
        return Path.IsPathRooted(fixtureRoot)
            ? Path.Combine(fixtureRoot, fileName)
            : Path.Combine(hostEnvironment.ContentRootPath, fixtureRoot, fileName);
    }

    private sealed class TestScoreFixtureItem
    {
        public string Semester { get; set; } = "";
        public string Name { get; set; } = "";
        public string LessonCode { get; set; } = "";
        public string LessonName { get; set; } = "";
        public string Grade { get; set; } = "";
        public string Gpa { get; set; } = "";
        public string GradeDetail { get; set; } = "";
        public string Credit { get; set; } = "";
        public bool IsMinor { get; set; }
    }

    private sealed class PaymentTurnoverFixtureItem
    {
        public string TurnoverType { get; set; } = "";
        public string DatetimeStr { get; set; } = "";
        public string Resume { get; set; } = "";
        public double Tranamt { get; set; }
    }

    private sealed class PaymentBalanceFixture
    {
        public double Total { get; set; }
    }
}
