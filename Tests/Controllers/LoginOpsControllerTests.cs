using EduApi.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using XAUAT.EduApi.Configuration;
using XAUAT.EduApi.Controllers.V1;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Controllers;

/// <summary>
/// 登录运维端点的契约：管理员 Token 鉴权、状态码映射、以及错误文案的本地化。
/// <para>
/// 沿用仓库既有的"直接 new 控制器调 action"风格（没有 <c>WebApplicationFactory</c>）。
/// 封禁日志含学号等个人信息，因此鉴权那两条（无 Token / Token 正确）是本类最重要的一半。
/// </para>
/// </summary>
public class LoginOpsControllerTests
{
    private const string Token = "test-token";

    private static readonly ILanguageResolver LanguageResolver = new HeaderLanguageResolver();
    private static readonly IApiMessageLocalizer MessageLocalizer = new ApiMessageLocalizer();

    // ---------------------------------------------------------------- 鉴权

    [Fact]
    public async Task GetUserCount_ShouldReturnUnauthorized_WhenNoToken()
    {
        var controller = CreateController(Mock.Of<ILoginAdminService>());

        var result = await controller.GetUserCount();

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<object>>(unauthorized.Value);
        Assert.Equal(StatusCodes.Status401Unauthorized, payload.Code);
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.LoginOpsTokenRequired), payload.Message);
    }

    [Fact]
    public async Task GetUserCount_ShouldReturnUnauthorized_WhenTokenMismatch()
    {
        var controller = CreateController(Mock.Of<ILoginAdminService>(), token: "wrong-token");

        var result = await controller.GetUserCount();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetBanLogs_ShouldReturnUnauthorized_WhenNoToken()
    {
        var controller = CreateController(Mock.Of<ILoginAdminService>());

        var result = await controller.GetBanLogs();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Unban_ShouldReturnUnauthorized_WhenNoToken()
    {
        // 写操作尤其不能漏鉴权
        var loginAdminService = new Mock<ILoginAdminService>();
        var controller = CreateController(loginAdminService.Object);

        var result = await controller.Unban(new UnbanRequest { Username = "2024001" });

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        loginAdminService.Verify(x => x.UnbanAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetUserCount_ShouldNotReachService_WhenUnauthorized()
    {
        var loginAdminService = new Mock<ILoginAdminService>();
        var controller = CreateController(loginAdminService.Object);

        await controller.GetUserCount();

        loginAdminService.Verify(x => x.GetUserCountAsync(), Times.Never);
    }

    // ---------------------------------------------------------------- 正常路径

    [Fact]
    public async Task GetUserCount_ShouldReturnCount()
    {
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.GetUserCountAsync()).ReturnsAsync(42);
        var controller = CreateController(loginAdminService.Object, token: Token);

        var result = await controller.GetUserCount();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<LoginUserCount>>(ok.Value);
        Assert.Equal(ApiCodes.Success, payload.Code);
        Assert.Equal(42, payload.Data!.Count);
    }

    [Fact]
    public async Task GetBanLogs_ShouldPassFiltersThrough()
    {
        var page = new BanLogPage
        {
            Total = 1,
            Items = [new BanLogItem { Type = "auto_ban", Account = "xauat:2024001" }]
        };
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.GetBanLogsAsync("xauat", "2024001")).ReturnsAsync(page);
        var controller = CreateController(loginAdminService.Object, token: Token);

        var result = await controller.GetBanLogs("xauat", "2024001");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<BanLogPage>>(ok.Value);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Single(payload.Data.Items);
    }

    [Fact]
    public async Task GetBanLogs_ShouldForwardNullFilters_WhenNotProvided()
    {
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.GetBanLogsAsync(null, null))
            .ReturnsAsync(new BanLogPage());
        var controller = CreateController(loginAdminService.Object, token: Token);

        await controller.GetBanLogs();

        loginAdminService.Verify(x => x.GetBanLogsAsync(null, null), Times.Once);
    }

    // ---------------------------------------------------------------- 解封

    [Fact]
    public async Task Unban_ShouldReturnBadRequest_WhenUsernameMissing()
    {
        var loginAdminService = new Mock<ILoginAdminService>();
        var controller = CreateController(loginAdminService.Object, token: Token);

        var result = await controller.Unban(new UnbanRequest { Username = "  " });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<object>>(badRequest.Value);
        Assert.Equal(ApiCodes.ParamError, payload.Code);
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.LoginOpsUsernameRequired), payload.Message);
        loginAdminService.Verify(x => x.UnbanAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Unban_ShouldDefaultSchoolToXauat_WhenMissing()
    {
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.UnbanAsync("xauat", "2024001"))
            .ReturnsAsync(new UnbanResult { Success = true, Message = "用户已解封" });
        var controller = CreateController(loginAdminService.Object, token: Token);

        var result = await controller.Unban(new UnbanRequest { Username = "2024001" });

        Assert.IsType<OkObjectResult>(result.Result);
        loginAdminService.Verify(x => x.UnbanAsync("xauat", "2024001"), Times.Once);
    }

    [Fact]
    public async Task Unban_ShouldLowercaseSchool()
    {
        // 封禁键是小写的，上游也会转；这里不转就会查不到键
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.UnbanAsync("xauat", "2024001"))
            .ReturnsAsync(new UnbanResult { Success = true, Message = "ok" });
        var controller = CreateController(loginAdminService.Object, token: Token);

        await controller.Unban(new UnbanRequest { School = "XAUAT", Username = "2024001" });

        loginAdminService.Verify(x => x.UnbanAsync("xauat", "2024001"), Times.Once);
    }

    [Fact]
    public async Task Unban_ShouldReturnBadRequestWithUpstreamMessage_WhenNotBanned()
    {
        // 「当前未被封禁」是业务结果：回 400 + 上游原文，而不是 500
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.UnbanAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new UnbanResult { Success = false, Message = "用户当前未被封禁或解封失败" });
        var controller = CreateController(loginAdminService.Object, token: Token);

        var result = await controller.Unban(new UnbanRequest { Username = "2024001" });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<object>>(badRequest.Value);
        Assert.Equal("用户当前未被封禁或解封失败", payload.Message);
    }

    // ---------------------------------------------------------------- 异常映射

    [Fact]
    public async Task GetBanLogs_ShouldReturn503_WhenLoginApiNotConfigured()
    {
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.GetBanLogsAsync(It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new LoginOpsNotConfiguredException("未配置 LOGIN_API_BASE_URL"));
        var controller = CreateController(loginAdminService.Object, token: Token);

        var result = await controller.GetBanLogs();

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        var payload = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.Equal(ApiCodes.UpstreamError, payload.Code);
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.LoginOpsBaseUrlNotConfigured), payload.Message);
    }

    [Fact]
    public async Task GetUserCount_ShouldReturn503_WhenLoginServiceUnreachable()
    {
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.GetUserCountAsync())
            .ThrowsAsync(new LoginOpsUnavailableException("连不上"));
        var controller = CreateController(loginAdminService.Object, token: Token);

        var result = await controller.GetUserCount();

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        var payload = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.ServiceUnavailable), payload.Message);
    }

    [Fact]
    public async Task GetUserCount_ShouldReturn500_WhenUnexpectedError()
    {
        // 非领域异常（上游 5xx 等）必须回 500 —— 与 503 区分开，否则真 bug 会被当成"稍后重试"
        var loginAdminService = new Mock<ILoginAdminService>();
        loginAdminService.Setup(x => x.GetUserCountAsync())
            .ThrowsAsync(new InvalidOperationException("LoginApi 返回 500"));
        var controller = CreateController(loginAdminService.Object, token: Token);

        var result = await controller.GetUserCount();

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var payload = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.Equal(ApiCodes.InternalError, payload.Code);
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.InternalServerError), payload.Message);
    }

    // ---------------------------------------------------------------- 夹具

    private static LoginOpsController CreateController(
        ILoginAdminService loginAdminService, string? token = null)
        => new(
            loginAdminService,
            Mock.Of<ILogger<LoginOpsController>>(),
            new MapAdminTokenService(Options.Create(new MapAdminOptions { Token = Token })),
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = BuildControllerContext(token)
        };

    private static ControllerContext BuildControllerContext(string? token)
    {
        var httpContext = new DefaultHttpContext();
        if (token is not null)
        {
            httpContext.Request.Headers["Token"] = token;
        }

        return new ControllerContext { HttpContext = httpContext };
    }
}
