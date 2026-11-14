using EduApi.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XAUAT.EduApi.Controllers.V1;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Login.Mfa;

namespace XAUAT.EduApi.Tests.Controllers;

public class MfaControllerTests
{
    private static readonly HeaderLanguageResolver LanguageResolver = new();
    private static readonly ApiMessageLocalizer MessageLocalizer = new();

    private static MfaController Create(Mock<IMfaLoginService> service)
        => new(service.Object, NullLogger<MfaController>.Instance, LanguageResolver, MessageLocalizer)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    [Fact]
    public async Task Complete_ReturnsTypedLoginResponse()
    {
        var service = new Mock<IMfaLoginService>();
        service.Setup(x => x.CompleteAsync(It.IsAny<MfaSubmitRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoginResponse { Success = true, StudentId = "20240001", Cookie = "c" });

        var result = await Create(service).Complete(new MfaSubmitRequest
        {
            MfaSessionId = "s",
            Code = "123456"
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value);
        var login = Assert.IsType<LoginResponse>(payload.Data);
        Assert.Equal("20240001", login.StudentId);
    }

    [Fact]
    public async Task Complete_SessionExpired_Returns410()
    {
        var service = new Mock<IMfaLoginService>();
        service.Setup(x => x.CompleteAsync(It.IsAny<MfaSubmitRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MfaSessionExpiredException());

        var result = await Create(service).Complete(new MfaSubmitRequest { MfaSessionId = "s" });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(410, obj.StatusCode);
    }

    [Fact]
    public async Task Status_Pending_Returns202WithRetryAfter()
    {
        var service = new Mock<IMfaLoginService>();
        service.Setup(x => x.PollQrAsync("s", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MfaStatusResponse { SessionId = "s", Status = "pending" });

        var controller = Create(service);
        var result = await controller.Status("s");

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(202, obj.StatusCode);
        Assert.Equal("1", controller.Response.Headers["Retry-After"].ToString());
    }

    [Fact]
    public async Task Status_Success_Returns200()
    {
        var service = new Mock<IMfaLoginService>();
        service.Setup(x => x.PollQrAsync("s", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MfaStatusResponse
            {
                SessionId = "s",
                Status = "success",
                Login = new LoginResponse { Success = true, StudentId = "20240001", Cookie = "c" }
            });

        var result = await Create(service).Status("s");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<MfaStatusResponse>>(ok.Value);
        Assert.Equal("success", payload.Data!.Status);
    }
}
