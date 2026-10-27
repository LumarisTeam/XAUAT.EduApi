using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XAUAT.EduApi.Tests.Login.TestSupport;

/// <summary>
/// 只服务一次请求的裸 TCP "HTTP 服务器"：把调用方给的字节**原样**写回去。
/// <para>
/// 用来回放真实世界的畸形响应（例如 <c>gmis.xauat.edu.cn</c> 每条响应都带的
/// <c>SELINUX=Server:  </c> 头）。这类响应用 <see cref="HttpMessageHandler"/> 替身造不出来——
/// 要验证的恰恰是"原始字节怎么被解析"，而 <see cref="HttpResponseMessage"/> 里根本放不下非法头名。
/// </para>
/// </summary>
internal sealed class RawHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _response;
    private readonly Task _serving;

    private RawHttpServer(TcpListener listener, byte[] response)
    {
        _listener = listener;
        _response = response;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _serving = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>收到的请求头，按行（不含行尾 CRLF）。</summary>
    public List<string> ReceivedHead { get; } = [];

    /// <summary>收到的请求正文（按 Content-Length 读全）。</summary>
    public string ReceivedBody { get; private set; } = string.Empty;

    /// <summary>那一次请求处理完（测试里应在客户端调用之后 await）。</summary>
    public Task Completed => _serving;

    public static RawHttpServer Start(string response) => Start(Encoding.UTF8.GetBytes(response));

    /// <summary>二进制响应（gzip 正文之类的用例）。</summary>
    public static RawHttpServer Start(byte[] response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new RawHttpServer(listener, response);
    }

    private async Task ServeAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        using var received = new MemoryStream();
        var buffer = new byte[1024];
        var headEnd = -1;

        while (headEnd < 0)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0) return;

            received.Write(buffer, 0, read);
            headEnd = received.GetBuffer().AsSpan(0, (int)received.Length).IndexOf("\r\n\r\n"u8);
        }

        var all = received.GetBuffer().AsSpan(0, (int)received.Length);
        ReceivedHead.AddRange(Encoding.Latin1.GetString(all[..headEnd]).Split("\r\n"));

        var contentLength = ReceivedHead
            .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(line => int.TryParse(line["Content-Length:".Length..].Trim(), out var parsed) ? parsed : 0)
            .FirstOrDefault();

        var body = new byte[contentLength];
        var bodyStart = headEnd + 4;
        var already = all[bodyStart..];
        var copied = Math.Min(already.Length, contentLength);
        already[..copied].CopyTo(body);

        while (copied < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(copied));
            if (read <= 0) break;
            copied += read;
        }

        ReceivedBody = Encoding.UTF8.GetString(body, 0, copied);

        await stream.WriteAsync(_response);
        await stream.FlushAsync();
        client.Client.Shutdown(SocketShutdown.Send);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();

        try
        {
            await _serving;
        }
        catch
        {
            // 用例失败时可能根本没连进来，清理阶段不必再抛
        }
    }
}
