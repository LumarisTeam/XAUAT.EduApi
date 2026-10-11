namespace EduApi.Data.Models;

/// <summary>
/// 通用统一身份认证（CAS）登录请求。
/// <para>
/// 只有三个字段，这是刻意的：调用方不需要知道认证服务器在哪、<c>service</c> 参数是什么、
/// 登录页有哪些隐藏域——这些全部由 <paramref name="Url"/> 出发的跳转链自动发现。
/// </para>
/// </summary>
public class UnifiedAuthRequest
{
    /// <summary>
    /// <b>受统一身份认证保护的目标地址</b>（不是认证服务器地址）。
    /// <para>
    /// 例如 <c>https://swjw.xauat.edu.cn/student/index</c>。服务端先请求它，
    /// 顺着 302 找到 CAS 登录页与 <c>service</c> 参数，登录后再顺原路跳回，
    /// 把沿途种下的 cookie 交回给调用方。
    /// </para>
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>学号／工号（统一认证的用户名）。</summary>
    public string Username { get; set; } = "";

    /// <summary>统一认证密码。仅在内存与发往认证服务器的表单里出现，不落日志、不落缓存。</summary>
    public string Password { get; set; } = "";
}
