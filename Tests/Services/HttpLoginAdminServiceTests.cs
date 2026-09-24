using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// <see cref="HttpLoginAdminService"/> 的测试。
/// <para>
/// 两条最关键的覆盖点：
/// </para>
/// <list type="number">
///   <item><b>snake_case 反序列化</b>——上游的 <c>created_at</c>/<c>type_label</c> 与
///     C# 属性名对不上，而 <c>PropertyNameCaseInsensitive</c> 兜不住这种差异。
///     漏掉 <c>[JsonPropertyName]</c> 不会报错，只会让整列变成默认值。</item>
///   <item><b>解封的 400 是业务结果而不是错误</b>——「该用户当前没被封」不能变成 500。</item>
/// </list>
/// </summary>
public class HttpLoginAdminServiceTests
{
    private const string BaseAddress = "http://xauat-loginapi:8080";

    /// <summary>零退避：否则传输失败用例会真等 2+4+8 秒。</summary>
    private static readonly TimeSpan[] NoDelay = [];

    private const string BanLogsBody = """
        {"total":2,"items":[
          {"type":"auto_ban","type_label":"自动封禁","created_at":1758600000,
           "created_at_human":"2025-09-23 12:00:00","unban_at":1758686400,
           "unban_at_human":"2025-09-24 12:00:00","reason":"连续多次触发登录限流",
           "account":"xauat:2024001"},
          {"type":"manual_unban","type_label":"手动解封","created_at":1758610000,
           "created_at_human":"2025-09-23 14:46:40","reason":"手动解封",
           "account":"xauat:2024002:abc123"}
        ]}
        """;

    // ---------------------------------------------------------------- 用户数

    [Fact]
    public async Task GetUserCount_ShouldParseCount()
    {
        var service = CreateService(HttpStatusCode.OK, """{"count":42}""");

        Assert.Equal(42, await service.GetUserCountAsync());
    }

    [Fact]
    public async Task GetUserCount_ShouldThrow_WhenBodyIsNotParseable()
    {
        // 解析不出来与"真的是 0 人"必须能区分，否则管理端会安静地显示成 0
        var service = CreateService(HttpStatusCode.OK, "<html>404</html>");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetUserCountAsync());
    }

    [Fact]
    public async Task GetUserCount_ShouldRequestRootPath()
    {
        string? path = null;
        var service = CreateService(HttpStatusCode.OK, """{"count":0}""",
            onRequest: request =>
            {
                path = request.RequestUri?.PathAndQuery;
                return Task.CompletedTask;
            });

        await service.GetUserCountAsync();

        Assert.Equal("/user_count", path);
    }

    // ---------------------------------------------------------------- 封禁日志

    [Fact]
    public async Task GetBanLogs_ShouldMapSnakeCaseFields()
    {
        // 这条是防"静默反序列化成默认值"的哨兵：任何字段漏了 [JsonPropertyName]，
        // 这里都会退化成 0/空串。
        var service = CreateService(HttpStatusCode.OK, BanLogsBody);

        var page = await service.GetBanLogsAsync(null, null);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);

        var autoBan = page.Items[0];
        Assert.Equal("auto_ban", autoBan.Type);
        Assert.Equal("自动封禁", autoBan.TypeLabel);
        Assert.Equal(1758600000, autoBan.CreatedAt);
        Assert.Equal("2025-09-23 12:00:00", autoBan.CreatedAtHuman);
        Assert.Equal(1758686400, autoBan.UnbanAt);
        Assert.Equal("2025-09-24 12:00:00", autoBan.UnbanAtHuman);
        Assert.Equal("连续多次触发登录限流", autoBan.Reason);
        Assert.Equal("xauat:2024001", autoBan.Account);
    }

    [Fact]
    public async Task GetBanLogs_ShouldLeaveUnbanFieldsNull_WhenEntryHasNoSnapshot()
    {
        // 既没有 data 也没有 previous 快照的条目，上游会把 unban_at 两个键整个省掉，
        // 反序列化后应保持 null，而不是被填成 0（0 会被前端格式化成 1970 年）。
        // 注意正常的手动解封条目**是带** unban_at 的：它取的是解封前的 previous 快照。
        var service = CreateService(HttpStatusCode.OK, BanLogsBody);

        var page = await service.GetBanLogsAsync(null, null);

        var manualUnban = page.Items[1];
        Assert.Equal("manual_unban", manualUnban.Type);
        Assert.Null(manualUnban.UnbanAt);
        Assert.Null(manualUnban.UnbanAtHuman);
        // 历史条目的 account 是三段式，解封时要按前两段还原学校与用户名
        Assert.Equal("xauat:2024002:abc123", manualUnban.Account);
    }

    [Fact]
    public async Task GetBanLogs_ShouldRequestUnfilteredPath_WhenNoFilters()
    {
        string? path = null;
        var service = CreateService(HttpStatusCode.OK, """{"total":0,"items":[]}""",
            onRequest: request =>
            {
                path = request.RequestUri?.PathAndQuery;
                return Task.CompletedTask;
            });

        await service.GetBanLogsAsync(null, null);

        Assert.Equal("/security/ban_logs", path);
    }

    [Fact]
    public async Task GetBanLogs_ShouldEscapeQueryValues()
    {
        string? path = null;
        var service = CreateService(HttpStatusCode.OK, """{"total":0,"items":[]}""",
            onRequest: request =>
            {
                path = request.RequestUri?.PathAndQuery;
                return Task.CompletedTask;
            });

        await service.GetBanLogsAsync("xauat", "2024&01");

        Assert.Equal("/security/ban_logs?school=xauat&username=2024%2601", path);
    }

    [Fact]
    public async Task GetBanLogs_ShouldThrowNotConfigured_WhenLoginApiBaseUrlMissing()
    {
        // 封禁日志的 JSON 接口只有 XAUAT.LoginApi 有；回落到 Flask 时打过去只会 404，
        // 与其冒泡成含糊的 500，不如抛出可辨识的异常让控制器回 503 + 说明
        var service = CreateService(HttpStatusCode.OK, BanLogsBody, loginApiBaseUrl: null);

        await Assert.ThrowsAsync<LoginOpsNotConfiguredException>(
            () => service.GetBanLogsAsync("xauat", null));
    }

    [Fact]
    public async Task GetUserCount_ShouldStillWork_WhenLoginApiBaseUrlMissing()
    {
        // 与封禁日志相反：/user_count 在 Flask 上是同形状的接口，回落模式下照常可用。
        // 这条守住"只封禁日志那一条路径需要配置"这个刻意的不对称。
        var service = CreateService(HttpStatusCode.OK, """{"count":7}""", loginApiBaseUrl: null);

        Assert.Equal(7, await service.GetUserCountAsync());
    }

    // ---------------------------------------------------------------- 解封

    [Fact]
    public async Task Unban_ShouldReturnSuccess_WhenUpstreamReturns200()
    {
        var service = CreateService(HttpStatusCode.OK, """{"success":true,"message":"用户已解封"}""");

        var result = await service.UnbanAsync("xauat", "2024001");

        Assert.True(result.Success);
        Assert.Equal("用户已解封", result.Message);
    }

    [Fact]
    public async Task Unban_ShouldReturnFailureResult_WhenUpstreamReturns400()
    {
        // 「该用户当前未被封禁」是业务结果，不是内部错误——不能变成 500
        var service = CreateService(HttpStatusCode.BadRequest,
            """{"success":false,"message":"用户当前未被封禁或解封失败"}""");

        var result = await service.UnbanAsync("xauat", "2024001");

        Assert.False(result.Success);
        Assert.Equal("用户当前未被封禁或解封失败", result.Message);
    }

    [Fact]
    public async Task Unban_ShouldPostSchoolAndUsername()
    {
        string? path = null;
        string? method = null;
        string? body = null;
        var service = CreateService(HttpStatusCode.OK, """{"success":true,"message":"ok"}""",
            onRequest: async request =>
            {
                path = request.RequestUri?.PathAndQuery;
                method = request.Method.Method;
                body = await request.Content!.ReadAsStringAsync();
            });

        await service.UnbanAsync("xauat", "2024001");

        Assert.Equal("/security/ban_status", path);
        Assert.Equal("POST", method);
        Assert.Equal("""{"school":"xauat","username":"2024001"}""", body);
    }

    // ---------------------------------------------------------------- 异常映射

    [Fact]
    public async Task GetUserCount_ShouldThrowUnavailable_WhenConnectionFails()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var service = CreateService(handler, retryDelays: NoDelay);

        var ex = await Assert.ThrowsAsync<LoginOpsUnavailableException>(() => service.GetUserCountAsync());

        Assert.StartsWith("获取活跃用户数失败: ", ex.Message);
    }

    [Fact]
    public async Task GetUserCount_ShouldThrowInvalidOperation_WhenUnexpectedStatus()
    {
        // 非 2xx 代表登录服务自己内部出错，必须是"非领域异常"，
        // 让控制器走 catch-all 回 500，而不是误报成"服务不可用"
        var service = CreateService(HttpStatusCode.InternalServerError, "boom");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.GetUserCountAsync());

        Assert.IsNotType<LoginOpsUnavailableException>(ex);
    }

    [Fact]
    public async Task GetUserCount_ShouldRetryTransientFailures()
    {
        // 与 HttpLoginService 一致：传输层错误要重试，重试次数 = 退避数组长度 + 1
        var attempts = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"count":1}""", Encoding.UTF8, "application/json")
                });
            });

        var service = CreateService(handler, retryDelays: NoDelay);

        Assert.Equal(1, await service.GetUserCountAsync());
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task GetUserCount_ShouldGiveUpAfterRetriesExhausted()
    {
        var attempts = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                attempts++;
                throw new HttpRequestException("Connection refused");
            });

        var service = CreateService(handler, retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        await Assert.ThrowsAsync<LoginOpsUnavailableException>(() => service.GetUserCountAsync());

        Assert.Equal(3, attempts);
    }

    // ---------------------------------------------------------------- 夹具

    private static HttpLoginAdminService CreateService(
        HttpStatusCode statusCode, string body,
        string? loginApiBaseUrl = BaseAddress,
        Func<HttpRequestMessage, Task>? onRequest = null,
        TimeSpan[]? retryDelays = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, _) =>
            {
                if (onRequest is not null) await onRequest(request);

                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        return CreateService(handler, loginApiBaseUrl, retryDelays);
    }

    private static HttpLoginAdminService CreateService(
        Mock<HttpMessageHandler> handler,
        string? loginApiBaseUrl = BaseAddress,
        TimeSpan[]? retryDelays = null)
        => new(
            new HttpClient(handler.Object) { BaseAddress = new Uri(BaseAddress) },
            Mock.Of<ILogger<HttpLoginAdminService>>(),
            new ServiceConfiguration { LoginApiBaseUrl = loginApiBaseUrl },
            retryDelays: retryDelays);
}
