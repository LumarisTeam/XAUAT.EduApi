using System.Collections.Concurrent;

namespace XAUAT.EduApi.Login.Unified;

/// <summary>
/// 严格传输 + 容错传输的回退壳：默认走 <see cref="SocketsHttpHandler"/>（白名单、内网闸门、
/// 无代理、无自动重定向/ cookie 全都保持不变）；只有当严格解析器判定"响应不符合 HTTP 规范"时，
/// 才把**同一个请求原样重放**到 <see cref="TolerantHttpMessageHandler"/>。
/// <para>
/// <b>为什么必须做成回退而不是直接替换</b>：严格通道是既有的、有测试保护的路径，
/// 容错通道是为了兼容学校里那台会发 <c>SELINUX=Server</c> 头的服务器才引入的。
/// 把容错通道扶正等于让所有目标都走一份手写的 HTTP 实现，收益为零、风险为正。
/// </para>
/// <para>
/// <b>触发条件</b>只用 <see cref="HttpRequestException.HttpRequestError"/>：
/// <see cref="HttpRequestError.InvalidResponse"/>（畸形头/状态行）与
/// <see cref="HttpRequestError.ResponseEnded"/>（正文半途断掉）。
/// 不匹配异常文案——文案会跟随本地化/运行时版本变化，而枚举不会。
/// 连接失败、TLS 失败、超时都**不**回退：那些是真正的网络问题，换条通道也一样失败。
/// </para>
/// <para>
/// <b>为什么要记住"坏主机"</b>：一次登录要跳 3～4 跳，其中会多次回到同一台坏主机。
/// 不复用这个判断，每次都要先让严格通道失败一次（几百毫秒 + 一条异常栈），一次登录白等 1 秒多。
/// 缓存只有主机名、有上限、且路由到容错通道本身是安全的（容错通道能正常处理规范响应），
/// 因此校方哪天修好了也不会坏，只是少走一条捷径。
/// </para>
/// </summary>
internal sealed class ProtocolFallbackHandler : DelegatingHandler
{
    /// <summary>坏主机缓存上限。目标域名来自调用方，虽然受白名单约束，也不能无界增长。</summary>
    private const int BrokenHostLimit = 256;

    // HttpMessageHandler.SendAsync 是 protected，这里用 HttpMessageInvoker 才能"直接调用另一个 handler"
    private readonly HttpMessageInvoker _tolerant;
    private readonly ILogger<ProtocolFallbackHandler> _logger;

    private readonly ConcurrentDictionary<string, byte> _brokenHosts = new(StringComparer.OrdinalIgnoreCase);

    public ProtocolFallbackHandler(
        HttpMessageHandler strict,
        HttpMessageHandler tolerant,
        ILogger<ProtocolFallbackHandler> logger) : base(strict)
    {
        _tolerant = new HttpMessageInvoker(tolerant, disposeHandler: true);
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri
                  ?? throw new HttpRequestException(HttpRequestError.InvalidResponse, "请求缺少绝对 URI");

        // 重放需要请求体，而 HttpContent 只能读一次：先缓冲（表单只有几百字节）。
        var hasContent = request.Content is not null;
        var body = hasContent
            ? await request.Content!.ReadAsByteArrayAsync(cancellationToken)
            : null;

        if (_brokenHosts.ContainsKey(uri.Host))
        {
            _logger.LogDebug("目标 {Host} 此前返回过不符合规范的响应，本次直接走容错传输", uri.Host);
            return await _tolerant.SendAsync(Clone(request, body, hasContent), cancellationToken);
        }

        try
        {
            return await base.SendAsync(Clone(request, body, hasContent), cancellationToken);
        }
        catch (HttpRequestException ex) when (IsProtocolViolation(ex))
        {
            if (_brokenHosts.Count < BrokenHostLimit)
            {
                _brokenHosts.TryAdd(uri.Host, 0);
            }

            _logger.LogWarning(ex,
                "目标 {Host} 的响应不符合 HTTP 规范（{Error}），本次改用容错传输重放该请求；" +
                "后续对该主机的请求将直接走容错通道",
                uri.Host, ex.HttpRequestError);

            return await _tolerant.SendAsync(Clone(request, body, hasContent), cancellationToken);
        }
    }

    /// <summary>
    /// 是否属于"上游协议不规范"而不是网络故障。见类注释：只认枚举，不认文案。
    /// </summary>
    internal static bool IsProtocolViolation(HttpRequestException exception)
        => exception.HttpRequestError is HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded;

    /// <summary>
    /// 复制一份请求用于重放。
    /// <para>
    /// 必须复制：请求体已被第一次尝试读过，且 <see cref="HttpRequestMessage"/> 发过之后不再能安全复用。
    /// 不复制 <c>Options</c>——登录链路不用它，复制反而要处理泛型键。
    /// </para>
    /// </summary>
    private static HttpRequestMessage Clone(HttpRequestMessage source, byte[]? body, bool hasContent)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
            Content = hasContent ? new ByteArrayContent(body ?? []) : null
        };

        if (hasContent && source.Content is not null)
        {
            foreach (var header in source.Content.Headers)
            {
                clone.Content!.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tolerant.Dispose();
        }

        // 严格传输挂在 DelegatingHandler._innerHandler 上，基类会负责释放
        base.Dispose(disposing);
    }
}
