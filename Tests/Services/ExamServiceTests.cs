using Moq;
using Moq.Protected;
using Microsoft.Extensions.Logging;
using XAUAT.EduApi.Services;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Repos;
using EduApi.Data.Models;
using System.Net;
using System.Collections.Concurrent;
using XAUAT.EduApi.Extensions;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// ExamService单元测试
/// </summary>
public class ExamServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<ExamService>> _loggerMock;
    private readonly Mock<IInfoService> _infoServiceMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly Mock<IExamRepository> _examRepositoryMock;
    private readonly ExamService _examService;

    /// <summary>
    /// 构造函数，初始化测试依赖
    /// </summary>
    public ExamServiceTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<ExamService>>();
        _infoServiceMock = new Mock<IInfoService>();
        _cacheServiceMock = new Mock<ICacheService>();
        _examRepositoryMock = new Mock<IExamRepository>();

        // 默认配置GetOrCreateAsync：缓存未命中时调用工厂方法
        SetupGetOrCreatePassThrough<SemesterItem>();
        SetupGetOrCreatePassThrough<ExamResponse>();

        // 创建ExamService实例
        _examService = new ExamService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            _infoServiceMock.Object,
            _cacheServiceMock.Object,
            _examRepositoryMock.Object);
    }

    private void SetupGetOrCreatePassThrough<T>()
    {
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<T>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns(async (string key, Func<Task<T>> factory, TimeSpan? exp, CacheLevel level, int priority,
                    CancellationToken ct, bool isUse) =>
                await factory());
    }

    private void SetupGetOrCreateReturn<T>(T value)
    {
        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<T>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(value);
    }

    #region GetThisSemester Tests

    [Fact]
    public async Task GetThisSemester_ShouldReturnCachedData_WhenRedisCacheExists()
    {
        // Arrange
        var cookie = "test-cookie";
        var expectedSemester = new SemesterItem { Value = "301", Text = "2025-2026-1" };

        SetupGetOrCreateReturn(expectedSemester);

        // Act
        var result = await _examService.GetThisSemester(cookie);

        // Assert
        Assert.Equal(expectedSemester.Value, result.Value);
        Assert.Equal(expectedSemester.Text, result.Text);

        // Reset
        SetupGetOrCreatePassThrough<SemesterItem>();
    }

    [Fact]
    public async Task GetThisSemester_ShouldFetchFromHttp_WhenCacheMiss()
    {
        // Arrange
        var cookie = "test-cookie";

        _infoServiceMock.Setup(m => m.IsGreatThanStart()).Returns(true);
        _infoServiceMock.Setup(m => m.IsLessThanEnd()).Returns(true);

        var htmlContent = @"
            <html>
                <body>
                    <div>课表</div>
                    <select>
                        <option selected=""selected"" value=""301"">2025-2026-1</option>
                    </select>
                </body>
            </html>";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act
        var result = await _examService.GetThisSemester(cookie);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("301", result.Value);
    }

    [Fact]
    public async Task GetThisSemester_ShouldThrowUnAuthenticationError_WhenSessionExpired()
    {
        // Arrange
        var cookie = "expired-cookie";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html>登入页面</html>")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<XAUAT.EduApi.Exceptions.UnAuthenticationError>(() =>
            _examService.GetThisSemester(cookie));
    }

    #endregion

    #region GetExamArrangementsAsync Tests

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldCallGetExamArrangementAsync_WhenIdIsEmpty()
    {
        // Arrange
        var cookie = "test-cookie";
        string? id = null;

        // 模拟HttpClient
        var httpClientMock = new Mock<HttpClient>();
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClientMock.Object);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Exams);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldSplitId_WhenIdContainsComma()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123,456";

        // 模拟HttpClient
        var httpClientMock = new Mock<HttpClient>();
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClientMock.Object);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Exams);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldNotSplitId_WhenIdDoesNotContainComma()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123";

        // 模拟HttpClient
        var httpClientMock = new Mock<HttpClient>();
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClientMock.Object);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldReturnCachedData_WhenRedisCacheExists()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123";
        var expectedExam = new ExamResponse
        {
            Exams = new List<ExamInfo>
            {
                new ExamInfo { Name = "高等数学", Time = "2025-01-15 09:00", Location = "教学楼A101", Seat = "15" }
            },
            CanClick = true
        };

        SetupGetOrCreateReturn(expectedExam);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.Exams);
        Assert.Equal("高等数学", result.Exams[0].Name);

        // Reset
        SetupGetOrCreatePassThrough<ExamResponse>();
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldHandleHttpError()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Exams);
        Assert.False(result.CanClick);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldThrowRateLimitException_WithoutRetrying_WhenRateLimitPageReturned()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123";
        var callCount = 0;

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("<html>系统限流中</html>")
                };
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<XAUAT.EduApi.Exceptions.RateLimitException>(() =>
            _examService.GetExamArrangementsAsync(cookie, id));
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldFetchInParallel_WhenMultipleIds()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123,456,789";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html>var studentExamInfoVms = [];</html>")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldParseExamRows_WithoutTbody()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123";
        List<ExamRecord>? persistedRecords = null;
        var htmlContent = """
                          <html>
                              <table id="exams">
                                  <tr>
                                      <th>课程</th><th>时间</th><th>地点</th><th>座位</th>
                                  </tr>
                                  <tr>
                                      <td>高等数学</td>
                                      <td>2026-06-20 09:00-11:00</td>
                                      <td>教学楼A101</td>
                                      <td>12</td>
                                  </tr>
                              </table>
                          </html>
                          """;

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);
        _examRepositoryMock
            .Setup(x => x.MergeStudentExamsAsync(id, It.IsAny<IEnumerable<ExamRecord>>()))
            .Callback<string, IEnumerable<ExamRecord>>((_, records) => persistedRecords = records.ToList())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.Single(result.Exams);
        Assert.Equal("高等数学", result.Exams[0].Name);
        Assert.True(result.CanClick);
        Assert.NotNull(persistedRecords);
        var persistedRecord = Assert.Single(persistedRecords);
        Assert.Equal(DateTimeKind.Utc, persistedRecord.ExamTime.Kind);
        Assert.Equal(new DateTime(2026, 6, 20, 1, 0, 0, DateTimeKind.Utc), persistedRecord.ExamTime);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldParseExamRows_WhenOnlyFourColumnsPresent()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123";
        var htmlContent = """
                          <html>
                              <table id="exams">
                                  <tbody>
                                      <tr>
                                          <td>大学英语</td>
                                          <td>2026-06-22 14:00-16:00</td>
                                          <td>教学楼B302</td>
                                          <td>08</td>
                                      </tr>
                                  </tbody>
                              </table>
                          </html>
                          """;

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.Single(result.Exams);
        Assert.Equal("大学英语", result.Exams[0].Name);
        Assert.Equal("08", result.Exams[0].Seat);
        Assert.True(result.CanClick);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldTrimIds_WhenStudentIdContainsSpaces()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = " 123 , 456 ,, ";
        var cacheKeys = new ConcurrentBag<string>();

        _cacheServiceMock
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<ExamResponse>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns(async (string key, Func<Task<ExamResponse>> factory, TimeSpan? exp, CacheLevel level, int priority,
                CancellationToken ct, bool isUse) =>
            {
                cacheKeys.Add(key);
                return await factory();
            });

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html><table id=\"exams\"></table></html>")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.True(result.Error == null, $"unexpected error: {result.Error}");
        Assert.Equal(2, cacheKeys.Count);
        Assert.Contains(CacheKeys.ExamArrangement("123"), cacheKeys);
        Assert.Contains(CacheKeys.ExamArrangement("456"), cacheKeys);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldWorkWithoutRedis()
    {
        // Arrange
        var cookie = "test-cookie";
        var id = "123";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html>var studentExamInfoVms = [];</html>")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetExamArrangementsAsync_ShouldHandleEmptyCookie()
    {
        // Arrange
        var cookie = string.Empty;
        var id = "123";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html>登入页面</html>")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act
        var result = await _examService.GetExamArrangementsAsync(cookie, id);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetThisSemester_ShouldReturnTestFixtureData_WhenTestAccountMatched()
    {
        var resolver = new Mock<ITestAccountResolver>();
        resolver.Setup(x => x.IsTestAccount("test-cookie", null)).Returns(true);

        var provider = new Mock<ITestDataProvider>();
        provider.Setup(x => x.GetCurrentSemesterAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemesterItem { Value = "301", Text = "2025-2026-1" });

        var service = new ExamService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            _infoServiceMock.Object,
            _cacheServiceMock.Object,
            _examRepositoryMock.Object,
            resolver.Object,
            provider.Object);

        var result = await service.GetThisSemester("test-cookie");

        Assert.Equal("301", result.Value);
        _cacheServiceMock.Verify(x => x.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<SemesterItem>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<bool>()), Times.Never);
        _httpClientFactoryMock.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }

    #endregion

    // ------------------------------------------------------------------
    // ParseExamTimeRange：日历要的是**校本部本地墙钟**的起止，和 ExamRecord.ExamTime
    // 那条 UTC 路径不是一回事。下面的用例就是把这两条语义分开钉住。
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("2026-06-20 09:00-11:00")]   // 表格解析路径（ParseNow 已把 ~ 换成 -）
    [InlineData("2026-06-20 09:00~11:00")]   // 上游 JS 的原始形态
    [InlineData("2026-06-20 09:00～11:00")]  // 全角波浪号
    [InlineData("2026-06-20 09:00 - 11:00")] // 带空格的连字符
    public void ParseExamTimeRange_ShouldSplitEveryKnownSeparator(string timeRaw)
    {
        var (start, end) = ExamService.ParseExamTimeRange(timeRaw);

        Assert.Equal(new DateTime(2026, 6, 20, 9, 0, 0), start);
        Assert.Equal(new DateTime(2026, 6, 20, 11, 0, 0), end);
    }

    [Fact]
    public void ParseExamTimeRange_ShouldReturnLocalWallClock_NotUtc()
    {
        // 这条是防"考试 UID 整体偏移 8 小时"的哨兵：
        // UID 里嵌的是起始时刻，若这里返回 UTC（06:00 而不是 14:00），
        // 所有老订户都会看到重复的考试事件。
        var (start, end) = ExamService.ParseExamTimeRange("2026-07-13 14:00-16:00");

        Assert.Equal(DateTimeKind.Unspecified, start.Kind);
        Assert.Equal(14, start.Hour);
        Assert.Equal(16, end.Hour);
        Assert.Equal(new DateTime(2026, 7, 13), start.Date);
    }

    [Fact]
    public void ParseExamTimeRange_ShouldParseSlashSeparatedDate()
    {
        var (start, end) = ExamService.ParseExamTimeRange("2026/07/13 14:00~16:00");

        Assert.Equal(new DateTime(2026, 7, 13, 14, 0, 0), start);
        Assert.Equal(new DateTime(2026, 7, 13, 16, 0, 0), end);
    }

    [Fact]
    public void ParseExamTimeRange_ShouldFallBackToStartAsEnd_WhenOnlyStartGiven()
    {
        var (start, end) = ExamService.ParseExamTimeRange("2026-06-20 08:00");

        Assert.Equal(new DateTime(2026, 6, 20, 8, 0, 0), start);
        Assert.Equal(start, end);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("待定")]
    public void ParseExamTimeRange_ShouldReturnMinValue_WhenUnparsable(string timeRaw)
    {
        var (start, end) = ExamService.ParseExamTimeRange(timeRaw);

        Assert.Equal(DateTime.MinValue, start);
        Assert.Equal(DateTime.MinValue, end);
    }

    [Fact]
    public void ParseExamTime_ShouldNotChangeExamRecordUtcSemantics()
    {
        // 同一条串走两条路径：日历要本地 14:00，ExamRecord.ExamTime 要 UTC 06:00。
        // 后者的语义由 GetExamArrangementsAsync_ShouldParseExamRows_WithoutTbody 守着，
        // 这里只确认新加的范围解析没有把"本地"这条语义带回去污染它。
        var (localStart, localEnd) = ExamService.ParseExamTimeRange("2026-07-13 14:00-16:00");

        Assert.Equal(DateTimeKind.Unspecified, localStart.Kind);
        Assert.Equal(14, localStart.Hour);

        // 本地 14:00 对应的 UTC 就是 06:00 —— 两者相差 8 小时，绝不能混用
        var asUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, SchoolClock.TimeZone);
        Assert.Equal(6, asUtc.Hour);
        Assert.Equal(localEnd.Date, localStart.Date);
    }
}
