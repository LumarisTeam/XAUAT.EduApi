using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EduApi.Data.Models;
using Microsoft.Extensions.Logging;
using Moq;
using XAUAT.EduApi.Services;
using XAUAT.EduApi.Caching;

namespace XAUAT.EduApi.Tests.Performance;

/// <summary>
/// CourseService性能测试
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[RPlotExporter]
public class CourseServicePerformanceTests
{
    private CourseService? _courseService;
    private Mock<IHttpClientFactory>? _httpClientFactoryMock;
    private Mock<ILogger<CourseService>>? _loggerMock;
    private Mock<IExamService>? _examServiceMock;
    private Mock<ICacheService>? _cacheServiceMock;
    private const string StudentId = "123456";
    private const string Cookie = "test-cookie";
    private const string SemesterValue = "2025-2026-1";
    private const string SemesterText = "2025-2026学年第一学期";

    /// <summary>
    /// 性能测试前的初始化设置
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        // 创建HttpClientFactory模拟
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        var httpClient = new HttpClient();
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // 创建日志模拟
        _loggerMock = new Mock<ILogger<CourseService>>();

        // 创建ExamService模拟
        _examServiceMock = new Mock<IExamService>();
        _examServiceMock.Setup(m => m.GetThisSemester(It.IsAny<string>()))
            .ReturnsAsync(new SemesterItem { Value = SemesterValue, Text = SemesterText });

        // 创建CacheService模拟
        _cacheServiceMock = new Mock<ICacheService>();
        _cacheServiceMock.Setup(x => x.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<CourseActivity>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns<string, Func<Task<List<CourseActivity>>>, TimeSpan?, CacheLevel, int, CancellationToken, bool>(async (
                key, factory, expiration, level, priority, token, isUse) => await factory());

        var infoService = new InfoService();
        var classTimeService = new ClassTimeService();

        // 创建课程服务
        _courseService = new CourseService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            _examServiceMock.Object,
            _cacheServiceMock.Object,
            infoService,
            classTimeService);
    }

    /// <summary>
    /// 测试单线程下的课程查询性能
    /// </summary>
    [Benchmark]
    public async Task GetCoursesAsync_SingleThread()
    {
        try
        {
            // 由于HttpClient调用会失败，我们预期会捕获异常
            await _courseService!.GetCoursesAsync(StudentId, Cookie);
        }
        catch (Exception)
        {
            // 预期会抛出异常，因为HttpClient是模拟的
            // 我们关注的是服务的处理时间，而不是实际的网络调用结果
        }
    }

    /// <summary>
    /// 测试10并发下的课程查询性能
    /// </summary>
    [Benchmark]
    public async Task GetCoursesAsync_Concurrent_10()
    {
        await RunConcurrentCoursesQuery(10);
    }

    /// <summary>
    /// 测试50并发下的课程查询性能
    /// </summary>
    [Benchmark]
    public async Task GetCoursesAsync_Concurrent_50()
    {
        await RunConcurrentCoursesQuery(50);
    }

    /// <summary>
    /// 测试100并发下的课程查询性能
    /// </summary>
    [Benchmark]
    public async Task GetCoursesAsync_Concurrent_100()
    {
        await RunConcurrentCoursesQuery(100);
    }

    /// <summary>
    /// 测试200并发下的课程查询性能
    /// </summary>
    [Benchmark]
    public async Task GetCoursesAsync_Concurrent_200()
    {
        await RunConcurrentCoursesQuery(200);
    }

    /// <summary>
    /// 测试500并发下的课程查询性能
    /// </summary>
    [Benchmark]
    public async Task GetCoursesAsync_Concurrent_500()
    {
        await RunConcurrentCoursesQuery(500);
    }

    /// <summary>
    /// 测试不同学生ID的课程查询性能
    /// </summary>
    [Benchmark]
    public async Task GetCoursesAsync_DifferentStudentIds()
    {
        // 测试不同学生的课程查询
        for (int i = 0; i < 50; i++)
        {
            var studentId = $"{100000 + i}";
            try
            {
                await _courseService!.GetCoursesAsync(studentId, Cookie);
            }
            catch (Exception)
            {
                // 预期会抛出异常
            }
        }
    }

    /// <summary>
    /// 测试多次调用的性能稳定性
    /// </summary>
    [Benchmark]
    public async Task GetCoursesAsync_MultipleCalls()
    {
        // 测试100次调用的性能
        for (int i = 0; i < 100; i++)
        {
            try
            {
                await _courseService!.GetCoursesAsync(StudentId, Cookie);
            }
            catch (Exception)
            {
                // 预期会抛出异常
            }
        }
    }

    /// <summary>
    /// 运行并发课程查询测试
    /// </summary>
    /// <param name="concurrentCount">并发数量</param>
    private async Task RunConcurrentCoursesQuery(int concurrentCount)
    {
        // 并发执行查询
        var tasks = new List<Task>();
        for (int i = 0; i < concurrentCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await _courseService!.GetCoursesAsync(StudentId, Cookie);
                }
                catch (Exception)
                {
                    // 预期会抛出异常
                }
            }));
        }

        await Task.WhenAll(tasks);
    }
}
