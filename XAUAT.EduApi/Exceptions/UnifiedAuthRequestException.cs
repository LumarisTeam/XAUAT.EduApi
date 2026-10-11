namespace XAUAT.EduApi.Exceptions;

/// <summary>
/// 通用统一身份认证（<c>/v1/login/unified</c>）的请求本身不合法：URL 格式错、
/// 目标域名不在白名单、目标地址解析到内网、跳转链里压根没有认证页。
/// <para>
/// 这类问题出在调用方而不是上游，控制器据此回 400——混进 5xx 会让客户端
/// 反复重试一个永远不会成功的请求。
/// </para>
/// <para>
/// <see cref="MessageKey"/> 指向 <c>ApiMessageKey</c> 里的本地化文案（对外可见），
/// <see cref="Exception.Message"/> 是给日志看的具体原因（含 host 等细节，不直接回给客户端）。
/// </para>
/// </summary>
public class UnifiedAuthRequestException(string messageKey, string? detail = null)
    : Exception(detail ?? messageKey)
{
    /// <summary>本地化文案键，取值来自 <c>ApiMessageKey</c>。</summary>
    public string MessageKey { get; } = messageKey;
}
