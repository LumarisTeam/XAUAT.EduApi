using EduApi.Data.Models;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Login.Unified;

namespace XAUAT.EduApi.Tests.Login.UnifiedAuth;

/// <summary>
/// 编排层：前置校验与"结果 → 异常 → HTTP 状态码"的映射。
/// <para>
/// 这层不碰网络，但它决定了客户端看到 400 还是 401 还是 502——判错会让客户端
/// 要么拿着错密码无限重试，要么把配置错误当成上游故障去报警。
/// </para>
/// </summary>
public class UnifiedAuthServiceTests
{
    private const string TargetUrl = "https://swjw.xauat.edu.cn/student/index";
    private readonly FakeUnifiedSsoClient _client = new();

    private UnifiedAuthService CreateService(HostAllowList? allowList = null)
        => new(_client, allowList ?? HostAllowList.Create(null), NullLogger<UnifiedAuthService>.Instance);

    private Task<UnifiedAuthResponse> LoginAsync(string url = TargetUrl)
        => CreateService().LoginAsync(new UnifiedAuthRequest
        {
            Url = url,
            Username = "alice",
            Password = "my-password"
        });

    // ================================================================ 前置校验

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/student/index")]
    [InlineData("ftp://swjw.xauat.edu.cn/x")]
    [InlineData("file:///etc/passwd")]
    public async Task Login_ShouldRejectInvalidTarget(string url)
    {
        var exception = await Assert.ThrowsAsync<UnifiedAuthRequestException>(() => LoginAsync(url));

        Assert.Equal(ApiMessageKey.UnifiedAuthInvalidTarget, exception.MessageKey);
        Assert.Null(_client.LastCommand);
    }

    [Fact]
    public async Task Login_ShouldRejectNonEducationalHost_WithoutTouchingUpstream()
    {
        var exception = await Assert.ThrowsAsync<UnifiedAuthRequestException>(
            () => LoginAsync("https://example.com/index"));

        Assert.Equal(ApiMessageKey.UnifiedAuthHostNotAllowed, exception.MessageKey);
        Assert.Null(_client.LastCommand);
    }

    // ================================================================ 结果映射

    [Fact]
    public async Task Login_ShouldMapSuccessToResponse()
    {
        _client.Result = new UnifiedAuthOutcome(
            true, "登录成功", UnifiedAuthFailure.None,
            AuthServerUrl: "http://authserver.xauat.edu.cn/authserver/login?service=x",
            LandingUrl: "https://swjw.xauat.edu.cn/student/index",
            LandingCookie: "__pstsid__=abc",
            Cookies: [("swjw.xauat.edu.cn", "__pstsid__", "abc")]);

        var response = await LoginAsync();

        Assert.Equal("alice", response.Username);
        Assert.Equal("alice", response.StudentId);
        Assert.Equal(TargetUrl, response.Url);
        Assert.Equal("__pstsid__=abc", response.Cookie);
        Assert.Equal("https://swjw.xauat.edu.cn/student/index", response.LandingUrl);
        Assert.Equal("http://authserver.xauat.edu.cn/authserver/login?service=x", response.AuthServerUrl);
        Assert.Equal("swjw.xauat.edu.cn", Assert.Single(response.Cookies).Host);
    }

    [Fact]
    public async Task Login_ShouldPassCredentialThroughToClient()
    {
        _client.Result = UnifiedAuthOutcome.Fail(UnifiedAuthFailure.InvalidCredentials, "错了");

        await Assert.ThrowsAsync<LoginFailedException>(() => LoginAsync());

        Assert.Equal("alice", _client.LastCommand!.Username);
        Assert.Equal("my-password", _client.LastCommand.Password);
        Assert.Equal(TargetUrl, _client.LastCommand.EntryUrl);
    }

    [Fact]
    public async Task Login_ShouldMapInvalidCredentialsTo401()
    {
        _client.Result = UnifiedAuthOutcome.Fail(UnifiedAuthFailure.InvalidCredentials, "登录失败：用户名或密码错误");

        var exception = await Assert.ThrowsAsync<LoginFailedException>(() => LoginAsync());

        Assert.Contains("用户名或密码错误", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_ShouldMapNotCasProtectedTo400()
    {
        _client.Result = UnifiedAuthOutcome.Fail(UnifiedAuthFailure.NotCasProtected, "没找到登录页");

        var exception = await Assert.ThrowsAsync<UnifiedAuthRequestException>(() => LoginAsync());

        Assert.Equal(ApiMessageKey.UnifiedAuthNotCasProtected, exception.MessageKey);
    }

    [Fact]
    public async Task Login_ShouldMapBlockedTargetTo400()
    {
        _client.Result = UnifiedAuthOutcome.Fail(UnifiedAuthFailure.BlockedTarget, "解析到内网");

        var exception = await Assert.ThrowsAsync<UnifiedAuthRequestException>(() => LoginAsync());

        Assert.Equal(ApiMessageKey.UnifiedAuthBlockedTarget, exception.MessageKey);
    }

    [Fact]
    public async Task Login_ShouldMapUpstreamFailureTo502()
    {
        _client.Result = UnifiedAuthOutcome.Fail(UnifiedAuthFailure.Upstream, "连接超时");

        await Assert.ThrowsAsync<HttpRequestException>(() => LoginAsync());
    }

    private sealed class FakeUnifiedSsoClient : IUnifiedSsoClient
    {
        public UnifiedAuthOutcome Result { get; set; } =
            UnifiedAuthOutcome.Fail(UnifiedAuthFailure.Upstream, "未编排");

        public UnifiedAuthCommand? LastCommand { get; private set; }

        public Task<UnifiedAuthOutcome> LoginAsync(
            UnifiedAuthCommand command, CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(Result);
        }
    }
}
