using System.Text.Json.Serialization;

namespace XAUAT.EduApi.Interfaces;

/// <summary>
/// 登录子系统的运维/统计数据读取：活跃用户数、封禁日志、封禁状态、解封。
/// <para>
/// 数据源是那套与 Flask 共用的 Redis 键（<c>sso-cookies-*</c> / <c>security:ban:*</c> /
/// <c>security:ban_logs</c>）。这些数据原先在独立的 XAUAT.LoginApi 里，EduApi 经 HTTP 转发；
/// 登录逻辑搬进来之后唯一实现是 <see cref="Login.LocalLoginAdminService"/>，直接读本地 Redis。
/// 本接口保留是为了让控制器与实现解耦，便于测试中 mock。
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
    /// 查询某账号当前的封禁记录。
    /// <para>
    /// 这条能力原先由 LoginApi 的 <c>GET /security/ban_status</c> 承载。迁移时那条
    /// Flask 形状的路由刻意没有搬过来，因此在这里补一个 EduApi 自己形状的出口——
    /// 否则「查某账号是否被封」会随 LoginApi 一起消失，而封禁日志只告诉管理员
    /// 「发生过什么」，不告诉他「现在是否仍然生效」。
    /// </para>
    /// <para>
    /// 同时覆盖新旧两种键形状（见 <c>BanService.FindActiveBansAsync</c>），
    /// 因此灰度期间 Redis 里并存的三段式旧键也能被查到。
    /// </para>
    /// </summary>
    Task<BanStatusResult> GetBanStatusAsync(string school, string username);

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
/// 漏一个就会静默反序列化成默认值（<c>HttpLoginService</c> 当年就踩过这个坑——
/// 它已随本次迁移删除，但教训适用于所有跨 snake_case 边界的 DTO）。
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
/// 一条封禁日志。已被 <c>BanService</c> 归一化，抹平了 <c>auto_ban</c> / <c>manual_unban</c>
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

/// <summary>
/// <c>GET v1/login-ops/ban-status</c> 的响应。
/// <para>
/// <see cref="Items"/> 的形状逐字沿用 LoginApi 的 <c>GET /security/ban_status</c>
/// （snake_case，含运行时注入的 <c>ttl</c> 与 <c>key</c>），这样接管那条能力的调用方
/// 不需要改字段名。
/// </para>
/// </summary>
public sealed record BanStatusResult
{
    /// <summary>是否存在至少一条仍然生效的封禁记录。</summary>
    public bool Banned { get; init; }

    public List<BanItem> Items { get; init; } = [];
}

/// <summary>
/// 单条封禁记录。键名与 Flask <c>get_ban_status</c> / <c>list_bans_for_user</c>
/// 返回的 dict 完全一致，因此用 <see cref="JsonPropertyNameAttribute"/> 逐个钉死
/// snake_case——<c>PropertyNameCaseInsensitive</c> 只兜大小写差异，兜不住下划线。
/// </summary>
public sealed record BanItem
{
    [JsonPropertyName("account")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Account { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("banned_at")] public long BannedAt { get; init; }

    [JsonPropertyName("unban_at")] public long UnbanAt { get; init; }

    /// <summary>剩余 TTL 秒数（Redis TTL 原样透出，-1 表示永不过期，-2 表示键不存在）。</summary>
    [JsonPropertyName("ttl")] public long Ttl { get; init; }

    /// <summary>完整 Redis 键名，仅 <c>list_bans_for_user</c> 路径带。</summary>
    [JsonPropertyName("key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; init; }

    /// <summary>
    /// Redis 里的值不是合法 JSON 时的兜底：Flask 会把它原样塞进 <c>raw</c> 键并照常返回
    /// （见 <c>redis_client.get_ban_status</c> 的 <c>except: data = {"raw": raw}</c>）。
    /// </summary>
    [JsonPropertyName("raw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Raw { get; init; }
}
