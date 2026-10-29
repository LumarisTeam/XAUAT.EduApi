using EduApi.Data.Models;

namespace XAUAT.EduApi.Interfaces;

/// <summary>
/// 研究生教务系统（<c>gmis.xauat.edu.cn</c>）登录。
/// <para>
/// 由请求头 <c>x-loginMode: 1</c> 触发，与 <see cref="ILoginService"/>（本科生）并列。
/// 两者共享统一身份认证的底层实现，但返回的 cookie 归属不同站点，不能混用。
/// </para>
/// </summary>
public interface IGraduateLoginService
{
    /// <exception cref="Exceptions.LoginFailedException">凭据错误或上游拒绝 → 401。</exception>
    /// <exception cref="HttpRequestException">认证服务器或研究生系统不可达 → 502。</exception>
    Task<LoginResponse> LoginAsync(
        string username, string password, CancellationToken cancellationToken = default);
}
