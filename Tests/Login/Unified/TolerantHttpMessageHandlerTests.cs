using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.EduApi.Login.Unified;
using XAUAT.EduApi.Tests.Login.TestSupport;

namespace XAUAT.EduApi.Tests.Login.UnifiedAuth;

/// <summary>
/// 容错传输的回归用例。
/// <para>
/// 第一条用例就是线上故障的复刻：<c>gmis.xauat.edu.cn</c> 的 302 响应带
/// <c>SELINUX=Server:  </c>（头名含 <c>=</c>，违反 RFC 9110），
/// <c>SocketsHttpHandler</c> 会在解析响应头时抛 <c>Received an invalid header name</c>，
/// 表现为"目标站点在线但登录直接报网络错误"。这里的字节序列来自真实抓包（curl -D -）。
/// </para>
/// </summary>
public class TolerantHttpMessageHandlerTests
{
    private const string GmisRedirect =
        "HTTP/1.1 302 Found\r\n" +
        "SELINUX=Server:  \r\n" +
        "Date: Tue, 27 Oct 2026 14:02:27 GMT\r\n" +
        "Content-Length: 0\r\n" +
        "Connection: keep-alive\r\n" +
        "Location: http://authserver.xauat.edu.cn/authserver/login?service=https%3A%2F%2Fgmis.xauat.edu.cn%2Fpyxx%2Fsso%2Flogin\r\n" +
        "Content-Security-Policy: upgrade-insecure-requests;connect-src *\r\n" +
        "\r\n";

    private const string TargetUrl = "http://gmis.xauat.edu.cn/pyxx/sso/login";

    [Fact]
    public async Task Send_ShouldTolerateInvalidHeaderName_AndKeepLocation()
    {
        await using var server = RawHttpServer.Start(GmisRedirect);
        using var handler = CreateHandler(server.Port);
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(TargetUrl);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "http://authserver.xauat.edu.cn/authserver/login?service=https%3A%2F%2Fgmis.xauat.edu.cn%2Fpyxx%2Fsso%2Flogin",
            response.Headers.Location!.ToString());

        // 非法头名那行整行丢弃：既不能抛，也不能以原样留在响应头里
        Assert.DoesNotContain(response.Headers, header => header.Key.Contains('='));

        await server.Completed;
        Assert.Contains(server.ReceivedHead,
            line => line.StartsWith("GET /pyxx/sso/login HTTP/1.1", StringComparison.Ordinal));
        Assert.Contains(server.ReceivedHead,
            line => line.Equals("Connection: close", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.ReceivedHead,
            line => line.Equals("Host: gmis.xauat.edu.cn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Send_ShouldWriteFormBodyAndForwardCookie()
    {
        await using var server = RawHttpServer.Start(
            "HTTP/1.1 302 Found\r\nContent-Length: 0\r\nLocation: /landing\r\n\r\n");

        using var handler = CreateHandler(server.Port);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://gmis.xauat.edu.cn/pyxx/sso/submit")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "2623213365",
                ["pwdEncryptSalt"] = "abc"
            })
        };
        request.Headers.TryAddWithoutValidation("Cookie", "JSESSIONID=node1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/landing", response.Headers.Location!.ToString());

        await server.Completed;
        Assert.Contains(server.ReceivedHead, line => line.StartsWith("POST /pyxx/sso/submit HTTP/1.1", StringComparison.Ordinal));
        Assert.Contains(server.ReceivedHead, line => line.StartsWith("Content-Type: application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.ReceivedHead, line => line.Equals("Cookie: JSESSIONID=node1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("username=2623213365", server.ReceivedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ShouldDecodeChunkedBody()
    {
        await using var server = RawHttpServer.Start(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html;charset=UTF-8\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n" +
            "5\r\nhello\r\n" +
            "6\r\n world\r\n" +
            "0\r\n\r\n");

        using var handler = CreateHandler(server.Port);
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(TargetUrl);

        Assert.Equal("hello world", await response.Content.ReadAsStringAsync());
        Assert.Equal("UTF-8", response.Content.Headers.ContentType?.CharSet);
    }

    [Fact]
    public async Task Send_ShouldDecodeGzipBody()
    {
        var html = Encoding.UTF8.GetBytes("<html><body>登录页</body></html>");

        byte[] body;
        using (var compressed = new MemoryStream())
        {
            using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(html);
            }

            body = compressed.ToArray();
        }

        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html;charset=UTF-8\r\n" +
            "Content-Encoding: gzip\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "\r\n");

        await using var server = RawHttpServer.Start([.. head, .. body]);
        using var handler = CreateHandler(server.Port);
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(TargetUrl);

        Assert.Equal("<html><body>登录页</body></html>", await response.Content.ReadAsStringAsync());
        // 已解压，就不该再把压缩声明留在响应上
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Send_ShouldRejectUnparsableStatusLine()
    {
        await using var server = RawHttpServer.Start("NOT-HTTP/1.1 200 OK\r\n\r\n");

        using var handler = CreateHandler(server.Port);
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(TargetUrl));

        Assert.Equal(HttpRequestError.InvalidResponse, exception.HttpRequestError);
    }

    /// <summary>把 handler 的建连指到本机监听（绕开白名单/内网拦截，那是另一条用例的事）。</summary>
    private static TolerantHttpMessageHandler CreateHandler(int port)
        => new(
            NullLogger<TolerantHttpMessageHandler>.Instance,
            async (_, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            });
}
