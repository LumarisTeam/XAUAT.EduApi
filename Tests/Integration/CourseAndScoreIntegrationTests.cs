using Moq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using XAUAT.EduApi.Services;
using XAUAT.EduApi.Repos;
using EduApi.Data;
using EduApi.Data.Models;
using XAUAT.EduApi.Caching;

namespace XAUAT.EduApi.Tests.Integration;

/// <summary>
/// 课程和成绩业务流程集成测试
/// </summary>
public class CourseAndScoreIntegrationTests : IDisposable
{
    private readonly DbContextOptions<EduContext> _dbContextOptions;
    private readonly EduContext _dbContext;
    private readonly ScoreService _scoreService;
    private readonly CourseService _courseService;
    private readonly ScoreRepository _scoreRepository;
    private readonly Mock<IExamService> _examServiceMock;

    /// <summary>
    /// 构造函数，初始化测试依赖
    /// </summary>
    public CourseAndScoreIntegrationTests()
    {
        // 为每个测试创建唯一的数据库名称，确保测试隔离
        var uniqueDatabaseName = "TestDatabase_CourseScore_" + Guid.NewGuid();

        // 使用内存数据库进行集成测试
        _dbContextOptions = new DbContextOptionsBuilder<EduContext>()
            .UseInMemoryDatabase(databaseName: uniqueDatabaseName)
            .Options;

        // 创建数据库上下文
        _dbContext = new EduContext(_dbContextOptions);

        // 初始化数据库
        _dbContext.Database.EnsureCreated();

        // 创建公共依赖项
        var httpClientFactoryMock2 = new Mock<IHttpClientFactory>();
        httpClientFactoryMock2.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var httpClientFactory = httpClientFactoryMock2.Object;
        var cacheServiceForScore = new Mock<ICacheService>();
        // 设置透传用于ScoreService
        cacheServiceForScore
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<ScoreResponse>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns(async (string key, Func<Task<List<ScoreResponse>>> factory, TimeSpan? exp, CacheLevel level, int priority, CancellationToken ct, bool isUse) =>
                await factory());

        // 创建ExamService模拟
        _examServiceMock = new Mock<IExamService>();
        _examServiceMock.Setup(m => m.GetThisSemester(It.IsAny<string>()))
            .ReturnsAsync(new SemesterItem { Value = "2025-2026-1", Text = "2025-2026学年第一学期" });

        // 创建课程服务
        var courseLogger = new Mock<ILogger<CourseService>>().Object;
        
        var infoService = new InfoService();

        // 设置HttpClientFactory返回一个有效的HttpClient实例
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        // 创建CacheService模拟
        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(x => x.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<CourseActivity>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns<string, Func<Task<List<CourseActivity>>>, TimeSpan?, CacheLevel, int, CancellationToken, bool>(async (
                key, factory, expiration, level, priority, token, isUse) => await factory());

        _courseService = new CourseService(
            httpClientFactoryMock.Object,
            courseLogger,
            _examServiceMock.Object,
            cacheServiceMock.Object, 
            infoService);

        // 创建成绩仓库和服务
        var scoreLogger = new Mock<ILogger<ScoreService>>().Object;
        _scoreRepository = new ScoreRepository(new TestDbContextFactory(_dbContextOptions));
        _scoreService = new ScoreService(
            httpClientFactory,
            scoreLogger,
            _examServiceMock.Object,
            cacheServiceForScore.Object,
            _scoreRepository);
    }

    /// <summary>
    /// 测试课程和成绩的综合业务流程
    /// </summary>
    [Fact]
    public async Task TestCourseAndScoreBusinessFlow()
    {
        // Arrange
        var studentId = "123456";
        var cookie = "test-cookie";
        var semester = "2024-2025-1"; // 非当前学期，走数据库查询路径

        // 添加测试成绩数据到数据库
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
            }
        };

        await _dbContext.Scores.AddRangeAsync(testScores);
        await _dbContext.SaveChangesAsync();

        // Act - 获取成绩数据
        var scoresResult = await _scoreService.GetScoresAsync(studentId, semester, cookie);

        // Assert - 验证成绩数据
        Assert.NotNull(scoresResult);
        Assert.Single(scoresResult);
        Assert.Equal("计算机基础", scoresResult[0].Name);
        Assert.Equal("85", scoresResult[0].Grade);

        // Act - 尝试获取课程（预期会失败，因为HttpClient是模拟的，但应处理异常）
        // 使用ThrowsAnyAsync来捕获所有Exception子类，包括JsonReaderException
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    /// <summary>
    /// 测试多个学生成绩的查询流程
    /// </summary>
    [Fact]
    public async Task TestMultipleStudentsScoreFlow()
    {
        // Arrange
        var semester = "2024-2025-1"; // 非当前学期，走数据库查询路径
        var cookie = "test-cookie";

        // 添加多个学生的测试成绩数据
        var testScores = new List<ScoreResponse>
        {
            new ScoreResponse
            {
                Key = $"123456_{semester}_CS101_计算机基础",
                Name = "计算机基础",
                Credit = "2.0",
                LessonCode = "CS101",
                LessonName = "计算机基础",
                Grade = "85",
                Gpa = "3.5",
                UserId = "123456",
                Semester = semester
            },
            new ScoreResponse
            {
                Key = $"789012_{semester}_CS101_计算机基础",
                Name = "计算机基础",
                Credit = "2.0",
                LessonCode = "CS101",
                LessonName = "计算机基础",
                Grade = "90",
                Gpa = "4.0",
                UserId = "789012",
                Semester = semester
            }
        };

        await _dbContext.Scores.AddRangeAsync(testScores);
        await _dbContext.SaveChangesAsync();

        // Act - 获取第一个学生的成绩
        var scoresResult1 = await _scoreService.GetScoresAsync("123456", semester, cookie);

        // Assert - 验证第一个学生的成绩
        Assert.NotNull(scoresResult1);
        Assert.Single(scoresResult1);
        Assert.Equal("123456", scoresResult1[0].UserId);

        // Act - 获取第二个学生的成绩
        var scoresResult2 = await _scoreService.GetScoresAsync("789012", semester, cookie);

        // Assert - 验证第二个学生的成绩
        Assert.NotNull(scoresResult2);
        Assert.Single(scoresResult2);
        Assert.Equal("789012", scoresResult2[0].UserId);
    }

    /// <summary>
    /// 清理测试资源
    /// </summary>
    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
