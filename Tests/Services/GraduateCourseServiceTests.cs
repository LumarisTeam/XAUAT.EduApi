using System.Net;
using System.Text;
using EduApi.Data.Models;
using Microsoft.Extensions.Logging;
using Moq;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Login.Unified;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

public class GraduateCourseServiceTests
{
    private const string SampleHtml = """
        <script>
        var td = document.getElementById("td_1_41");
        td.innerHTML += "课程:高等数学<br>主讲教师:张三<br>教室:草堂校区-南山书院-南山书院303(第1-16周)";
        hb_cell();
        </script>
        """;

    private readonly Mock<IHttpClientFactory> _httpClientFactory = new();
    private readonly Mock<ILogger<GraduateCourseService>> _logger = new();
    private readonly Mock<ICacheService> _cache = new();

    public GraduateCourseServiceTests()
    {
        // 缓存工厂直接透传，测试关注抓取本身
        _cache.Setup(x => x.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<CourseActivity>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns(async (string _, Func<Task<List<CourseActivity>>> factory, TimeSpan? _, CacheLevel _,
                int _, CancellationToken _, bool _) => await factory());
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldFetchFromGmis_WithGraduateCacheKey()
    {
        string? capturedKey = null;
        _cache.Setup(x => x.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<List<CourseActivity>>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CacheLevel>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns(async (string key, Func<Task<List<CourseActivity>>> factory, TimeSpan? _, CacheLevel _,
                int _, CancellationToken _, bool _) =>
            {
                capturedKey = key;
                return await factory();
            });

        var stub = new StubHandler(_ => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(SampleHtml, Encoding.UTF8, "text/html")
        });
        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(stub, disposeHandler: false));

        var service = CreateService();
        var result = await service.GetCoursesAsync("20240001", "JSESSIONID=abc");

        Assert.Equal(CacheKeys.GraduateCourses("20240001"), capturedKey);
        // gmis 每条响应都带非法头名 SELINUX=Server，只有挂了 ProtocolFallbackHandler 的
        // UnifiedAuthClient 能读进来——换成默认客户端会静默变成"网络连接错误"
        _httpClientFactory.Verify(x => x.CreateClient(UnifiedSsoClient.ClientName), Times.Once);
        Assert.Equal("https://gmis.xauat.edu.cn/pyxx/pygl/xskbcx", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("JSESSIONID=abc", stub.LastRequest.Headers.GetValues("Cookie").Single());

        var course = Assert.Single(result);
        Assert.Equal("高等数学", course.CourseName);
        Assert.Equal(1, course.Weekday);
        Assert.Equal(1, course.StartUnit);
        Assert.Equal("南山书院303", course.Room);
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowUnAuthenticationError_WhenUpstreamRedirectsToLogin()
    {
        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)),
                disposeHandler: false));

        await Assert.ThrowsAsync<UnAuthenticationError>(() =>
            CreateService().GetCoursesAsync("20240001", "JSESSIONID=expired"));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowUnAuthenticationError_WhenUpstreamReturns401()
    {
        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
                disposeHandler: false));

        await Assert.ThrowsAsync<UnAuthenticationError>(() =>
            CreateService().GetCoursesAsync("20240001", "JSESSIONID=expired"));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowHttpRequestException_WhenUpstreamFails()
    {
        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)),
                disposeHandler: false));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateService().GetCoursesAsync("20240001", "JSESSIONID=abc"));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldThrowArgumentNullException_WhenCookieIsEmpty()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            CreateService().GetCoursesAsync("20240001", ""));
    }

    [Fact]
    public async Task GetCoursesAsync_ShouldReturnFixture_WhenTestAccountMatched()
    {
        var resolver = new Mock<ITestAccountResolver>();
        resolver.Setup(x => x.IsTestAccount("test-cookie", "20239999")).Returns(true);

        var provider = new Mock<ITestDataProvider>();
        provider.Setup(x => x.GetCoursesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CourseActivity { CourseName = "测试课程" }]);

        var result = await CreateService(resolver.Object, provider.Object)
            .GetCoursesAsync("20239999", "test-cookie");

        Assert.Equal("测试课程", Assert.Single(result).CourseName);
        _httpClientFactory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }

    private GraduateCourseService CreateService(
        ITestAccountResolver? resolver = null, ITestDataProvider? provider = null)
        => new(_httpClientFactory.Object, _logger.Object, _cache.Object, resolver, provider);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(handler(request));
        }
    }
}
