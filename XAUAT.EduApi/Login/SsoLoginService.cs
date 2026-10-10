using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Login;

/// <summary>
/// <see cref="ILoginService"/> 的进程内实现：自己跑完整的 CAS/UAAP 登录握手。
/// <para>
/// 取代原先的 <c>HttpLoginService</c>（它把 <c>POST auth/login</c> 转给独立的 XAUAT.LoginApi）。
/// 上游契约因此从"HTTP 状态码 + JSON 信封"变成了 <see cref="SsoAuthResult"/>，
/// 但<b>抛出的异常类型一字未变</b>，<c>LoginController</c> 的 catch 链因此一行都不用改：
/// </para>
/// <list type="bullet">
///   <item>账号被封 → <see cref="AccountBannedException"/>，控制器回 429，并据 <c>ban_until</c>
///     设出 Retry-After。</item>
///   <item>用户名密码错、限流命中、拿不到 cookie —— 一律 <see cref="LoginFailedException"/>，
///     控制器回 401。限流**刻意也走这一条**：LoginApi 当年就是回 401 的，改成 429 会动到
///     既有客户端的判定逻辑。</item>
/// </list>
/// <para>
/// <b>名字里的 Sso 是必需的</b>：本仓库历史上还有一个同名的 <c>SSOLoginService</c>
/// （已随这次迁移删除），那个是打 Flask 的 HTTP 实现，与本类无关。
/// </para>
/// </summary>
internal sealed class SsoLoginService(
    SsoAuthService authService,
    ICookieCodeService cookieCode,
    ITestAccountResolver testAccountResolver,
    ILogger<SsoLoginService> logger) : ILoginService
{
    /// <summary>唯一支持的学校。与 <see cref="SsoAuthService"/> 里的同名常量一致。</summary>
    private const string School = "xauat";

    /// <remarks>
    /// <paramref name="language"/> 未被使用：CAS 与教务系统都不接受语言参数，
    /// 面向用户的文案一律由控制器按异常类型本地化生成。保留该参数是为了不改动控制器签名。
    /// </remarks>
    public async Task<LoginResponse> LoginAsync(string username, string password, string language = "zh")
    {
        // 测试账号：不碰上游、不碰 Redis、不计限流。
        // 短路留在这一层（而不是 SsoAuthService 里）有两个原因：一是与改造前的位置完全一致，
        // 二是伪造的 cookie 必须在这里就返回，不能落进下面的 GetCode 去打真教务系统。
        if (testAccountResolver.IsTestLogin(username, password))
        {
            logger.LogInformation("用户 {Username} 命中测试账号登录", username);
            return testAccountResolver.CreateLoginResponse();
        }

        var result = await authService.LoginAsync(School, username, password, CancellationToken.None);

        if (result.Banned)
        {
            logger.LogWarning("用户 {Username} 已被封禁，理由 {Reason}，解封时刻 {UnbanAt}",
                username, result.BanReason, result.BanUntil);
            throw new AccountBannedException(result.Message, result.BanReason, result.BanUntil);
        }

        if (!result.Success || string.IsNullOrEmpty(result.Cookies))
        {
            logger.LogWarning("用户 {Username} 登录失败：{Message}", username, result.Message);
            throw string.IsNullOrEmpty(result.Message)
                ? new LoginFailedException()
                : new LoginFailedException(result.Message);
        }

        // 学号不在登录链路的产物里（Flask 当年也不返回），仍然由 EduApi 拿 cookie 去教务系统反解。
        var studentId = await cookieCode.GetCode(result.Cookies);
        if (string.IsNullOrEmpty(studentId) || studentId == "/student/login")
        {
            logger.LogWarning("用户 {Username} 登录失败，用户 Id {StudentId}", username, studentId);
            throw new LoginFailedException();
        }

        logger.LogInformation("用户 {Username} 登录成功", username);
        return new LoginResponse
        {
            Success = true,
            StudentId = studentId,
            Cookie = result.Cookies
        };
    }
}
