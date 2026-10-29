using EduApi.Data.Models;
using Microsoft.Extensions.Logging;
using Moq;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

public class GraduateLoginServiceTests
{
    private readonly Mock<IUnifiedAuthService> _unifiedAuth = new();
    private readonly Mock<ITestAccountResolver> _testAccount = new();
    private readonly Mock<ILogger<GraduateLoginService>> _logger = new();

    [Fact]
    public async Task LoginAsync_ShouldCallUnifiedAuth_WithFixedGraduateUrl()
    {
        _unifiedAuth.Setup(x => x.LoginAsync(
                It.Is<UnifiedAuthRequest>(request =>
                    request.Url == GraduateLoginService.LoginUrl &&
                    request.Username == "20240001" &&
                    request.Password == "pwd"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnifiedAuthResponse { Cookie = "JSESSIONID=abc" });

        var result = await CreateService().LoginAsync("20240001", "pwd");

        Assert.True(result.Success);
        // 研究生系统不从 cookie 反解学号，用户名即学号
        Assert.Equal("20240001", result.StudentId);
        Assert.Equal("JSESSIONID=abc", result.Cookie);
    }

    [Fact]
    public async Task LoginAsync_ShouldRethrowLoginFailedException_WhenCredentialsAreWrong()
    {
        _unifiedAuth.Setup(x => x.LoginAsync(It.IsAny<UnifiedAuthRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LoginFailedException());

        await Assert.ThrowsAsync<LoginFailedException>(() =>
            CreateService().LoginAsync("20240001", "wrong"));
    }

    [Fact]
    public async Task LoginAsync_ShouldThrowHttpRequestException_WhenFixedTargetIsRejected()
    {
        // 目标地址是写死的；被通用认证层拒绝（白名单/内网拦截/不再是 CAS 页面）
        // 对调用方而言是上游故障，而不是 400 参数错误
        _unifiedAuth.Setup(x => x.LoginAsync(It.IsAny<UnifiedAuthRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnifiedAuthRequestException("UnifiedAuthNotCasProtected", "detail"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateService().LoginAsync("20240001", "pwd"));
    }

    [Fact]
    public async Task LoginAsync_ShouldThrowLoginFailed_WhenNoCookieReturned()
    {
        _unifiedAuth.Setup(x => x.LoginAsync(It.IsAny<UnifiedAuthRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnifiedAuthResponse { Cookie = "" });

        await Assert.ThrowsAsync<LoginFailedException>(() =>
            CreateService().LoginAsync("20240001", "pwd"));
    }

    [Fact]
    public async Task LoginAsync_ShouldShortCircuitTestAccount_WithoutCallingUpstream()
    {
        _testAccount.Setup(x => x.IsTestLogin("frontend-test", "frontend-test-password")).Returns(true);
        _testAccount.Setup(x => x.CreateLoginResponse()).Returns(new LoginResponse
        {
            Success = true,
            StudentId = "20239999",
            Cookie = "test-marker"
        });

        var result = await CreateService().LoginAsync("frontend-test", "frontend-test-password");

        Assert.Equal("20239999", result.StudentId);
        _unifiedAuth.Verify(x => x.LoginAsync(
            It.IsAny<UnifiedAuthRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private GraduateLoginService CreateService()
        => new(_unifiedAuth.Object, _testAccount.Object, _logger.Object);
}
