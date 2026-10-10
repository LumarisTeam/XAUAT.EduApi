using System.Text.Json;
using EduApi.Data.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Login;
using XAUAT.EduApi.Login.Xauat;
using XAUAT.EduApi.Services;
using XAUAT.EduApi.Tests.Login.TestSupport;

namespace XAUAT.EduApi.Tests.Login.Services;

/// <summary>
/// <see cref="SsoLoginService"/> 的异常映射：<see cref="SsoAuthResult"/> → 控制器认得的异常类型。
/// <para>
/// 这一层是"登录逻辑搬进 EduApi"之后新增的接缝，也是原先 <c>HttpLoginService.Interpret</c>
/// 所承担的那份工作。它必须抛得和改造前**一模一样**，否则 <c>LoginController</c> 的 catch 链
/// 会给出错误的状态码——而那是客户端直接可见的契约。
/// </para>
/// </summary>
public class SsoLoginServiceTests
{
    private const string Username = "alice";
    private const string Password = "my-password";
    private const string StudentId = "2024001";

    private readonly FakeLoginRedisStore _store = new();
    private readonly FakeSsoClient _sso = new();
    private readonly Mock<ICookieCodeService> _cookieCode = new();
    private readonly Mock<ITestAccountResolver> _testAccounts = new();
    private readonly SsoLoginService _service;

    public SsoLoginServiceTests()
    {
        var banService = new BanService(_store, NullLogger<BanService>.Instance);
        var authService = new SsoAuthService(
            _sso, banService, _store, NullLogger<SsoAuthService>.Instance);

        _cookieCode.Setup(code => code.GetCode(It.IsAny<string>())).ReturnsAsync(StudentId);

        _service = new SsoLoginService(
            authService, _cookieCode.Object, _testAccounts.Object, NullLogger<SsoLoginService>.Instance);
    }

    private Task<LoginResponse> LoginAsync() => _service.LoginAsync(Username, Password);

    private string AccountKey => LoginCacheKeys.AccountKey("xauat", Username);

    private void SeedBan(long unbanAt)
    {
        var info = new BanInfo
        {
            Account = AccountKey, Reason = "连续触发登录限流", BannedAt = 1, UnbanAt = unbanAt
        };
        _store.SeedString(
            LoginCacheKeys.Ban(AccountKey),
            JsonSerializer.Serialize(info, RedisJson.Options),
            LoginCacheTtl.Ban);
    }

    // ================================================================ 成功路径

    [Fact]
    public async Task Login_ShouldReturnStudentIdResolvedFromCookies()
    {
        var response = await LoginAsync();

        Assert.True(response.Success);
        Assert.Equal(StudentId, response.StudentId);
        Assert.Equal(FakeSsoClient.DefaultEduCookie, response.Cookie);
    }

    [Fact]
    public async Task Login_ShouldPassCredentialsThroughToUpstream()
    {
        await LoginAsync();

        Assert.Equal((Username, Password), _sso.LastLoginCredentials);
    }

    [Fact]
    public async Task Login_ShouldUseEduCookieForStudentIdLookup_NotSsoTicket()
    {
        // 学号只能拿**教务会话**去反解；把 CASTGC 递过去教务系统不认，会静默拿到空串。
        string? seen = null;
        _cookieCode.Setup(code => code.GetCode(It.IsAny<string>()))
            .Callback<string>(cookies => seen = cookies)
            .ReturnsAsync(StudentId);

        await LoginAsync();

        Assert.Equal(FakeSsoClient.DefaultEduCookie, seen);
    }

    // ================================================================ 失败映射

    [Fact]
    public async Task Login_ShouldThrowLoginFailed_WithUpstreamMessage_WhenUpstreamRejects()
    {
        _sso.LoginResult = LoginToken.Failed("登录失败：用户名或密码错误");

        var ex = await Assert.ThrowsAsync<LoginFailedException>(LoginAsync);

        Assert.Contains("用户名或密码错误", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_ShouldThrowLoginFailed_WithoutTouchingCookieCode_WhenUpstreamFails()
    {
        _sso.LoginResult = LoginToken.Failed("登录失败：用户名或密码错误");

        await Assert.ThrowsAsync<LoginFailedException>(LoginAsync);

        _cookieCode.Verify(code => code.GetCode(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/student/login")]
    public async Task Login_ShouldThrowLoginFailed_WhenStudentIdCannotBeResolved(string studentId)
    {
        // "" 是教务系统不认 cookie 时的返回；"/student/login" 是 CookieCodeService 那条
        // 历史分支的产物。两者都等价于"这次登录其实没成功"。
        _cookieCode.Setup(code => code.GetCode(It.IsAny<string>())).ReturnsAsync(studentId);

        await Assert.ThrowsAsync<LoginFailedException>(LoginAsync);
    }

    [Fact]
    public async Task Login_ShouldThrowLoginFailed_WhenSuccessfulLoginYieldsNoCookie()
    {
        // 上游说成功却没给 cookie：不能把空 cookie 当成功返回给客户端。
        _sso.LoginResult = new LoginToken(true, "登录成功", "", "");
        _sso.ExchangeResult = new LoginToken(true, "登录成功", "", "");

        await Assert.ThrowsAsync<LoginFailedException>(LoginAsync);
    }

    // ================================================================ 封禁

    [Fact]
    public async Task Login_ShouldThrowAccountBanned_WithReasonAndUnbanTime()
    {
        var unbanAt = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
        SeedBan(unbanAt);

        var ex = await Assert.ThrowsAsync<AccountBannedException>(LoginAsync);

        Assert.Equal("连续触发登录限流", ex.Reason);
        Assert.Equal(unbanAt, ex.UnbanAt);
    }

    [Fact]
    public async Task Login_ShouldDeriveRetryAfterFromBanUntil_NotFromLocalWindow()
    {
        // 被封 24 小时的账号不能被告知"60 秒后再试"——Retry-After 必须来自解封时刻。
        var unbanAt = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
        SeedBan(unbanAt);

        var ex = await Assert.ThrowsAsync<AccountBannedException>(LoginAsync);

        Assert.NotNull(ex.RetryAfterSeconds);
        Assert.InRange(ex.RetryAfterSeconds!.Value, 86395, 86400);
    }

    [Fact]
    public async Task Login_ShouldNotTouchUpstream_WhenAccountIsBanned()
    {
        SeedBan(DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        await Assert.ThrowsAsync<AccountBannedException>(LoginAsync);

        Assert.Equal(0, _sso.LoginCallCount);
    }

    // ================================================================ 限流

    [Fact]
    public async Task Login_ShouldThrowLoginFailed_NotRateLimit_WhenFailuresExceedWindow()
    {
        // 契约刻意如此：LoginApi 当年对"失败次数过多"回的是 401 而不是 429，
        // HttpLoginService 把它转成 LoginFailedException，控制器回 401。
        // 迁到进程内之后必须保持——改成 429 会动到既有客户端的判定逻辑。
        _sso.LoginResult = LoginToken.Failed("登录失败：用户名或密码错误");

        for (var attempt = 1; attempt <= BanService.MaxAttempts; attempt++)
        {
            await Assert.ThrowsAsync<LoginFailedException>(LoginAsync);
        }

        // 第 6 次：限流命中，仍然只能是 LoginFailedException
        var ex = await Record.ExceptionAsync(LoginAsync);

        Assert.NotNull(ex);
        Assert.IsNotType<RateLimitException>(ex);
        Assert.IsType<LoginFailedException>(ex);
    }

    // ================================================================ 测试账号

    [Fact]
    public async Task Login_ShouldShortCircuitTestAccount_WithoutTouchingUpstreamOrCookieCode()
    {
        // 这是整条链路里最容易写坏的一处：测试账号的伪造 cookie 一旦落进
        // CookieCodeService.GetCode，就会带着假 cookie 去打真教务系统，
        // 于是本地联调与端到端测试全部变成"必须联网且必须失败"。
        _testAccounts.Setup(resolver => resolver.IsTestLogin(Username, Password)).Returns(true);
        _testAccounts.Setup(resolver => resolver.CreateLoginResponse())
            .Returns(new LoginResponse
            {
                Success = true,
                StudentId = "20239999",
                Cookie = "__pstsid__=marker; SESSION=marker;"
            });

        var response = await LoginAsync();

        Assert.True(response.Success);
        Assert.Equal("20239999", response.StudentId);
        Assert.Equal("__pstsid__=marker; SESSION=marker;", response.Cookie);

        Assert.Equal(0, _sso.LoginCallCount);
        _cookieCode.Verify(code => code.GetCode(It.IsAny<string>()), Times.Never);
    }
}
