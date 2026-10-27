using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.EduApi.Login.Unified;

namespace XAUAT.EduApi.Tests.Login.UnifiedAuth;

/// <summary>
/// 协议回退壳的用例：严格解析失败时把请求**原样**重放到容错通道，其余失败不重放。
/// <para>
/// 这里守两条容易回退的错误：
/// <list type="number">
/// <item>回退条件放宽成"任何 <see cref="HttpRequestException"/> 都重放"——那样连不上、
/// 证书错误也会被重试两次，日志里会出现两个不同来源的异常；</item>
/// <item>重放时丢了 Cookie 或请求体——表现为"目标站点能连上了，但 CAS 报表单过期/未登录"，
/// 比原来的报错更难查。</item>
/// </list>
/// </para>
/// </summary>
public class ProtocolFallbackHandlerTests
{
    private const string TargetUrl = "https://gmis.xauat.edu.cn/pyxx/sso/login";

    [Fact]
    public async Task Send_ShouldReplayThroughTolerantTransport_WhenStrictParserRejectsHeaderName()
    {
        var strict = new RecordingHandler(_ => throw InvalidHeaderName());
        var tolerant = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)));
        using var client = CreateClient(strict, tolerant);

        using var request = new HttpRequestMessage(HttpMethod.Post, TargetUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "2623213365"
            })
        };
        request.Headers.TryAddWithoutValidation("Cookie", "JSESSIONID=node1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(strict.Calls);

        var replay = Assert.Single(tolerant.Calls);
        Assert.Equal("POST", replay.Method);
        Assert.Equal(TargetUrl, replay.Uri);
        Assert.Equal("JSESSIONID=node1", replay.Cookie);
        Assert.NotNull(replay.Body);
        Assert.Contains("username=2623213365", replay.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ShouldNotFallBack_WhenFailureIsANetworkError()
    {
        var strict = new RecordingHandler(
            _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));
        var tolerant = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = CreateClient(strict, tolerant);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(TargetUrl));

        // 连接错误换条通道也一样失败，重放只会白花一倍时间并掩盖真实原因
        Assert.Empty(tolerant.Calls);
    }

    [Fact]
    public async Task Send_ShouldRememberBrokenHost_AndSkipStrictTransportNextTime()
    {
        var strict = new RecordingHandler(_ => throw InvalidHeaderName());
        var tolerant = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = CreateClient(strict, tolerant);

        (await client.GetAsync(TargetUrl)).Dispose();
        (await client.GetAsync(TargetUrl)).Dispose();

        // 一次登录要跳好几跳且会反复回到同一台坏主机，第二次起不该再让严格通道失败一次
        Assert.Single(strict.Calls);
        Assert.Equal(2, tolerant.Calls.Count);
    }

    private static HttpClient CreateClient(HttpMessageHandler strict, HttpMessageHandler tolerant)
        => new(new ProtocolFallbackHandler(strict, tolerant, NullLogger<ProtocolFallbackHandler>.Instance));

    private static HttpRequestException InvalidHeaderName()
        => new(HttpRequestError.InvalidResponse, "Received an invalid header name: 'SELINUX=Server'.");

    /// <summary>记录每次调用（方法/URL/正文/Cookie）并按脚本应答或抛异常的 handler。</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<Recorded> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Calls.Add(new Recorded(
                request.Method.Method,
                request.RequestUri!.ToString(),
                body,
                request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null));

            return await respond(request);
        }

        internal sealed record Recorded(string Method, string Uri, string? Body, string? Cookie);
    }
}
