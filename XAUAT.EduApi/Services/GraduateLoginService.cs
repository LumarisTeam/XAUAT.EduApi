using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 研究生教务系统登录：在 <see cref="IUnifiedAuthService"/> 之上把目标地址固定为
/// <c>gmis.xauat.edu.cn</c> 的 SSO 入口。
/// <para>
/// 与本科生 <c>SsoLoginService</c> 的差异是刻意的：
/// <list type="bullet">
/// <item><b>不走 Redis</b>。通用统一认证每次都会真的登录一次、cookie 会轮换，
/// 缓存那套 <c>login-cookies-*</c> 键名是 XAUAT 本科生链路的兼容契约，不能复用。</item>
/// <item><b>不做封禁/登录限流</b>。这两项目前只覆盖本科生链路；
/// 研究生登录失败直接按 401 返回。</item>
/// <item>学号直接取调用方传入的用户名——研究生系统的登录页不返回学号，
/// 而用户名本身就是学号。</item>
/// </list>
/// </para>
/// </summary>
public sealed class GraduateLoginService(
    IUnifiedAuthService unifiedAuthService,
    ITestAccountResolver testAccountResolver,
    ILogger<GraduateLoginService> logger) : IGraduateLoginService
{
    /// <summary>
    /// 研究生系统受 CAS 保护的入口（与 <c>src/get_data.py</c> 里写死的地址一致）。
    /// <b>不要换成 <c>/pyxx/pygl/xskbcx</c></b>：那是登录后要抓的课表页，
    /// 作为登录入口虽然也能跳到认证服务器，但落地页与 cookie 归属不同。
    /// </summary>
    public const string LoginUrl = "https://gmis.xauat.edu.cn/pyxx/sso/login";

    public async Task<LoginResponse> LoginAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        // 与本科生链路一致：测试账号不碰上游、不碰缓存。
        if (testAccountResolver.IsTestLogin(username, password))
        {
            logger.LogInformation("用户 {Username} 命中研究生测试账号登录", username);
            return testAccountResolver.CreateLoginResponse();
        }

        UnifiedAuthResponse result;
        try
        {
            result = await unifiedAuthService.LoginAsync(
                new UnifiedAuthRequest
                {
                    Url = LoginUrl,
                    Username = username,
                    Password = password
                },
                cancellationToken);
        }
        catch (UnifiedAuthRequestException ex)
        {
            // 目标地址是写死的；走到这里说明上游形态变了或本机连不上（白名单/内网拦截）。
            // 对调用方而言这是"上游不可用"，而不是他填错了参数。
            logger.LogWarning(ex, "研究生登录的目标地址被通用认证层拒绝");
            throw new HttpRequestException(ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(result.Cookie))
        {
            logger.LogWarning("用户 {Username} 研究生登录失败：未获取到 cookie", username);
            throw new LoginFailedException();
        }

        logger.LogInformation("用户 {Username} 研究生登录成功", username);
        return new LoginResponse
        {
            Success = true,
            StudentId = username,
            Cookie = result.Cookie
        };
    }
}
