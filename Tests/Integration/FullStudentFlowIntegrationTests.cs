using EduApi.Data;
using EduApi.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using XAUAT.EduApi.Repos;
using XAUAT.EduApi.Services;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Exceptions;

namespace XAUAT.EduApi.Tests.Integration;

/// <summary>
/// 完整学生业务流程集成测试
/// </summary>
public class FullStudentFlowIntegrationTests : IDisposable
{
    private readonly DbContextOptions<EduContext> _dbContextOptions;
    private readonly EduContext _dbContext;
    private readonly ScoreService _scoreService;
    private readonly CourseService _courseService;
    private readonly PaymentService _paymentService;
    private readonly InfoService _infoService;
    private readonly ScoreRepository _scoreRepository;
    private readonly Mock<IExamService> _examServiceMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ICacheService> _cacheServiceMock;

    public FullStudentFlowIntegrationTests()
    {
        var uniqueDatabaseName = "TestDatabase_FullStudentFlow_" + Guid.NewGuid();

        _dbContextOptions = new DbContextOptionsBuilder<EduContext>()
            .UseInMemoryDatabase(databaseName: uniqueDatabaseName)
            .Options;

        _dbContext = new EduContext(_dbContextOptions);
        _dbContext.Database.EnsureCreated();

        // 创建ExamService模拟
        _examServiceMock = new Mock<IExamService>();
        _examServiceMock.Setup(m => m.GetThisSemester(It.IsAny<string>()))
            .ReturnsAsync(new SemesterItem { Value = "2025-2026-1", Text = "2025-2026学年第一学期" });

        // 创建HttpClientFactory模拟
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        // 创建CacheService模拟 - 默认调用工厂方法
        _cacheServiceMock = new Mock<ICacheService>();
        _cacheServiceMock.Setup(x => x.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<CourseActivity>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns(async (string key, Func<Task<List<CourseActivity>>> factory, TimeSpan? exp, CacheLevel level, int priority, CancellationToken ct, bool isUse) =>
                await factory());

        // 默认: ScoreService GetOrCreateAsync 返回空列表
        _cacheServiceMock.Setup(x => x.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<ScoreResponse>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoreResponse>());

        // 创建日志模拟
        var courseLogger = new Mock<ILogger<CourseService>>().Object;
        var scoreLogger = new Mock<ILogger<ScoreService>>().Object;
        var paymentLogger = new Mock<ILogger<PaymentService>>().Object;

        // 创建成绩仓库和服务
        _scoreRepository = new ScoreRepository(new TestDbContextFactory(_dbContextOptions));
        _scoreService = new ScoreService(
            _httpClientFactoryMock.Object,
            scoreLogger,
            _examServiceMock.Object,
            _cacheServiceMock.Object,
            _scoreRepository);

        // 创建信息服务
        _infoService = new InfoService();

        // 创建课程服务
        _courseService = new CourseService(
            _httpClientFactoryMock.Object,
            courseLogger,
            _examServiceMock.Object,
            _cacheServiceMock.Object, _infoService);

        // 创建支付服务
        _paymentService = new PaymentService(
            _cacheServiceMock.Object,
            _httpClientFactoryMock.Object,
            paymentLogger);
    }

    [Fact]
    public async Task FullStudentFlow_ShouldWorkCorrectly()
    {
        var studentId = "123456";
        var cardNum = "123456";
        var cookie = "test-cookie";
        var semester = "2025-2026-1";

        var testScores = new List<ScoreResponse>
        {
            new ScoreResponse
            {
                Key = $"{studentId}_{semester}_CS101_计算机基础",
                Name = "计算机基础",
                Credit = "2.0",
                LessonCode = "CS101",
                LessonName = "计算机基础",
                Grade = "85",
                Gpa = "3.5",
                GradeDetail = "平时成绩: 90; 期末成绩: 80",
                UserId = studentId,
                Semester = semester
            },
            new ScoreResponse
            {
                Key = $"{studentId}_{semester}_MA101_高等数学",
                Name = "高等数学",
                Credit = "4.0",
                LessonCode = "MA101",
                LessonName = "高等数学",
                Grade = "90",
                Gpa = "4.0",
                GradeDetail = "平时成绩: 95; 期末成绩: 85",
                UserId = studentId,
                Semester = semester
            }
        };

        await _dbContext.Scores.AddRangeAsync(testScores);
        await _dbContext.SaveChangesAsync();

        // 设置ScoreService返回预加载的测试数据
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<ScoreResponse>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(testScores);

        // 设置缓存返回支付token
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<string>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-payment-token");

        // Act
        var timeInfo = _infoService.GetTime();
        var isInSchool = _infoService.IsGreatThanStart();
        var scoresResult = await _scoreService.GetScoresAsync(studentId, semester, cookie);
        var paymentToken = await _paymentService.Login(cardNum);

        // Assert
        Assert.NotNull(timeInfo);
        Assert.NotNull(timeInfo.StartTime);
        Assert.NotNull(timeInfo.EndTime);
        Assert.IsType<bool>(isInSchool);

        Assert.NotNull(scoresResult);
        Assert.Equal(2, scoresResult.Count);
        Assert.Equal("计算机基础", scoresResult[0].Name);
        Assert.Equal("高等数学", scoresResult[1].Name);
        Assert.Equal("85", scoresResult[0].Grade);
        Assert.Equal("90", scoresResult[1].Grade);

        Assert.NotNull(paymentToken);
        Assert.Equal("test-payment-token", paymentToken);
    }

    [Fact]
    public async Task MultipleServices_ShouldHandleExceptionsCorrectly()
    {
        var studentId = "123456";
        var cardNum = "123456";
        var cookie = "test-cookie";
        var semester = "2025-2026-1";

        // 设置缓存操作失败 - 清除之前的设置
        _cacheServiceMock.Reset();
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<string>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Cache service failure"));

        // 设置ScoreService返回空列表以模拟降级处理
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<ScoreResponse>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoreResponse>());

        // 设置CourseService返回空列表
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<CourseActivity>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CourseActivity>());

        // 验证成绩服务在缓存失败时仍能降级处理
        var scoresResult = await _scoreService.GetScoresAsync(studentId, semester, cookie);
        Assert.NotNull(scoresResult);

        // 验证支付服务在缓存失败时能正确处理异常
        await Assert.ThrowsAsync<PaymentServiceException>(() =>
            _paymentService.Login(cardNum));

        // 验证课程服务在缓存失败时能降级处理（返回空列表而不抛出异常）
        var coursesResult = await _courseService.GetCoursesAsync(studentId, cookie);
        Assert.NotNull(coursesResult);
        Assert.Empty(coursesResult);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
