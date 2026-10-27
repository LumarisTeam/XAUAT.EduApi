using System.Net;
using System.Net.Sockets;

namespace XAUAT.EduApi.Login.Unified;

/// <summary>
/// 目标地址解析到了内网/保留地址。<c>HttpClient</c> 有时会把它包进
/// <see cref="HttpRequestException"/>，因此调用方两处都要判。
/// </summary>
internal sealed class BlockedTargetException(string message) : Exception(message);

/// <summary>
/// 建连前的地址闸门：把域名解析成 IP，丢掉一切内网/保留地址，只对公网地址发起 TCP 连接。
/// <para>
/// <b>为什么不能只做"请求前解析一次域名再校验"</b>：那样校验用的解析结果与
/// <c>HttpClient</c> 自己再解析一次的结果是两次独立的 DNS 查询（TOCTOU），
/// 攻击者用 TTL=0 的域名就可以第一次答公网 IP、第二次答 <c>127.0.0.1</c>。
/// 挂在 <see cref="SocketsHttpHandler.ConnectCallback"/> 上则只有一个解析结果，
/// 校验与建连用的是同一个 IP，rebinding 无从下手。
/// </para>
/// <para>
/// 关掉 <c>UseProxy</c> 也是同一个道理：走代理时连接是代理建的，这里的校验形同虚设。
/// 见注册处 <c>AddLoginServices</c>。
/// </para>
/// </summary>
internal static class UnifiedConnectGuard
{
    /// <summary>
    /// 解析目标主机并在通过校验的 IP 上建立连接。全部 IP 都被拒时抛
    /// <see cref="HttpRequestException"/>（由上层转成 400/502，而不是 500）。
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);

    /// <summary>
    /// 与上面同一套"解析 → 过滤内网 → 逐个尝试建连"的逻辑，供**不走** <see cref="SocketsHttpHandler"/>
    /// 的原始传输复用（见 <see cref="TolerantHttpMessageHandler"/>）。
    /// <para>
    /// 两条通道必须共用这一份实现：容错传输是"严格解析失败时的退路"，
    /// 如果它自己另写一套地址校验，那条退路就成了绕过 SSRF 防线的旁路。
    /// </para>
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            // IP 字面量：不查 DNS。白名单通常已经把这类地址挡在外面，
            // 这里再挡一次内网部分，防止白名单被配成 "*" 时直接打内网。
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }

        var permitted = addresses.Where(address => !IsBlocked(address)).ToArray();
        if (permitted.Length == 0)
        {
            throw new BlockedTargetException($"目标主机 {host} 解析到内网或保留地址，已拒绝连接");
        }

        Exception? lastError = null;
        foreach (var address in permitted)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
        }

        throw new HttpRequestException($"无法连接到 {host}", lastError);
    }

    /// <summary>
    /// 是否为不该被访问的地址：环回、私有、链路本地（含云元数据 <c>169.254.169.254</c>）、
    /// CGNAT、多播、保留段。
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                   // 0.0.0.0/8     "本网络"
                10 => true,                                  // 10.0.0.0/8    私有
                100 => b[1] is >= 64 and <= 127,             // 100.64.0.0/10 CGNAT
                127 => true,                                 // 127.0.0.0/8   环回
                169 => b[1] == 254,                          // 169.254.0.0/16 链路本地 / 云元数据
                172 => b[1] is >= 16 and <= 31,              // 172.16.0.0/12 私有
                192 => b[1] == 168 || (b[1] == 0 && b[2] == 0), // 192.168/16 私有、192.0.0.0/24
                198 => b[1] is 18 or 19,                     // 198.18.0.0/15 基准测试
                224 => true,                                 // 224.0.0.0/4   多播
                >= 240 => true,                              // 240.0.0.0/4   保留 / 广播
                _ => false
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            if (address.IsIPv6UniqueLocal) return true;      // fc00::/7

            var b = address.GetAddressBytes();
            // 2001:db8::/32 文档用地址
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;
            return false;
        }

        // 未知地址族：fail-closed
        return true;
    }
}
