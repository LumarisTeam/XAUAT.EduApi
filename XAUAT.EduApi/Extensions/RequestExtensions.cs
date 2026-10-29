namespace XAUAT.EduApi.Extensions;

public static class RequestExtensions
{
    public static string GetEduAuthCookie(this HttpRequest request)
    {
        var cookie = request.Headers.Cookie.ToString();
        if (!string.IsNullOrEmpty(cookie) && !cookie.StartsWith("Rider") && cookie.Contains("__pstsid__"))
        {
            return cookie;
        }

        return request.Headers["xauat"].ToString();
    }

    /// <summary>
    /// 取研究生教务系统（<c>gmis.xauat.edu.cn</c>）的会话 cookie。
    /// <para>
    /// 与 <see cref="GetEduAuthCookie"/> 的区别：本科生链路能靠 <c>__pstsid__</c> 这个特征
    /// 判断"这是不是教务 cookie"，研究生系统的 cookie 名没有可依赖的特征（JSESSIONID 之类
    /// 到处都是），因此这里不能按内容过滤，只能：优先 <c>xauat</c> 头，否则接受原始
    /// <c>Cookie</c> 头——但仍然排掉以 <c>Rider</c> 开头的 IDE 测试流量。
    /// </para>
    /// </summary>
    public static string GetGraduateAuthCookie(this HttpRequest request)
    {
        var xauat = request.Headers["xauat"].ToString();
        if (!string.IsNullOrWhiteSpace(xauat))
        {
            return xauat;
        }

        var cookie = request.Headers.Cookie.ToString();
        return !string.IsNullOrWhiteSpace(cookie) && !cookie.StartsWith("Rider", StringComparison.Ordinal)
            ? cookie
            : "";
    }
}
