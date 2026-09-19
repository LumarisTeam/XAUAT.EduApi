using Moq;
using Moq.Protected;
using Newtonsoft.Json;
using System.Net;
using XAUAT.EduApi.Services;
using XAUAT.EduApi.Caching;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace XAUAT.EduApi.Tests.Services;

public class ProgramServiceTests
{
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly ProgramService _service;

    public ProgramServiceTests()
    {
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockCacheService = new Mock<ICacheService>();

        // 默认配置GetOrCreateAsync：缓存未命中时调用工厂方法
        SetupGetOrCreatePassThrough<List<PlanCourse>>();

        _service = new ProgramService(_mockHttpClientFactory.Object, _mockCacheService.Object);
    }

    private void SetupGetOrCreatePassThrough<T>()
    {
        _mockCacheService
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<T>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns(async (string key, Func<Task<T>> factory, TimeSpan? exp, CacheLevel level, int priority, CancellationToken ct, bool isUse) =>
                await factory());
    }

    [Fact]
    public async Task GetAllTrainProgram_ShouldReturnCachedData_WhenCacheExists()
    {
        // Arrange
        var id = "123";
        var cookie = "cookie";
        var cachedData = new List<PlanCourse> { new PlanCourse { Name = "Test Course" } };

        _mockCacheService
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<PlanCourse>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedData);

        // Act
        var result = await _service.GetAllTrainProgram(cookie, id);

        // Assert
        Assert.Single(result);
        Assert.Equal("Test Course", result[0].Name);
        _mockHttpClientFactory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);

        // Reset
        SetupGetOrCreatePassThrough<List<PlanCourse>>();
    }

    [Fact]
    public async Task GetAllTrainProgram_ShouldFetchFromApi_WhenCacheMiss()
    {
        // Arrange
        var id = "123";
        var cookie = "cookie";

        var apiResponse = new ProgramModel
        {
            Children = new List<ProgramModel>
            {
                new ProgramModel
                {
                    PlanCourses = new List<PlanCourses>
                    {
                        new PlanCourses
                        {
                            Course = new CourseItem { NameZh = "API Course" },
                            ReadableTerms = new[] { "Term1" }
                        }
                    }
                }
            }
        };
        var apiJson = JsonSerializer.Serialize(apiResponse);

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
           .Protected()
           .Setup<Task<HttpResponseMessage>>(
              "SendAsync",
              ItExpr.IsAny<HttpRequestMessage>(),
              ItExpr.IsAny<CancellationToken>()
           )
           .ReturnsAsync(new HttpResponseMessage
           {
               StatusCode = HttpStatusCode.OK,
               Content = new StringContent(apiJson)
           });

        var client = new HttpClient(handlerMock.Object);
        _mockHttpClientFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(client);

        // Act
        var result = await _service.GetAllTrainProgram(cookie, id);

        // Assert
        Assert.Single(result);
        Assert.Equal("API Course", result[0].Name);
    }

    [Fact]
    public async Task GetAllTrainProgram_ShouldThrowUnAuthenticationError_WhenSessionExpired()
    {
        // Arrange
        var id = "123";
        var cookie = "cookie";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
           .Protected()
           .Setup<Task<HttpResponseMessage>>(
              "SendAsync",
              ItExpr.IsAny<HttpRequestMessage>(),
              ItExpr.IsAny<CancellationToken>()
           )
           .ReturnsAsync(new HttpResponseMessage
           {
               StatusCode = HttpStatusCode.OK,
               Content = new StringContent("<html>...登入页面...</html>")
           });

        var client = new HttpClient(handlerMock.Object);
        _mockHttpClientFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(client);

        // Act & Assert
        await Assert.ThrowsAsync<XAUAT.EduApi.Exceptions.UnAuthenticationError>(() => _service.GetAllTrainProgram(cookie, id));
    }

    [Fact]
    public async Task GetAllTrainProgram_ShouldReturnTestFixtureData_WhenTestAccountMatched()
    {
        var resolver = new Mock<ITestAccountResolver>();
        resolver.Setup(x => x.IsTestAccount("cookie", "20239999")).Returns(true);

        var provider = new Mock<ITestDataProvider>();
        provider.Setup(x => x.GetProgramAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PlanCourse
                {
                    Name = "测试软件工程",
                    TermStr = "第四学期"
                }
            ]);

        var service = new ProgramService(
            _mockHttpClientFactory.Object,
            _mockCacheService.Object,
            null,
            resolver.Object,
            provider.Object);

        var result = await service.GetAllTrainProgram("cookie", "20239999");

        Assert.Single(result);
        Assert.Equal("测试软件工程", result[0].Name);
        _mockCacheService.Verify(x => x.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<List<PlanCourse>>>>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CacheLevel>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockHttpClientFactory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }
}
