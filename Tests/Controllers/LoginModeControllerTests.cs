using EduApi.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using XAUAT.EduApi.Controllers.V1;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Controllers;

/// <summary>
/// V1 控制器按 <c>x-loginMode</c> 分流的契约：头决定调用哪个服务、用哪个 cookie 提取规则。
/// 这些用例直接 new 控制器调 action（沿用仓库既有风格）。
/// </summary>
public class LoginModeControllerTests
{
    private static readonly ILanguageResolver LanguageResolver = new HeaderLanguageResolver();
    private static readonly IApiMessageLocalizer MessageLocalizer = new ApiMessageLocalizer();

    [Fact]
    public async Task Login_GraduateMode_ShouldUseGraduateService()
    {
        var undergrad = new Mock<ILoginService>();
        var graduate = new Mock<IGraduateLoginService>();
        graduate.Setup(x => x.LoginAsync("20240001", "pwd", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoginResponse
            {
                Success = true,
                StudentId = "20240001",
                Cookie = "JSESSIONID=abc"
            });

        var controller = CreateLoginController(undergrad, graduate, "1");

        var result = await controller.Login(new LoginRequest { Username = "20240001", Password = "pwd" });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<LoginResponse>>(ok.Value);
        Assert.Equal("JSESSIONID=abc", payload.Data!.Cookie);
        undergrad.Verify(x => x.LoginAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Login_DefaultMode_ShouldUseUndergraduateService()
    {
        var undergrad = new Mock<ILoginService>();
        undergrad.Setup(x => x.LoginAsync("20230001", "pwd", It.IsAny<string>()))
            .ReturnsAsync(new LoginResponse { Success = true, StudentId = "20230001", Cookie = "undergrad" });
        var graduate = new Mock<IGraduateLoginService>();

        var result = await CreateLoginController(undergrad, graduate, headerValue: null)
            .Login(new LoginRequest { Username = "20230001", Password = "pwd" });

        Assert.IsType<OkObjectResult>(result.Result);
        graduate.Verify(x => x.LoginAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("graduate")]
    public async Task Login_InvalidMode_ShouldReturnBadRequest_WithoutCallingAnyService(string headerValue)
    {
        var undergrad = new Mock<ILoginService>();
        var graduate = new Mock<IGraduateLoginService>();

        var result = await CreateLoginController(undergrad, graduate, headerValue)
            .Login(new LoginRequest { Username = "20230001", Password = "pwd" });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<object>>(badRequest.Value);
        Assert.Equal(ApiCodes.ParamError, payload.Code);

        undergrad.Verify(x => x.LoginAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        graduate.Verify(x => x.LoginAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Course_GraduateMode_ShouldUseGraduateService_AndRawCookieHeader()
    {
        var undergrad = new Mock<ICourseService>();
        var graduate = new Mock<IGraduateCourseService>();
        graduate.Setup(x => x.GetCoursesAsync("20240001", "JSESSIONID=abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CourseActivity { CourseName = "研究生课程" }]);

        var controller = CreateCourseController(
            undergrad, graduate, "1", cookie: "JSESSIONID=abc", rawCookieHeader: true);

        var result = await controller.GetCourse("20240001");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<List<CourseActivity>>>(ok.Value);
        Assert.Equal("研究生课程", Assert.Single(payload.Data!).CourseName);
        undergrad.Verify(x => x.GetCoursesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Course_DefaultMode_ShouldUseUndergraduateService_WithXauatHeader()
    {
        var undergrad = new Mock<ICourseService>();
        undergrad.Setup(x => x.GetCoursesAsync("20230001", "undergrad-cookie", It.IsAny<string>()))
            .ReturnsAsync([new CourseActivity { CourseName = "本科课程" }]);
        var graduate = new Mock<IGraduateCourseService>();

        var controller = CreateCourseController(
            undergrad, graduate, headerValue: null, cookie: "undergrad-cookie");

        var result = await controller.GetCourse("20230001");

        Assert.IsType<OkObjectResult>(result.Result);
        graduate.Verify(x => x.GetCoursesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Course_InvalidMode_ShouldReturnBadRequest_WithoutCallingAnyService()
    {
        var undergrad = new Mock<ICourseService>();
        var graduate = new Mock<IGraduateCourseService>();

        var result = await CreateCourseController(undergrad, graduate, "9", cookie: "whatever")
            .GetCourse("20230001");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<object>>(badRequest.Value);
        Assert.Equal(ApiCodes.ParamError, payload.Code);

        undergrad.Verify(x => x.GetCoursesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        graduate.Verify(x => x.GetCoursesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static LoginController CreateLoginController(
        Mock<ILoginService> undergrad, Mock<IGraduateLoginService> graduate, string? headerValue)
        => new(undergrad.Object, graduate.Object, Mock.Of<ILogger<LoginController>>(),
            LanguageResolver, MessageLocalizer)
        {
            ControllerContext = BuildContext(headerValue)
        };

    private static CourseController CreateCourseController(
        Mock<ICourseService> undergrad, Mock<IGraduateCourseService> graduate,
        string? headerValue, string cookie, bool rawCookieHeader = false)
        => new(Mock.Of<ILogger<CourseController>>(), undergrad.Object, graduate.Object,
            Mock.Of<IScheduleTimeService>(), LanguageResolver, MessageLocalizer)
        {
            ControllerContext = BuildContext(headerValue, cookie, rawCookieHeader)
        };

    private static ControllerContext BuildContext(
        string? headerValue, string? cookie = null, bool rawCookieHeader = false)
    {
        var httpContext = new DefaultHttpContext();

        if (headerValue is not null)
        {
            httpContext.Request.Headers[LoginModeExtensions.HeaderName] = headerValue;
        }

        if (!string.IsNullOrEmpty(cookie))
        {
            // 本科生链路走 xauat 头（或带 __pstsid__ 的 Cookie）；研究生链路常见直接回传 Cookie 头
            if (rawCookieHeader)
            {
                httpContext.Request.Headers.Cookie = cookie;
            }
            else
            {
                httpContext.Request.Headers["xauat"] = cookie;
            }
        }

        return new ControllerContext { HttpContext = httpContext };
    }
}
