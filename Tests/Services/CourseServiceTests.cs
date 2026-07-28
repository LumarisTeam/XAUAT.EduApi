using EduApi.Data.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using XAUAT.EduApi.Services;
using XAUAT.EduApi.Caching;

namespace XAUAT.EduApi.Tests.Services;

public class CourseServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<CourseService>> _loggerMock;
    private readonly Mock<IExamService> _examServiceMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly CourseService _courseService;

    public CourseServiceTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<CourseService>>();
        _examServiceMock = new Mock<IExamService>();
        _cacheServiceMock = new Mock<ICacheService>();

        // 默认: GetOrCreateAsync 返回空列表，不执行工厂方法
        _cacheServiceMock.Setup(x => x.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<CourseActivity>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CourseActivity>());

        var infoService = new InfoService();
        _courseService = new CourseService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            _examServiceMock.Object,
            _cacheServiceMock.Object,
            infoService);
    }

    private void SetupPassThrough()
    {
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
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowException_WhenStudentIdIsEmpty()
    {
        var studentId = string.Empty;
        var cookie = "test-cookie";

        SetupPassThrough();

        await Assert.ThrowsAsync<Exception>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowException_WhenCookieIsEmpty()
    {
        var studentId = "123456";
        var cookie = string.Empty;

        SetupPassThrough();

        await Assert.ThrowsAsync<Exception>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowInvalidOperationException_WhenSemesterIsEmpty()
    {
        var studentId = "123456";
        var cookie = "test-cookie";

        SetupPassThrough();

        _examServiceMock.Setup(x => x.GetThisSemester(cookie))
            .ReturnsAsync(new SemesterItem { Value = string.Empty, Text = string.Empty });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowException_WhenStudentIdIsNull()
    {
        string? studentId = null;
        var cookie = "test-cookie";

        SetupPassThrough();

        await Assert.ThrowsAsync<Exception>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowException_WhenCookieIsNull()
    {
        var studentId = "123456";
        string? cookie = null;

        SetupPassThrough();

        await Assert.ThrowsAsync<Exception>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldHandleMultipleStudentIds()
    {
        var studentId = "123456,789012";
        var cookie = "test-cookie";
        var semester = new SemesterItem { Value = "2024-2025-1", Text = "2024-2025学年第一学期" };

        SetupPassThrough();

        _examServiceMock.Setup(x => x.GetThisSemester(cookie))
            .ReturnsAsync(semester);

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""success"": true,
                    ""studentTableVm"": {
                        ""activities"": [
                            {
                                ""id"": 1,
                                ""name"": ""Test Course"",
                                ""room"": ""Room 101"",
                                ""teacherName"": ""Teacher 1"",
                                ""weekIndexes"": [1, 2, 3],
                                ""dayOfWeek"": 1,
                                ""startSection"": 1,
                                ""sectionCount"": 2
                            }
                        ]
                    }
                }")
            });

        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handlerMock.Object, disposeHandler: false));

        var result = await _courseService.GetCoursesAsync(studentId, cookie);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldHandleInvalidStudentIdFormat()
    {
        var studentId = "123456,,789012";
        var cookie = "test-cookie";
        var semester = new SemesterItem { Value = "2024-2025-1", Text = "2024-2025学年第一学期" };

        SetupPassThrough();

        _examServiceMock.Setup(x => x.GetThisSemester(cookie))
            .ReturnsAsync(semester);

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""success"": true,
                    ""studentTableVm"": {
                        ""activities"": [
                            {
                                ""id"": 1,
                                ""name"": ""Test Course"",
                                ""room"": ""Room 101"",
                                ""teacherName"": ""Teacher 1"",
                                ""weekIndexes"": [1, 2, 3],
                                ""dayOfWeek"": 1,
                                ""startSection"": 1,
                                ""sectionCount"": 2
                            }
                        ]
                    }
                }")
            });

        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handlerMock.Object, disposeHandler: false));

        var result = await _courseService.GetCoursesAsync(studentId, cookie);

        Assert.NotNull(result);
        Assert.True(result.Count > 0);
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowHttpRequestException_WhenApiReturns404()
    {
        var studentId = "123456";
        var cookie = "test-cookie";
        var semester = new SemesterItem { Value = "2024-2025-1", Text = "2024-2025学年第一学期" };

        SetupPassThrough();

        _examServiceMock.Setup(x => x.GetThisSemester(cookie))
            .ReturnsAsync(semester);

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.NotFound
            });

        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handlerMock.Object, disposeHandler: false));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowException_WhenApiReturnsInvalidJson()
    {
        var studentId = "123456";
        var cookie = "test-cookie";
        var semester = new SemesterItem { Value = "2024-2025-1", Text = "2024-2025学年第一学期" };

        SetupPassThrough();

        _examServiceMock.Setup(x => x.GetThisSemester(cookie))
            .ReturnsAsync(semester);

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("invalid json")
            });

        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handlerMock.Object, disposeHandler: false));

        await Assert.ThrowsAsync<Newtonsoft.Json.JsonReaderException>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowInvalidOperationException_WhenApiReturnsEmptyCourses()
    {
        var studentId = "123456";
        var cookie = "test-cookie";
        var semester = new SemesterItem { Value = "2024-2025-1", Text = "2024-2025学年第一学期" };

        SetupPassThrough();

        _examServiceMock.Setup(x => x.GetThisSemester(cookie))
            .ReturnsAsync(semester);

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""success"": true,
                    ""studentTableVm"": {
                        ""activities"": []
                    }
                }")
            });

        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handlerMock.Object, disposeHandler: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _courseService.GetCoursesAsync(studentId, cookie));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldReturnTestFixtureData_WhenTestAccountMatched()
    {
        var resolver = new Mock<ITestAccountResolver>();
        resolver.Setup(x => x.IsTestAccount("test-cookie", "20239999", null)).Returns(true);

        var provider = new Mock<ITestDataProvider>();
        provider.Setup(x => x.GetCoursesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CourseActivity
                {
                    CourseName = "测试数据结构",
                    CourseCode = "TEST101"
                }
            ]);

        var courseService = new CourseService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            _examServiceMock.Object,
            _cacheServiceMock.Object,
            new InfoService(),
            resolver.Object,
            provider.Object);

        var result = await courseService.GetCoursesAsync("20239999", "test-cookie");

        Assert.Single(result);
        Assert.Equal("测试数据结构", result[0].CourseName);
        _cacheServiceMock.Verify(x => x.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<List<CourseActivity>>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _httpClientFactoryMock.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }
}
