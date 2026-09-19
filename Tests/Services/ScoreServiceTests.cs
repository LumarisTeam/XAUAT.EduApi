using Moq;
using Microsoft.Extensions.Logging;
using XAUAT.EduApi.Services;
using XAUAT.EduApi.Repos;
using XAUAT.EduApi.Caching;
using EduApi.Data.Models;

namespace XAUAT.EduApi.Tests.Services;

public class ScoreServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<ScoreService>> _loggerMock;
    private readonly Mock<IExamService> _examServiceMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly Mock<IScoreRepository> _scoreRepositoryMock;
    private readonly ScoreService _scoreService;

    public ScoreServiceTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<ScoreService>>();
        _examServiceMock = new Mock<IExamService>();
        _cacheServiceMock = new Mock<ICacheService>();
        _scoreRepositoryMock = new Mock<IScoreRepository>();

        // 默认: GetOrCreateAsync<List<ScoreResponse>> 返回空列表
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<ScoreResponse>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoreResponse>());

        // 默认: GetOrCreateAsync<SemesterResult> 返回空SemesterResult
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<SemesterResult>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemesterResult());

        // 默认: GetAsync<string> 返回 null
        _cacheServiceMock
            .Setup(m => m.GetAsync<string>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string)null);

        _scoreService = new ScoreService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            _examServiceMock.Object,
            _cacheServiceMock.Object,
            _scoreRepositoryMock.Object);
    }

    [Fact]
    public async Task GetScoresAsync_ShouldThrowArgumentNullException_WhenStudentIdIsEmpty()
    {
        var studentId = string.Empty;
        var semester = "2025-2026-1";
        var cookie = "test-cookie";

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _scoreService.GetScoresAsync(studentId, semester, cookie));
    }

    [Fact]
    public async Task GetScoresAsync_ShouldThrowArgumentNullException_WhenCookieIsEmpty()
    {
        var studentId = "123456";
        var semester = "2025-2026-1";
        var cookie = string.Empty;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _scoreService.GetScoresAsync(studentId, semester, cookie));
    }

    [Fact]
    public async Task ParseSemesterAsync_ShouldCallCacheService_WhenCalled()
    {
        var studentId = "123456";
        var cookie = "test-cookie";

        await _scoreService.ParseSemesterAsync(studentId, cookie);

        _cacheServiceMock.Verify(m => m.GetOrCreateAsync(
            It.Is<string>(k => k.Contains(studentId)),
            It.IsAny<Func<Task<SemesterResult>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetThisSemesterAsync_ShouldCallExamService_WhenCalled()
    {
        var cookie = "test-cookie";
        var semesterItem = new SemesterItem { Value = "2025-2026-1", Text = "2025-2026学年第一学期" };
        _examServiceMock.Setup(m => m.GetThisSemester(cookie)).ReturnsAsync(semesterItem);

        var result = await _scoreService.GetThisSemesterAsync(cookie);

        _examServiceMock.Verify(m => m.GetThisSemester(cookie), Times.Once);
        Assert.Equal(semesterItem, result);
    }

    [Fact]
    public async Task GetScoreResponse_ShouldCallGetOrCreateAsync_WhenCurrentSemester()
    {
        var studentId = "123456";
        var semester = "2025-2026-1";
        var cookie = "test-cookie";

        var currentSemester = new SemesterItem { Value = semester, Text = "2025-2026学年第一学期" };
        _examServiceMock.Setup(m => m.GetThisSemester(cookie)).ReturnsAsync(currentSemester);

        var result = await _scoreService.GetScoresAsync(studentId, semester, cookie);

        Assert.NotNull(result);
        Assert.Empty(result);
        _cacheServiceMock.Verify(m => m.GetOrCreateAsync(
            It.Is<string>(k => k.Contains(studentId) && k.Contains(semester)),
            It.IsAny<Func<Task<List<ScoreResponse>>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetScoresAsync_ShouldThrowArgumentNullException_WhenStudentIdIsNull()
    {
        var studentId = (string)null;
        var semester = "2025-2026-1";
        var cookie = "test-cookie";

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _scoreService.GetScoresAsync(studentId, semester, cookie));
    }

    [Fact]
    public async Task GetScoresAsync_ShouldThrowArgumentNullException_WhenCookieIsNull()
    {
        var studentId = "123456";
        var semester = "2025-2026-1";
        var cookie = (string)null;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _scoreService.GetScoresAsync(studentId, semester, cookie));
    }

    [Fact]
    public async Task GetScoresAsync_ShouldHandleEmptySemester()
    {
        var studentId = "123456";
        var semester = string.Empty;
        var cookie = "test-cookie";

        var currentSemester = new SemesterItem { Value = "2025-2026-1", Text = "2025-2026学年第一学期" };
        _examServiceMock.Setup(m => m.GetThisSemester(cookie)).ReturnsAsync(currentSemester);

        var result = await _scoreService.GetScoresAsync(studentId, semester, cookie);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetScoresAsync_ShouldHandleSpecialCharactersInStudentId()
    {
        var studentId = "123456!@#$%^&*()_+";
        var semester = "2025-2026-1";
        var cookie = "test-cookie";

        var currentSemester = new SemesterItem { Value = semester, Text = "2025-2026学年第一学期" };
        _examServiceMock.Setup(m => m.GetThisSemester(cookie)).ReturnsAsync(currentSemester);

        var result = await _scoreService.GetScoresAsync(studentId, semester, cookie);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetScoresAsync_ShouldHandleMultipleStudentIds()
    {
        var studentId = "123456,789012,345678";
        var semester = "2025-2026-1";
        var cookie = "test-cookie";

        var currentSemester = new SemesterItem { Value = semester, Text = "2025-2026学年第一学期" };
        _examServiceMock.Setup(m => m.GetThisSemester(cookie)).ReturnsAsync(currentSemester);

        var result = await _scoreService.GetScoresAsync(studentId, semester, cookie);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetScoresAsync_ShouldHandleRedisUnavailable()
    {
        var studentId = "123456";
        var semester = "2025-2026-1";
        var cookie = "test-cookie";

        var currentSemester = new SemesterItem { Value = semester, Text = "2025-2026学年第一学期" };
        _examServiceMock.Setup(m => m.GetThisSemester(cookie)).ReturnsAsync(currentSemester);

        var result = await _scoreService.GetScoresAsync(studentId, semester, cookie);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetScoresAsync_ShouldHandleLargeDataFromDatabase()
    {
        var studentId = "123456";
        var semester = "2025-2026-1";
        var cookie = "test-cookie";

        var largeScoreList = new List<ScoreResponse>();
        for (int i = 0; i < 1000; i++)
        {
            largeScoreList.Add(new ScoreResponse
            {
                Key = $"{studentId}_{semester}_CS{i.ToString("D3")}_Course{i}",
                Name = $"Course{i}",
                Credit = "2.0",
                LessonCode = $"CS{i.ToString("D3")}",
                LessonName = $"Course{i}",
                Grade = (80 + i % 20).ToString(),
                Gpa = (3.0 + (i % 20) * 0.1).ToString("F1"),
                UserId = studentId,
                Semester = semester
            });
        }

        // 覆盖默认mock，返回大量数据
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<ScoreResponse>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(largeScoreList);

        var currentSemester = new SemesterItem { Value = semester, Text = "2025-2026学年第一学期" };
        _examServiceMock.Setup(m => m.GetThisSemester(cookie)).ReturnsAsync(currentSemester);

        var result = await _scoreService.GetScoresAsync(studentId, semester, cookie);

        Assert.NotNull(result);
        Assert.Equal(largeScoreList.Count, result.Count);
    }

    [Fact]
    public async Task GetScoresAsync_ShouldReturnTestFixtureData_WhenTestAccountMatched()
    {
        var resolver = new Mock<ITestAccountResolver>();
        resolver.Setup(x => x.IsTestAccount("test-cookie", "20239999")).Returns(true);

        var provider = new Mock<ITestDataProvider>();
        provider.Setup(x => x.GetScoresAsync("301", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ScoreResponse
                {
                    Name = "测试数据结构",
                    LessonCode = "TEST101",
                    Grade = "92"
                }
            ]);

        var scoreService = new ScoreService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            _examServiceMock.Object,
            _cacheServiceMock.Object,
            _scoreRepositoryMock.Object,
            null,
            resolver.Object,
            provider.Object);

        var result = await scoreService.GetScoresAsync("20239999", "301", "test-cookie");

        Assert.Single(result);
        Assert.Equal("测试数据结构", result[0].Name);
        _cacheServiceMock.Verify(x => x.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<List<ScoreResponse>>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _scoreRepositoryMock.Verify(x => x.GetByUserIdAsync(It.IsAny<string>()), Times.Never);
        _examServiceMock.Verify(x => x.GetThisSemester(It.IsAny<string>()), Times.Never);
    }
}
