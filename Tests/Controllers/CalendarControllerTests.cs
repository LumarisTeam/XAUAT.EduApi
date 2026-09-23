using System.Text;
using EduApi.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using XAUAT.EduApi.Controllers.V1;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;

namespace XAUAT.EduApi.Tests.Controllers;

/// <summary>
/// 日历订阅端点的契约：内容类型、附件头、以及状态码/错误形状的映射。
/// <para>
/// 沿用仓库既有的"直接 new 控制器调 action"风格（没有 <c>WebApplicationFactory</c>）。
/// </para>
/// </summary>
public class CalendarControllerTests
{
    private static readonly ILanguageResolver LanguageResolver = new HeaderLanguageResolver();
    private static readonly IApiMessageLocalizer MessageLocalizer = new ApiMessageLocalizer();

    private static CalendarController CreateController(Mock<ICalendarService> calendarService)
    {
        var controller = new CalendarController(
            calendarService.Object,
            Mock.Of<ILogger<CalendarController>>(),
            LanguageResolver,
            MessageLocalizer)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return controller;
    }

    private static Mock<ICalendarService> MockCalendar(byte[]? content = null, Exception? throws = null)
    {
        var mock = new Mock<ICalendarService>();
        var setup = mock.Setup(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()));

        if (throws is not null)
        {
            setup.ThrowsAsync(throws);
        }
        else
        {
            setup.ReturnsAsync(content ?? Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"));
        }

        return mock;
    }

    private static string? ErrorOf(IActionResult result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        return Assert.IsType<ErrorResponse>(objectResult.Value).error;
    }

    [Fact]
    public async Task GetCalendar_ShouldReturnTextCalendarWithAttachmentHeader()
    {
        var content = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nX-WR-CALNAME:课程表\r\nEND:VCALENDAR\r\n");
        var controller = CreateController(MockCalendar(content));

        var result = await controller.GetCalendar("2024001", "pw", null, null, "webcal", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/calendar; charset=utf-8", file.ContentType);
        Assert.Equal(content, file.FileContents);
        Assert.Equal("attachment; filename=calendar.ics", controller.Response.Headers.ContentDisposition.ToString());
        // URL 里明文带密码，不能被任何中间层缓存
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetCalendar_ShouldIgnoreTypeParameter()
    {
        // type 是历史参数（webcal/https），现在直接返回正文，两种取值都必须照常工作
        var controller = CreateController(MockCalendar());

        var withWebcal = await controller.GetCalendar("2024001", "pw", null, null, "webcal", CancellationToken.None);
        var withHttps = await controller.GetCalendar("2024001", "pw", null, null, "https", CancellationToken.None);
        var without = await controller.GetCalendar("2024001", "pw", null, null, null, CancellationToken.None);

        Assert.IsType<FileContentResult>(withWebcal);
        Assert.IsType<FileContentResult>(withHttps);
        Assert.IsType<FileContentResult>(without);
    }

    [Fact]
    public async Task GetCalendar_ShouldFallBackToPasswdAlias()
    {
        var calendarService = MockCalendar();
        var controller = CreateController(calendarService);

        await controller.GetCalendar("2024001", null, "pw-from-passwd", null, null, CancellationToken.None);

        calendarService.Verify(x => x.GenerateAsync(
            "2024001", "pw-from-passwd", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCalendar_ShouldPreferPasswordOverPasswd()
    {
        var calendarService = MockCalendar();
        var controller = CreateController(calendarService);

        await controller.GetCalendar("2024001", "real-pw", "stale-pw", null, null, CancellationToken.None);

        calendarService.Verify(x => x.GenerateAsync(
            "2024001", "real-pw", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, "pw")]
    [InlineData("", "pw")]
    [InlineData("2024001", null)]
    [InlineData("2024001", "")]
    public async Task GetCalendar_ShouldReturn400_WhenCredentialsMissing(string? username, string? password)
    {
        var calendarService = MockCalendar();
        var controller = CreateController(calendarService);

        var result = await controller.GetCalendar(username, password, null, null, null, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        Assert.Equal("缺少用户名或密码", ErrorOf(result));
        calendarService.Verify(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCalendar_ShouldReturn401_WhenLoginFails()
    {
        var controller = CreateController(MockCalendar(throws: new LoginFailedException("用户名或密码错误")));

        var result = await controller.GetCalendar("2024001", "bad", null, null, null, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.InvalidUsernameOrPassword), ErrorOf(result));
    }

    [Fact]
    public async Task GetCalendar_ShouldReturn429WithRetryAfter_WhenAccountBanned()
    {
        var unbanAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var controller = CreateController(MockCalendar(
            throws: new AccountBannedException("账户已被暂时封禁", "连续触发登录限流", unbanAt)));

        var result = await controller.GetCalendar("2024001", "pw", null, null, null, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.AccountBanned), ErrorOf(result));
        // Retry-After 必须来自上游给的解封时刻（约 2 小时），不是本地限流窗口
        var retryAfter = int.Parse(controller.Response.Headers.RetryAfter.ToString());
        Assert.InRange(retryAfter, 3600, 7200);
    }

    [Fact]
    public async Task GetCalendar_ShouldReturn429_WhenRateLimited()
    {
        var controller = CreateController(MockCalendar(throws: new RateLimitException()));

        var result = await controller.GetCalendar("2024001", "pw", null, null, null, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.EduSystemRateLimited), ErrorOf(result));
        // 没有学生限流状态时兜底 60 秒
        Assert.Equal("60", controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task GetCalendar_ShouldReturn500_OnUnexpectedError()
    {
        var controller = CreateController(MockCalendar(throws: new InvalidOperationException("无法获取当前学期信息")));

        var result = await controller.GetCalendar("2024001", "pw", null, null, null, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        // 内部错误细节不外泄
        Assert.Equal(MessageLocalizer.Get("zh", ApiMessageKey.EduSystemAccessFailed), ErrorOf(result));
    }
}
