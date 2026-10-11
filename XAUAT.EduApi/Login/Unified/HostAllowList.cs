namespace XAUAT.EduApi.Login.Unified;

/// <summary>
/// 通用统一身份认证接口的目标域名白名单。
/// <para>
/// <b>为什么必须有它</b>：<c>/v1/login/unified</c> 让调用方直接指定 URL，服务端去请求——
/// 这正是 SSRF 的教科书形态（<c>http://169.254.169.254/</c>、<c>http://127.0.0.1:8080/</c>、
/// <c>http://10.0.0.5/</c>）。域名白名单是第一道闸（默认只放 <c>*.edu.cn</c>），
/// 第二道是 <see cref="UnifiedConnectGuard"/> 在真正建连前丢弃内网/保留地址。
/// 两道闸针对的攻击面不同，缺一不可：白名单防"指向任意内部主机"，
/// 建连前校验防"合法域名被解析到内网"（DNS rebinding / 内网 DNS 污染）。
/// </para>
/// <para>
/// 匹配规则（大小写不敏感）：
/// <list type="bullet">
/// <item><c>*.edu.cn</c> —— 任意以 <c>.edu.cn</c> 结尾的域名（不含裸 <c>edu.cn</c>）。</item>
/// <item><c>swjw.xauat.edu.cn</c> —— 精确匹配。</item>
/// <item><c>*</c> —— 全部放开（配置里显式写了才会生效，且每次调用都会打警告日志）。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class HostAllowList
{
    /// <summary>全部放开的通配符。写进配置即等于关掉域名这道闸，只留建连前的内网校验。</summary>
    public const string AnyPattern = "*";

    /// <summary>内置默认：中国大陆高校域名。目标系统不在这个后缀下时，用环境变量追加。</summary>
    public static readonly string[] DefaultPatterns = ["*.edu.cn"];

    private readonly string[] _wildcardSuffixes;
    private readonly string[] _exactHosts;

    private HostAllowList(bool allowAny, string[] wildcardSuffixes, string[] exactHosts)
    {
        AllowAny = allowAny;
        _wildcardSuffixes = wildcardSuffixes;
        _exactHosts = exactHosts;
    }

    /// <summary>配置里出现了 <see cref="AnyPattern"/>。</summary>
    public bool AllowAny { get; }

    /// <summary>
    /// 由环境变量内容构造。<paramref name="configured"/> 为逗号分隔的模式列表，
    /// 追加在 <see cref="DefaultPatterns"/> 之后（默认项无法被配置关闭，这是刻意的：
    /// 配置只做"加"，避免一次手滑把白名单清空）。
    /// </summary>
    public static HostAllowList Create(string? configured)
    {
        var patterns = new List<string>(DefaultPatterns);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            patterns.AddRange(configured.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var allowAny = false;
        var wildcards = new List<string>();
        var exact = new List<string>();

        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().TrimEnd('.');
            if (pattern.Length == 0) continue;

            if (pattern == AnyPattern)
            {
                allowAny = true;
                continue;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..]; // ".edu.cn"
                if (suffix.Length > 1) wildcards.Add(suffix);
                continue;
            }

            exact.Add(pattern);
        }

        return new HostAllowList(
            allowAny,
            wildcards.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            exact.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>该 host 是否在白名单内。<c>null</c>/空串一律拒绝（fail-closed）。</summary>
    public bool IsAllowed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (AllowAny) return true;

        var candidate = host.Trim().TrimEnd('.');

        // IP 字面量不走白名单（默认项都是域名），除非配置里显式写了它
        foreach (var exactHost in _exactHosts)
        {
            if (string.Equals(exactHost, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var suffix in _wildcardSuffixes)
        {
            // 要求 host 比后缀更长：*.edu.cn 不该匹配裸 edu.cn
            if (candidate.Length > suffix.Length &&
                candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
