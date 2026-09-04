using System.Text.Json;
using EduApi.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Linq;
using XAUAT.EduApi.Controllers;
using XAUAT.EduApi.Configuration;
using XAUAT.EduApi.Controllers.Old;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Controllers;

public class ControllerResponseModelTests
{
    private static readonly ILanguageResolver LanguageResolver = new HeaderLanguageResolver();
    private static readonly IApiMessageLocalizer MessageLocalizer = new ApiMessageLocalizer();

    [Fact]
    public async Task LoginController_ShouldReturnTypedSuccessResponse()
    {
        var loginService = new Mock<ILoginService>();
        loginService.Setup(x => x.LoginAsync("20230001", "pwd", It.IsAny<string>()))
            .ReturnsAsync(new LoginResponse
            {
                Success = true,
                StudentId = "20230001",
                Cookie = "foo=bar"
            });

        var controller = new LoginController(
            loginService.Object,
            Mock.Of<ILogger<LoginController>>(),
            LanguageResolver,
            MessageLocalizer);

        var result = await controller.Login(new LoginRequest
        {
            Username = "20230001",
            Password = "pwd"
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<LoginResponse>(ok.Value);
        Assert.True(payload.Success);
        Assert.Equal("20230001", payload.StudentId);
        Assert.Equal("foo=bar", payload.Cookie);
    }

    [Fact]
    public async Task CourseController_ShouldPreserveSuccessJsonShape()
    {
        var courseService = new Mock<ICourseService>();
        courseService.Setup(x => x.GetCoursesAsync("20230001", "test-cookie", It.IsAny<string>()))
            .ReturnsAsync([
                new CourseActivity
                {
                    CourseName = "高等数学",
                    CourseCode = "MATH001"
                }
            ]);

        var controller = new CourseController(
            Mock.Of<ILogger<CourseController>>(),
            courseService.Object,
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie")
        };

        var result = await controller.GetCourse("20230001");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<CourseResultResponse>(ok.Value);
        Assert.True(payload.Success);
        Assert.Single(payload.Data);

        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("\"Success\":true", json);
        Assert.Contains("\"Data\":[", json);
        Assert.Contains("\"ExpirationTime\":", json);
    }

    [Fact]
    public async Task CourseController_ShouldReturnTypedUnauthorizedError()
    {
        var courseService = new Mock<ICourseService>();
        courseService.Setup(x => x.GetCoursesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new UnAuthenticationError());

        var controller = new CourseController(
            Mock.Of<ILogger<CourseController>>(),
            courseService.Object,
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie")
        };

        var result = await controller.GetCourse("20230001");

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var payload = Assert.IsType<CourseErrorResponse>(unauthorized.Value);
        Assert.False(payload.Success);
        Assert.Equal("认证失败，请重新登录", payload.Message);
    }

    [Fact]
    public async Task CourseController_ShouldReturnRateLimitResponse_WhenServiceThrowsRateLimitException()
    {
        var courseService = new Mock<ICourseService>();
        courseService.Setup(x => x.GetCoursesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new RateLimitException());

        var rateLimitState = new StudentRateLimitState();
        var rateLimitKey = HttpContextStudentExtensions.CreateRateLimitStateKeys(["20230001"]).Single();
        rateLimitState.MarkRateLimited(rateLimitKey);

        var services = new ServiceCollection()
            .AddSingleton<IStudentRateLimitState>(rateLimitState)
            .BuildServiceProvider();

        var context = BuildControllerContext("test-cookie");
        context.HttpContext.Request.Path = "/Course";
        context.HttpContext.RequestServices = services;
        context.HttpContext.SetResolvedStudentIds(["20230001"]);
        context.HttpContext.SetResolvedRateLimitKeys([rateLimitKey]);

        var controller = new CourseController(
            Mock.Of<ILogger<CourseController>>(),
            courseService.Object,
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = context
        };

        var result = await controller.GetCourse("20230001");

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
        var payload = Assert.IsType<RateLimitErrorResponse>(objectResult.Value);
        Assert.Equal("rate_limited", payload.error);
        Assert.Equal("教务系统当前限流，请稍后重试", payload.message);
        Assert.True(payload.retryAfterSeconds >= 1);
        Assert.True(context.HttpContext.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task ExamController_ShouldReturnRateLimitResponse_WhenServiceThrowsRateLimitException()
    {
        var examService = new Mock<IExamService>();
        examService.Setup(x => x.GetExamArrangementsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>()))
            .ThrowsAsync(new RateLimitException());

        var rateLimitState = new StudentRateLimitState();
        var rateLimitKey = HttpContextStudentExtensions.CreateRateLimitStateKeys(["20230001"]).Single();
        rateLimitState.MarkRateLimited(rateLimitKey);

        var services = new ServiceCollection()
            .AddSingleton<IStudentRateLimitState>(rateLimitState)
            .BuildServiceProvider();

        var context = BuildControllerContext("test-cookie");
        context.HttpContext.Request.Path = "/Exam";
        context.HttpContext.RequestServices = services;
        context.HttpContext.SetResolvedStudentIds(["20230001"]);
        context.HttpContext.SetResolvedRateLimitKeys([rateLimitKey]);

        var controller = new ExamController(
            examService.Object,
            Mock.Of<ILogger<ExamController>>(),
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = context
        };

        var result = await controller.GetExamArrangements("20230001");

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
        var payload = Assert.IsType<RateLimitErrorResponse>(objectResult.Value);
        Assert.Equal("rate_limited", payload.error);
        Assert.Equal("教务系统当前限流，请稍后重试", payload.message);
        Assert.True(payload.retryAfterSeconds >= 1);
        Assert.True(context.HttpContext.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task CourseController_ShouldReturnEnglishUnauthorizedError_WhenLanguageHeaderIsEnglish()
    {
        var courseService = new Mock<ICourseService>();
        courseService.Setup(x => x.GetCoursesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new UnAuthenticationError());

        var controller = new CourseController(
            Mock.Of<ILogger<CourseController>>(),
            courseService.Object,
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie", language: "en")
        };

        var result = await controller.GetCourse("20230001");

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var payload = Assert.IsType<CourseErrorResponse>(unauthorized.Value);
        Assert.False(payload.Success);
        Assert.Equal("Authentication failed. Please sign in again.", payload.Message);
    }

    [Fact]
    public async Task CourseController_ShouldFallbackToChinese_WhenLanguageHeaderIsUnsupported()
    {
        var courseService = new Mock<ICourseService>();
        courseService.Setup(x => x.GetCoursesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new UnAuthenticationError());

        var controller = new CourseController(
            Mock.Of<ILogger<CourseController>>(),
            courseService.Object,
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie", language: "it")
        };

        var result = await controller.GetCourse("20230001");

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var payload = Assert.IsType<CourseErrorResponse>(unauthorized.Value);
        Assert.Equal("认证失败，请重新登录", payload.Message);
    }

    [Theory]
    [InlineData("fr", "Échec de l'authentification. Veuillez vous reconnecter.")]
    [InlineData("de", "Authentifizierung fehlgeschlagen. Bitte melden Sie sich erneut an.")]
    [InlineData("ja", "認証に失敗しました。もう一度ログインしてください。")]
    [InlineData("ko", "인증에 실패했습니다. 다시 로그인해 주세요.")]
    [InlineData("ru", "Ошибка аутентификации. Пожалуйста, войдите снова.")]
    [InlineData("zh-TW", "認證失敗，請重新登入")]
    [InlineData("zh", "认证失败，请重新登录")]
    public async Task CourseController_ShouldReturnLocalizedUnauthorizedError_WhenLanguageHeaderIsSupported(
        string language,
        string expectedMessage)
    {
        var courseService = new Mock<ICourseService>();
        courseService.Setup(x => x.GetCoursesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new UnAuthenticationError());

        var controller = new CourseController(
            Mock.Of<ILogger<CourseController>>(),
            courseService.Object,
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie", language: language)
        };

        var result = await controller.GetCourse("20230001");

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var payload = Assert.IsType<CourseErrorResponse>(unauthorized.Value);
        Assert.False(payload.Success);
        Assert.Equal(expectedMessage, payload.Message);
    }

    [Fact]
    public async Task PaymentController_ShouldReturnTypedServiceUnavailableError()
    {
        var paymentService = new Mock<IPaymentService>();
        paymentService.Setup(x => x.Login("123456", It.IsAny<string>()))
            .ThrowsAsync(new PaymentServiceException("远端失败"));

        var controller = new PaymentController(
            paymentService.Object,
            Mock.Of<ILogger<PaymentController>>(),
            LanguageResolver,
            MessageLocalizer);

        var result = await controller.Login("123456");

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        var payload = Assert.IsType<ErrorWithMessageResponse>(objectResult.Value);
        Assert.Equal("服务暂时不可用", payload.error);
        Assert.Equal("远端失败", payload.message);
    }

    [Theory]
    [InlineData("fr", "Service temporairement indisponible.")]
    [InlineData("en", "Service temporarily unavailable.")]
    [InlineData("zh-TW", "服務暫時無法使用")]
    public async Task PaymentController_ShouldReturnLocalizedServiceUnavailableError(string language, string expectedError)
    {
        var paymentService = new Mock<IPaymentService>();
        paymentService.Setup(x => x.Login("123456", It.IsAny<string>()))
            .ThrowsAsync(new PaymentServiceException("远端失败"));

        var controller = new PaymentController(
            paymentService.Object,
            Mock.Of<ILogger<PaymentController>>(),
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie", language: language)
        };

        var result = await controller.Login("123456");

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        var payload = Assert.IsType<ErrorWithMessageResponse>(objectResult.Value);
        Assert.Equal(expectedError, payload.error);
        Assert.Equal("远端失败", payload.message);
    }

    [Fact]
    public async Task ScoreController_ShouldReturnTypedBadRequestError()
    {
        var scoreService = new Mock<IScoreService>();
        scoreService.Setup(x => x.GetScoresAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new ArgumentNullException("semester", "semester is required"));

        var controller = new ScoreController(
            Mock.Of<ILogger<ScoreController>>(),
            scoreService.Object,
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie")
        };

        var result = await controller.GetScore("20230001", "");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var payload = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Contains("semester is required", payload.error);
    }

    [Fact]
    public async Task ProgramController_ShouldReturnTypedProgramList()
    {
        var programService = new Mock<IProgramService>();
        programService.Setup(x => x.GetAllTrainProgram("test-cookie", "3241", It.IsAny<string>()))
            .ReturnsAsync([
                new PlanCourse
                {
                    Name = "线性代数",
                    TermStr = "1"
                }
            ]);

        var controller = new ProgramController(
            Mock.Of<ILogger<ProgramController>>(),
            programService.Object,
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie")
        };

        var result = await controller.GetAllTrainProgram("3241", null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<List<PlanCourse>>(ok.Value);
        Assert.Single(payload);
    }

    [Fact]
    public async Task BusController_ShouldReturnEnglishError_WhenServiceFails()
    {
        var busService = new Mock<IBusService>();
        busService.Setup(x => x.GetBusFromOldDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var controller = new BusController(
            busService.Object,
            Mock.Of<ILogger<BusController>>(),
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie", language: "en")
        };

        var result = await controller.GetBus("2024-12-01");

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        Assert.Equal("Failed to get bus schedule data.", objectResult.Value);
    }

    [Fact]
    public void InfoController_ShouldReturnTypedTimeModel()
    {
        var infoService = new Mock<IInfoService>();
        infoService.Setup(x => x.GetTime()).Returns(new TimeModel
        {
            StartTime = "2026-03-01",
            EndTime = "2026-07-18"
        });

        var controller = new InfoController(
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ILogger<CourseController>>(),
            infoService.Object,
            LanguageResolver,
            MessageLocalizer);

        var result = controller.GetTime();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TimeModel>(ok.Value);
        Assert.Equal("2026-03-01", payload.StartTime);
        Assert.Equal("2026-07-18", payload.EndTime);
    }

    [Fact]
    public async Task InfoController_ShouldReturnTestCompletionData_WhenTestAccountMatched()
    {
        var resolver = new Mock<ITestAccountResolver>();
        resolver.Setup(x => x.IsTestAccount("test-cookie", null, null)).Returns(true);

        var provider = new Mock<ITestDataProvider>();
        provider.Setup(x => x.GetCompletionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new StudyModule
                {
                    Type = "主修"
                }
            ]);

        var controller = new InfoController(
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ILogger<CourseController>>(),
            Mock.Of<IInfoService>(),
            LanguageResolver,
            MessageLocalizer,
            resolver.Object,
            provider.Object)
        {
            ControllerContext = BuildControllerContext("test-cookie")
        };

        var result = await controller.GetCompletion();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<List<StudyModule>>(ok.Value);
        Assert.Single(payload);
        Assert.Equal("主修", payload[0].Type);
    }

    [Fact]
    public async Task V1MapController_ShouldReturnUnauthorized_WhenImportWithoutToken()
    {
        var controller = new XAUAT.EduApi.Controllers.V1.MapController(
            Mock.Of<IMapService>(),
            Mock.Of<ILogger<XAUAT.EduApi.Controllers.V1.MapController>>(),
            CreateMapAdminTokenService("test-token"),
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie")
        };

        var result = await controller.ImportPoi(new MapPoiModel
        {
            Name = "图书馆",
            Category = "学习",
            Latitude = 34.1m,
            Longitude = 108.9m
        });

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<object>>(unauthorized.Value);
        Assert.Equal(StatusCodes.Status401Unauthorized, payload.Code);
        Assert.Equal("Map 管理操作需要有效的 Token", payload.Message);
    }

    [Fact]
    public async Task V1MapController_ShouldAllowImport_WhenTokenMatches()
    {
        var mapService = new Mock<IMapService>();
        var controller = new XAUAT.EduApi.Controllers.V1.MapController(
            mapService.Object,
            Mock.Of<ILogger<XAUAT.EduApi.Controllers.V1.MapController>>(),
            CreateMapAdminTokenService("test-token"),
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext("test-cookie", token: "test-token")
        };

        var result = await controller.ImportPoi(new MapPoiModel
        {
            Name = "图书馆",
            Category = "学习",
            Latitude = 34.1m,
            Longitude = 108.9m
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(XAUAT.EduApi.Controllers.V1.ApiCodes.Success, payload.Code);
        mapService.Verify(x => x.AddPoiAsync(It.IsAny<MapPoiModel>()), Times.Once);
    }

    private static ControllerContext BuildControllerContext(string cookie, string? language = null, string? token = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["xauat"] = cookie;
        if (!string.IsNullOrWhiteSpace(token))
        {
            httpContext.Request.Headers["Token"] = token;
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            httpContext.Request.Headers[RequestLanguage.HeaderName] = language;
        }

        return new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    private static IMapAdminTokenService CreateMapAdminTokenService(string token)
    {
        return new MapAdminTokenService(Options.Create(new MapAdminOptions
        {
            Token = token
        }));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
