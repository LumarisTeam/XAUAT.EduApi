using System.Text.Json.Serialization;

namespace XAUAT.EduApi.Interfaces;

/// <summary>
/// XAUAT.LoginApi 的运维/统计数据读取：活跃用户数、封禁日志、解封。
/// <para>
/// 与 <see cref="IPaymentService"/> 同源——这些数据只存在于独立的登录服务里，EduApi
/// 不再自己实现，唯一实现是 <see cref="Services.HttpLoginAdminService"/>。本接口保留
/// 是为了让控制器与代理实现解耦，便于测试中 mock。
/// </para>
/// <para>
/// <b>为什么不让 lumaris_admin 直接访问 LoginApi</b>：管理端部署在别的主机上，而 LoginApi 的
/// compose 刻意不映射宿主机端口（只在 <c>xauat-net</c> 上暴露 <c>xauat-loginapi:8080</c>，
/// 见其 deploy/docker-compose.production.yml），EduApi 是唯一稳定可达的出口。
/// </para>
/// </summary>
public interface ILoginAdminService
{
    /// <summary>
    /// 活跃用户数。口径是当前存活的 <c>sso-cookies-*</c> 键数量，<b>不按人去重</b>——
    /// 同一个学生换一次密码就会多算一个。这是 Flask 的既有定义，沿用以免口径变化。
    /// </summary>
    Task<int> GetUserCountAsync();

    /// <summary>封禁日志（最新在前），可按学校/用户名过滤。上游固定只返回最近 200 条。</summary>
    Task<BanLogPage> GetBanLogsAsync(string? school, string? username);

    /// <summary>
    /// 按用户名解封。
    /// <para>
    /// 「用户当前未被封禁」在上游是 <b>400 而不是 5xx</b>，属于业务结果而非内部错误，
    /// 因此这里返回 <see cref="UnbanResult.Success"/> 为 false 的正常结果，不抛异常。
    /// </para>
    /// </summary>
    Task<UnbanResult> UnbanAsync(string school, string username);
}

/// <summary><c>GET /user_count</c> 的响应。形状与 Flask、XAUAT.LoginApi 都一致。</summary>
public sealed record LoginUserCount
{
    [JsonPropertyName("count")] public int Count { get; init; }
}

/// <summary>
/// <c>GET /security/ban_logs</c> 的响应。
/// <para>
/// 字段名是 <b>snake_case</b>，且必须用 <see cref="JsonPropertyNameAttribute"/> 逐个钉死：
/// <c>PropertyNameCaseInsensitive</c> 只兜大小写差异，<c>CreatedAt</c> 对不上 <c>created_at</c>，
/// 漏一个就会静默反序列化成默认值（<c>HttpLoginService</c> 的类注释记过这个坑）。
/// </para>
/// <para>
/// 这两个类型同时就是**回给管理端的响应体**（<c>ApiResponse&lt;T&gt;.Data</c>），
/// 所以 snake_case 也正好是 lumaris_admin 期望的形状，无需再包一层。
/// </para>
/// </summary>
public sealed record BanLogPage
{
    [JsonPropertyName("total")] public int Total { get; init; }

    [JsonPropertyName("items")] public List<BanLogItem> Items { get; init; } = [];
}

/// <summary>
/// 一条封禁日志。已被 LoginApi 归一化，抹平了 <c>auto_ban</c> / <c>manual_unban</c>
/// 两种存储形状的差异。
/// </summary>
public sealed record BanLogItem
{
    /// <summary><c>auto_ban</c> 或 <c>manual_unban</c>。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    /// <summary>Flask 页面里的中文标签：自动封禁 / 手动解封。</summary>
    [JsonPropertyName("type_label")] public string TypeLabel { get; init; } = "";

    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }

    /// <summary><c>yyyy-MM-dd HH:mm:ss</c>，上游按自己容器的本地时区格式化。</summary>
    [JsonPropertyName("created_at_human")] public string CreatedAtHuman { get; init; } = "";

    /// <summary>
    /// 解封时刻（unix 秒）。<b>可能整个键都不存在</b>（上游带 <c>WhenWritingNull</c>）：
    /// 条目里既没有 <c>data</c> 也没有 <c>previous</c> 快照时才会这样——正常的手动解封条目
    /// 取的是解封前的 <c>previous</c> 快照，那份快照里有 <c>unban_at</c>，所以照样会带上。
    /// 前端类型因此对应可选字段。
    /// </summary>
    [JsonPropertyName("unban_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UnbanAt { get; init; }

    /// <summary>同 <see cref="UnbanAt"/>，且在时刻为 0 时也被上游省略。</summary>
    [JsonPropertyName("unban_at_human")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnbanAtHuman { get; init; }

    [JsonPropertyName("reason")] public string Reason { get; init; } = "";

    /// <summary>
    /// <c>{school}:{username}</c>（新形状）或 <c>{school}:{username}:{hash}</c>（历史条目）。
    /// 解封时要按前两段还原学校与用户名，见 lumaris_admin 的封禁日志页。
    /// </summary>
    [JsonPropertyName("account")] public string Account { get; init; } = "";
}

/// <summary>解封结果。字段名走 Web 默认 camelCase，正好也是上游的形状。</summary>
public sealed record UnbanResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = "";
}
