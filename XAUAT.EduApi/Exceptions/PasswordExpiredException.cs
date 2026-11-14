namespace XAUAT.EduApi.Exceptions;

/// <summary>
/// 统一身份认证密码已过期，必须先改密。控制器捕获后返回 HTTP 428 + ApiCodes.PasswordExpired。
/// </summary>
public class PasswordExpiredException(string? passwordChangeUrl = null)
    : Exception("当前密码已过期，请先修改统一身份认证密码")
{
    public string? PasswordChangeUrl { get; } = passwordChangeUrl;
}
