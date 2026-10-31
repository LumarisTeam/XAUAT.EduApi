using System.Net;
using EduApi.Data.Models;
using Moq;
using Moq.Protected;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

public class ElectricityServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly ElectricityService _service;

    public ElectricityServiceTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _cacheServiceMock = new Mock<ICacheService>();

        SetupGetOrCreatePassThrough<double?>();
        SetupGetOrCreatePassThrough<List<ElectricData>>();

        _cacheServiceMock
            .Setup(x => x.GetAsync<string>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _cacheServiceMock
            .Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _service = new ElectricityService(_httpClientFactoryMock.Object, _cacheServiceMock.Object);
    }

    private void SetupGetOrCreatePassThrough<T>()
    {
        _cacheServiceMock
            .Setup(x => x.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<T>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns(async (string key, Func<Task<T>> factory, TimeSpan? expiration, CacheLevel level,
                int priority, CancellationToken cancellationToken, bool isUse) => await factory());
    }

    [Fact]
    public async Task FetchCurrentBalanceAsync_ShouldReturnBalance_WhenUrlProvided()
    {
        var url = "https://example.com/wxAccount?id=1";
        const string html = "<html><body>充值余额：¥12.34</body></html>";

        ConfigureHttpClient(html);

        var result = await _service.FetchCurrentBalanceAsync(url);

        Assert.Equal(12.34, result);
        _cacheServiceMock.Verify(x => x.GetOrCreateAsync(
            CacheKeys.ElectricityBalance(url),
            It.IsAny<Func<Task<double?>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task FetchCurrentBalanceAsync_ShouldReturnNull_WhenUrlNotProvided()
    {
        var result = await _service.FetchCurrentBalanceAsync();

        Assert.Null(result);
        _cacheServiceMock.Verify(x => x.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<double?>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<bool>()), Times.Never);
        _httpClientFactoryMock.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FetchWeeklyDataAsync_ShouldParseAndAggregateHourlyData()
    {
        var sourceUrl = "https://example.com/wxAccount?id=3";
        const string html = """
                            <html>
                              <body>
                                <table>
                                  <tr><td>1</td><td>2026/05/01 08:12</td><td>1.2</td></tr>
                                  <tr><td>2</td><td>2026/05/01 08:40</td><td>0.8</td></tr>
                                  <tr><td>3</td><td>2026/05/01 09:05</td><td>0.5</td></tr>
                                </table>
                              </body>
                            </html>
                            """;

        ConfigureHttpClient(html);

        var result = await _service.FetchWeeklyDataAsync(sourceUrl);

        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTime(2026, 5, 1, 8, 0, 0), result[0].Timestamp);
        Assert.Equal(2.0, result[0].Value);
        Assert.Equal(new DateTime(2026, 5, 1, 9, 0, 0), result[1].Timestamp);
        Assert.Equal(0.5, result[1].Value);
        _cacheServiceMock.Verify(x => x.GetOrCreateAsync(
            CacheKeys.ElectricityWeeklyData(sourceUrl),
            It.IsAny<Func<Task<List<ElectricData>>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task FetchWeeklyDataAsync_ShouldReturnEmpty_WhenPageHasNoTable()
    {
        // 生产场景：登录失效或页面结构变化时，上游返回的是没有 <table> 的错误页。
        var sourceUrl = "https://example.com/wxAccount?id=5";
        const string html = "<html><body>登录已失效</body></html>";

        ConfigureHttpClient(html);

        var result = await _service.FetchWeeklyDataAsync(sourceUrl);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRechargeUrlAsync_ShouldReturnDerivedUrl_WhenSourceUrlExists()
    {
        var sourceUrl = "https://example.com/wxAccount?id=4";

        var result = await _service.GetRechargeUrlAsync(sourceUrl);

        Assert.Equal("https://example.com/wxCharge?id=4", result);
    }

    private void ConfigureHttpClient(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });

        var client = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(client);
    }
}
