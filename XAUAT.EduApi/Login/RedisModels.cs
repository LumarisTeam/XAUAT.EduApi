using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XAUAT.EduApi.Login;

// ---------------------------------------------------------------------------------
// 登录子系统的 Redis 存储模型。
//
// 这些对象的键名是 **snake_case**，且必须与 Flask 写下的字节一致——EduApi 与 Flask
// 共用同一个 Redis 实例（见 LoginCacheKeys 的说明），灰度期间两边会读写同一批键。
// 序列化选项统一走 RedisJson.Options，不要在模型上手写 JsonPropertyName。
//
// 注：这里刻意**不用**源生成 JSON 上下文。EduApi 不是 Native AOT 项目（它跑 Serilog、
// HtmlAgilityPack、EF Core），运行时 JsonNamingPolicy.SnakeCaseLower 与源生成器的
// JsonKnownNamingPolicy.SnakeCaseLower 是同一套实现，产出逐字相同。
// ---------------------------------------------------------------------------------

/// <summary>
/// <c>security:ban:{school}:{username}</c> 的值。
/// 对应 Flask 的 <c>ban_data</c>：<c>{"account","reason","banned_at","unban_at"}</c>。
/// </summary>
internal sealed class BanInfo
{
    public string Account { get; set; } = "";
    public string Reason { get; set; } = "";
    public long BannedAt { get; set; }
    public long UnbanAt { get; set; }
}

/// <summary>
/// <c>security:ban_logs</c> 列表里的一条。
/// <para>
/// 这个键里同时存在两种形状（Flask 就写了两种，历史条目不能改）：
/// 自动封禁 <c>{"type":"auto_ban","created_at":N,"data":{...}}</c>
/// 手动解封 <c>{"type":"manual_unban","created_at":N,"account":S,"reason":S,"previous":{...}}</c>
/// 因此可选字段用 WhenWritingNull 忽略，保证新写入的条目形状与 Flask 逐字相同。
/// </para>
/// </summary>
internal sealed class BanLogEntry
{
    public string Type { get; set; } = "";
    public long CreatedAt { get; set; }

    /// <summary>仅 auto_ban 有。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BanInfo? Data { get; set; }

    /// <summary>仅 manual_unban 有。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Account { get; set; }

    /// <summary>仅 manual_unban 有。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>仅 manual_unban 有：解封前的封禁快照。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BanInfo? Previous { get; set; }
}

/// <summary>
/// Redis 值的序列化入口。
/// <para>
/// <b>两项设置都是存储契约，不是风格偏好</b>：
/// <list type="bullet">
/// <item><c>SnakeCaseLower</c> —— 键名形状，Flask 侧按 <c>banned_at</c> 取值，写错不会报错，
/// 只会让封禁状态读不出来（表现为封禁失效）。</item>
/// <item><c>UnsafeRelaxedJsonEscaping</c> —— 中文原样写成 UTF-8，对应 Flask 的
/// <c>json.dumps(..., ensure_ascii=False)</c>。默认编码器会转成 <c>\uXXXX</c>，
/// 字节形状就对不上了。</item>
/// </list>
/// </para>
/// </summary>
internal static class RedisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
