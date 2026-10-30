namespace XAUAT.EduApi.Extensions;

/// <summary>
/// 教务系统类型。取值来自请求头 <c>x-loginMode</c>。
/// </summary>
public enum EduSystemMode
{
    /// <summary>本科生教务系统（<c>swjw</c>）。缺省值。</summary>
    Undergraduate = 0,

    /// <summary>研究生教务系统（<c>gmis</c>，原 <c>src</c> 的 Python 实现）。</summary>
    Graduate = 1
}

/// <summary>
/// <c>x-loginMode</c> 通用头的解析。
/// <para>
/// 约定（与客户端对齐，勿擅自放宽）：
/// <list type="bullet">
/// <item>缺失或空 → 本科生系统；</item>
/// <item><c>0</c> → 本科生系统；</item>
/// <item><c>1</c> → 研究生系统；</item>
/// <item>其它任何值 → 视为请求参数错误（返回 400），而不是静默回落到本科生。</item>
/// </list>
/// 最后一条是刻意的：写错头却拿到"看起来正常、其实是另一个系统"的数据，
/// 比明确报错难排查得多。
/// </para>
/// <para>
/// 只有登录与课表接口真正读取这个头。校车、电费、饭卡等非教务功能对两个系统是通用的，
/// 不参与分支。
/// </para>
/// </summary>
public static class LoginModeExtensions
{
    /// <summary>通用头名称（ASP.NET Core 的请求头匹配不区分大小写）。</summary>
    public const string HeaderName = "x-loginMode";

    /// <summary>
    /// 解析 <c>x-loginMode</c>。返回 <c>false</c> 表示头值非法，调用方应回 400。
    /// </summary>
    public static bool TryGetEduSystemMode(this HttpRequest request, out EduSystemMode mode)
    {
        var value = request.Headers[HeaderName].ToString().Trim();

        switch (value)
        {
            case "":
            case "0":
                mode = EduSystemMode.Undergraduate;
                return true;
            case "1":
                mode = EduSystemMode.Graduate;
                return true;
            default:
                // 非法时也给一个确定值，避免调用方读到未初始化的枚举
                mode = EduSystemMode.Undergraduate;
                return false;
        }
    }
}
