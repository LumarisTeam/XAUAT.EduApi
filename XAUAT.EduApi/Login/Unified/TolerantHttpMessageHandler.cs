using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace XAUAT.EduApi.Login.Unified;

/// <summary>
/// 一条**容忍畸形响应头**的原始 HTTP/1.1 传输通道。
/// <para>
/// <b>为什么需要它</b>：<c>SocketsHttpHandler</c> 在解析响应头时会严格校验头名（RFC 9110 的 token），
/// 遇到非法头名直接抛 <see cref="HttpRequestException"/>（<see cref="HttpRequestError.InvalidResponse"/>），
/// 而这个异常发生在任何 <c>HttpMessageHandler</c> 拿到响应之前，**没有任何配置项可以放宽**。
/// 现实中确实存在这样的服务器：<c>gmis.xauat.edu.cn</c> 每一条响应都带
/// <c>SELINUX=Server:  </c>——名字里含 <c>=</c>，非法；浏览器会把它当未知头丢掉，所以校方长期发现不了。
/// 结果就是"目标站点明明在线、跳转链也正常，但 .NET 客户端连第一跳都发不出去"。
/// </para>
/// <para>
/// <b>安全边界全部保留</b>：<list type="bullet">
/// <item>建连走 <see cref="UnifiedConnectGuard.ConnectAsync(string,int,CancellationToken)"/>，
/// 即"解析 → 丢弃内网/保留地址 → 在同一批 IP 上建连"，与严格通道是同一份实现；</item>
/// <item>不读代理配置（原始 socket，<c>UseProxy=false</c> 的等价物）；</item>
/// <item>TLS 用系统信任链校验，SNI 取请求主机名，不提供"信任任意证书"的开关；</item>
/// <item>不自动跟随重定向、不自动存 cookie——与严格通道一致，由调用方
/// （<see cref="UnifiedSsoClient"/> / <see cref="Xauat.XauatCookieJar"/>）手工管理。</item>
/// </list>
/// </para>
/// <para>
/// <b>只做发起登录所必需的那一小部分</b>：GET/POST、Content-Length 与 chunked、gzip/deflate/br、
/// 1xx 中间响应跳过。<c>Connection: close</c>，一次请求一条连接——登录是低频操作，
/// 不值得为连接复用引入 keep-alive 状态机。
/// </para>
/// <para>
/// 由 <see cref="ProtocolFallbackHandler"/> 在严格解析失败时触发；单独注册成主传输没有必要，
/// 也不建议——严格通道能覆盖的场合优先走严格通道。
/// </para>
/// </summary>
internal sealed class TolerantHttpMessageHandler : HttpMessageHandler
{
    /// <summary>响应头（含状态行与全部头行）的字节上限，防止对端无限灌数据。</summary>
    private const int MaxResponseHeadBytes = 64 * 1024;

    /// <summary>响应正文上限（含解压后）。登录页面只有几百 KB，8 MiB 足够且能挡住 zip bomb。</summary>
    private const int MaxResponseBodyBytes = 8 * 1024 * 1024;

    /// <summary>由本类自己写入的请求头，遍历 <see cref="HttpRequestMessage.Headers"/> 时要跳过。</summary>
    private static readonly HashSet<string> ManagedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "Accept-Encoding",
        "Expect", "Proxy-Connection", "Keep-Alive", "TE", "Trailer", "Upgrade"
    };

    private static readonly Encoding HeaderEncoding = Encoding.Latin1;

    private readonly ILogger<TolerantHttpMessageHandler> _logger;
    private readonly Func<Uri, CancellationToken, Task<Stream>> _connect;

    public TolerantHttpMessageHandler(ILogger<TolerantHttpMessageHandler> logger)
        : this(logger, connect: null)
    {
    }

    /// <param name="connect">仅测试用：注入一个指向本机监听的建连函数，绕开白名单/内网拦截。</param>
    internal TolerantHttpMessageHandler(
        ILogger<TolerantHttpMessageHandler> logger,
        Func<Uri, CancellationToken, Task<Stream>>? connect)
    {
        _logger = logger;
        _connect = connect ?? DefaultConnectAsync;
    }

    private static async Task<Stream> DefaultConnectAsync(Uri uri, CancellationToken cancellationToken)
        => await UnifiedConnectGuard.ConnectAsync(uri.IdnHost, uri.Port, cancellationToken);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri
                  ?? throw new HttpRequestException(HttpRequestError.InvalidResponse, "原始传输要求请求带绝对 URI");

        if (uri.Scheme is not ("http" or "https"))
        {
            throw new HttpRequestException(
                HttpRequestError.InvalidResponse, $"原始传输只支持 http/https，实际为 {uri.Scheme}");
        }

        // 请求体只有表单，先整体缓冲：写头之前知道 Content-Length。
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        Stream? transport = null;
        try
        {
            transport = await _connect(uri, cancellationToken);

            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                // SslStream 默认 leaveInnerStreamOpen=false：释放它就会连带释放 socket。
                var ssl = new SslStream(transport);
                transport = ssl;
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = uri.IdnHost,
                    // None = 交给系统默认（TLS 1.2/1.3），不要在这里手写协议版本
                    EnabledSslProtocols = SslProtocols.None
                }, cancellationToken);
            }

            await WriteRequestAsync(transport, request, body, cancellationToken);
            return await ReadResponseAsync(transport, request, cancellationToken);
        }
        catch (BlockedTargetException)
        {
            // 内网地址：必须原样上抛，调用方据此回 400 而不是 502
            throw;
        }
        catch (OperationCanceledException)
        {
            // 超时/取消的语义由调用方判断，别裹成 HttpRequestException
            throw;
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or AuthenticationException or ObjectDisposedException)
        {
            throw new HttpRequestException(HttpRequestError.Unknown, $"原始传输失败：{ex.Message}", ex);
        }
        finally
        {
            if (transport is not null)
            {
                await transport.DisposeAsync();
            }
        }
    }

    // ================================================================ 写请求

    private static async Task WriteRequestAsync(
        Stream stream, HttpRequestMessage request, byte[]? body, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var head = new StringBuilder(512);

        head.Append(request.Method.Method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
        head.Append("Host: ").Append(uri.IsDefaultPort ? HostLiteral(uri) : $"{HostLiteral(uri)}:{uri.Port}").Append("\r\n");
        // 一次请求一条连接：不用维护 keep-alive 与分帧状态
        head.Append("Connection: close\r\n");
        // 自己解压，不依赖 SocketsHttpHandler.AutomaticDecompression
        head.Append("Accept-Encoding: gzip, deflate\r\n");

        foreach (var header in request.Headers)
        {
            if (ManagedRequestHeaders.Contains(header.Key)) continue;
            head.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
        }

        if (body is not null && request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                head.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
            }

            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }

        head.Append("\r\n");

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), cancellationToken);
        if (body is { Length: > 0 })
        {
            await stream.WriteAsync(body, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Host 头里 IPv6 字面量必须带方括号（<c>[2001:db8::1]</c>）。</summary>
    private static string HostLiteral(Uri uri)
        => uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;

    // ================================================================ 读响应

    private async Task<HttpResponseMessage> ReadResponseAsync(
        Stream stream, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var reader = new ResponseReader(stream);

        while (true)
        {
            var statusLine = await reader.ReadLineAsync(MaxResponseHeadBytes, cancellationToken)
                             ?? throw new HttpRequestException(
                                 HttpRequestError.ResponseEnded, "上游在返回状态行前就关闭了连接");

            var status = ParseStatusLine(statusLine);
            var (headers, dropped) = await ReadHeadersAsync(reader, cancellationToken);

            if (dropped.Count > 0)
            {
                // 正常响应不该出现；出现说明上游/中间件在造非法头。一行代码的可见度，
                // 否则"某个头莫名没生效"会变成无从下手的怪问题。
                _logger.LogWarning("原始传输：丢弃 {Count} 个不符合 HTTP 规范的响应头（{Names}）",
                    dropped.Count, string.Join(", ", dropped));
            }

            if (status is >= 100 and < 200)
            {
                // 1xx（100 Continue 等）不带正文，继续读真正的响应
                continue;
            }

            var raw = await ReadBodyAsync(reader, headers, request.Method, status, cancellationToken);
            var body = await DecodeAsync(raw, Find(headers, "Content-Encoding"), cancellationToken);
            return BuildResponse((HttpStatusCode)status, headers, body, request);
        }
    }

    private static int ParseStatusLine(string line)
    {
        var firstSpace = line.IndexOf(' ');
        if (!line.StartsWith("HTTP/", StringComparison.Ordinal) || firstSpace < 0)
        {
            throw new HttpRequestException(
                HttpRequestError.InvalidResponse, $"上游返回了无法解析的状态行：{Describe(line)}");
        }

        var rest = line[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf(' ');
        var codeText = secondSpace >= 0 ? rest[..secondSpace] : rest;

        if (!int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            || status is < 100 or > 599)
        {
            throw new HttpRequestException(
                HttpRequestError.InvalidResponse, $"上游返回了无法解析的状态码：{Describe(line)}");
        }

        return status;
    }

    /// <summary>
    /// 读头行。**这里是"容忍"的落点**：不符合 RFC 9110 的行只丢弃并记下来，绝不抛。
    /// 严格通道正是在这两处（无冒号 / 头名含分隔符）抛 <c>Received an invalid header name</c>。
    /// </summary>
    private static async Task<(List<KeyValuePair<string, string>> Headers, List<string> Dropped)> ReadHeadersAsync(
        ResponseReader reader, CancellationToken cancellationToken)
    {
        var headers = new List<KeyValuePair<string, string>>();
        var dropped = new List<string>();
        var totalBytes = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(MaxResponseHeadBytes, cancellationToken)
                       ?? throw new HttpRequestException(
                           HttpRequestError.ResponseEnded, "上游在响应头结束前就关闭了连接");

            if (line.Length == 0) return (headers, dropped);

            totalBytes += line.Length;
            if (totalBytes > MaxResponseHeadBytes)
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "上游响应头超过上限");
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                dropped.Add(Describe(line));
                continue;
            }

            var name = line[..colon].TrimEnd();
            var value = line[(colon + 1)..].Trim();

            if (name.Length == 0 || !IsToken(name))
            {
                dropped.Add(Describe(name));
                continue;
            }

            headers.Add(new KeyValuePair<string, string>(name, value));
        }
    }

    private async Task<byte[]> ReadBodyAsync(
        ResponseReader reader, List<KeyValuePair<string, string>> headers,
        HttpMethod method, int status, CancellationToken cancellationToken)
    {
        if (method == HttpMethod.Head || status is 204 or 304) return [];

        var transferEncoding = Find(headers, "Transfer-Encoding");
        if (transferEncoding is not null &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadChunkedAsync(reader, cancellationToken);
        }

        var contentLength = Find(headers, "Content-Length");
        if (contentLength is not null &&
            long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            if (length > MaxResponseBodyBytes)
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, $"上游响应正文超过上限（{length} 字节）");
            }

            return await reader.ReadExactlyAsync((int)length, cancellationToken);
        }

        // 没有长度声明：读到对端关闭（我们发的是 Connection: close）
        return await reader.ReadToEndAsync(MaxResponseBodyBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadChunkedAsync(
        ResponseReader reader, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();

        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(MaxResponseHeadBytes, cancellationToken)
                           ?? throw new HttpRequestException(
                               HttpRequestError.ResponseEnded, "上游在分块正文结束前就关闭了连接");

            // 分块扩展（";name=value"）忽略
            var semicolon = sizeLine.IndexOf(';');
            var sizeText = (semicolon >= 0 ? sizeLine[..semicolon] : sizeLine).Trim();

            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new HttpRequestException(
                    HttpRequestError.InvalidResponse, $"上游返回了无法解析的分块长度：{Describe(sizeLine)}");
            }

            if (size == 0)
            {
                await SkipTrailersAsync(reader, cancellationToken);
                return body.ToArray();
            }

            if (body.Length + size > MaxResponseBodyBytes)
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "上游响应正文超过上限");
            }

            body.Write(await reader.ReadExactlyAsync(size, cancellationToken));

            // 每个分块后面跟一个 CRLF
            await reader.ReadLineAsync(MaxResponseHeadBytes, cancellationToken);
        }
    }

    private static async Task SkipTrailersAsync(ResponseReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(MaxResponseHeadBytes, cancellationToken);
            if (line is null || line.Length == 0) return;
        }
    }

    private static async Task<byte[]> DecodeAsync(
        byte[] body, string? contentEncoding, CancellationToken cancellationToken)
    {
        if (body.Length == 0 || string.IsNullOrWhiteSpace(contentEncoding)) return body;

        var encodings = contentEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 多段 Content-Encoding 按声明顺序叠加，解码要反着来
        for (var index = encodings.Length - 1; index >= 0; index--)
        {
            body = encodings[index].ToLowerInvariant() switch
            {
                "identity" => body,
                "gzip" or "x-gzip" => await DecompressAsync(body, stream => new GZipStream(stream, CompressionMode.Decompress), cancellationToken),
                "deflate" => await DecompressAsync(body, stream => new DeflateStream(stream, CompressionMode.Decompress), cancellationToken),
                "br" => await DecompressAsync(body, stream => new BrotliStream(stream, CompressionMode.Decompress), cancellationToken),
                var unsupported => throw new HttpRequestException(
                    HttpRequestError.InvalidResponse, $"原始传输不支持响应压缩格式 {unsupported}")
            };
        }

        return body;
    }

    private static async Task<byte[]> DecompressAsync(
        byte[] body, Func<Stream, Stream> wrap, CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(body, writable: false);
        await using var decompressor = wrap(source);
        using var result = new MemoryStream();

        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await decompressor.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (result.Length + read > MaxResponseBodyBytes)
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "解压后的响应正文超过上限");
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }

    private static HttpResponseMessage BuildResponse(
        HttpStatusCode status, List<KeyValuePair<string, string>> headers,
        byte[] body, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request };
        string? contentType = null;

        foreach (var (name, value) in headers)
        {
            switch (name.ToLowerInvariant())
            {
                // 长度由 ByteArrayContent 重算、压缩已经解码、分块不再是分块
                case "content-length":
                case "content-encoding":
                case "transfer-encoding":
                    continue;
                // 字符集挂在 Content 上，调用方（XauatHttpUtils）从这里读 charset
                case "content-type":
                    contentType = value;
                    continue;
            }

            response.Headers.TryAddWithoutValidation(name, value);
        }

        var content = new ByteArrayContent(body);
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        response.Content = content;
        return response;
    }

    // ================================================================ 小工具

    private static string? Find(List<KeyValuePair<string, string>> headers, string name)
        => headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>RFC 9110 token：头名只能用这些字符，含 <c>=</c>、空格、控制字符即非法。</summary>
    private static bool IsToken(string name)
    {
        foreach (var c in name)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is
                '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>日志里的一行摘要：截断，避免把整页 HTML 灌进日志。</summary>
    private static string Describe(string value)
        => value.Length <= 120 ? value : value[..120] + "…";

    /// <summary>带缓冲的按行/按量读取，够用即可，不追求零分配。</summary>
    private sealed class ResponseReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8 * 1024];
        private int _offset;
        private int _count;

        /// <summary>读一行（去掉行尾 CRLF/LF）。对端在行首就关闭时返回 <c>null</c>。</summary>
        public async ValueTask<string?> ReadLineAsync(int maxLength, CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();

            while (true)
            {
                if (_offset >= _count && !await FillAsync(cancellationToken))
                {
                    return line.Length == 0
                        ? null
                        : HeaderEncoding.GetString(line.GetBuffer(), 0, (int)line.Length);
                }

                var span = _buffer.AsSpan(_offset, _count - _offset);
                var newline = span.IndexOf((byte)'\n');

                if (newline >= 0)
                {
                    line.Write(span[..newline]);
                    _offset += newline + 1;
                    return TakeLine(line, maxLength);
                }

                line.Write(span);
                _offset = _count;

                if (line.Length > maxLength)
                {
                    throw new HttpRequestException(HttpRequestError.InvalidResponse, "上游响应头行超过上限");
                }
            }
        }

        public async ValueTask<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
        {
            var result = new byte[count];
            var written = 0;

            while (written < count)
            {
                if (_offset >= _count && !await FillAsync(cancellationToken))
                {
                    throw new HttpRequestException(
                        HttpRequestError.ResponseEnded, "上游在响应正文完全到达前关闭了连接");
                }

                var take = Math.Min(_count - _offset, count - written);
                _buffer.AsSpan(_offset, take).CopyTo(result.AsSpan(written));
                _offset += take;
                written += take;
            }

            return result;
        }

        public async ValueTask<byte[]> ReadToEndAsync(int maxLength, CancellationToken cancellationToken)
        {
            using var result = new MemoryStream();

            while (true)
            {
                if (_offset >= _count && !await FillAsync(cancellationToken)) return result.ToArray();

                var remaining = _count - _offset;
                if (result.Length + remaining > maxLength)
                {
                    throw new HttpRequestException(HttpRequestError.InvalidResponse, "上游响应正文超过上限");
                }

                result.Write(_buffer, _offset, remaining);
                _offset = _count;
            }
        }

        private static string TakeLine(MemoryStream line, int maxLength)
        {
            var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
            if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];

            if (bytes.Length > maxLength)
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "上游响应头行超过上限");
            }

            return HeaderEncoding.GetString(bytes);
        }

        private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
        {
            _offset = 0;
            _count = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken);
            return _count > 0;
        }
    }
}
