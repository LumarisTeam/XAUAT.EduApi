using EduApi.Data.Models;

namespace XAUAT.EduApi.Interfaces;

/// <summary>
/// 通用统一身份认证：调用方只给一个受 CAS 保护的目标地址 + 学号密码，
/// 由服务端自己顺着跳转链发现认证服务器、登录、再跳回来，把沿途的 cookie 交回去。
/// <para>
/// 与 <see cref="ILoginService"/> 的区别：那个是 XAUAT 专用（地址、service 参数、
/// 学号反解全部写死，还带缓存与封禁编排）；这个是通用的，只要目标系统是 CAS/金智 UAAP，
/// 换个 URL 就能用，因此**不接** <c>Login/</c> 的那套 Redis 缓存与封禁键
/// （那批键名（<c>login-cookies-xauat-…</c>）是给 XAUAT 专用链路用的兼容契约）。
/// </para>
/// </summary>
public interface IUnifiedAuthService
{
    /// <exception cref="Exceptions.UnifiedAuthRequestException">请求本身不合法（URL、白名单、非 CAS 页面、内网地址）→ 400。</exception>
    /// <exception cref="Exceptions.LoginFailedException">认证服务器拒绝凭据 → 401。</exception>
    /// <exception cref="HttpRequestException">目标站点或认证服务器不可达 → 502。</exception>
    Task<UnifiedAuthResponse> LoginAsync(UnifiedAuthRequest request, CancellationToken cancellationToken = default);
}
