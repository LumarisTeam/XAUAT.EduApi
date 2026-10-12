using System.Net;
using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 直连学校统一认证（authserver）的登录实现，对应 ILoginService。
/// 原 SSOLoginService 走 schedule.xauat.site 代理（当前返回 502），
/// 这里直连认证服务器，登录成功后用 CookieCodeService 换取学号（可能是 "68551,84795"）。
/// </summary>
public class DirectSsoLoginService : ILoginService
{
    private const string AuthServerUrl = "http://authserver.xauat.edu.cn/authserver/login";
    private const string ServiceUrl = "https://swjw.xauat.edu.cn/student/sso/login";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36";

    // 教务系统部分 Cookie 的 Path 是 /student，取 Cookie 时要用足够深的路径探测
    private static readonly Uri EduCookieProbeUri =
        new("https://swjw.xauat.edu.cn/student/for-std/precaution");

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _client;
    private readonly HttpClient _noRedirectClient;
    private readonly ICookieCodeService _cookieCodeService;
    private readonly ILogger<DirectSsoLoginService> _logger;
    private readonly ITestAccountResolver? _testAccountResolver;

    public DirectSsoLoginService(
        ICookieCodeService cookieCodeService,
        ILogger<DirectSsoLoginService> logger,
        ITestAccountResolver? testAccountResolver = null)
    {
        _cookieCodeService = cookieCodeService;
        _logger = logger;
        _testAccountResolver = testAccountResolver;
        _client = CreateClient(allowAutoRedirect: true);
        _noRedirectClient = CreateClient(allowAutoRedirect: false);
    }

    public async Task<LoginResponse> LoginAsync(string username, string password, string language = "zh")
    {
        if (_testAccountResolver?.IsTestLogin(username, password) == true)
        {
            _logger.LogInformation("用户 {Username} 命中测试账号登录", username);
            return _testAccountResolver.CreateLoginResponse();
        }

        var loginUrl = $"{AuthServerUrl}?service={Uri.EscapeDataString(ServiceUrl)}";
        var page = await _client.GetStringAsync(loginUrl);
        var fields = SsoLoginFields.Parse(page);

        var form = new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = SsoPasswordEncryptor.Encrypt(password, fields.ByIdOrName("pwdEncryptSalt")),
            ["lt"] = fields.ByIdOrName("lt") ?? "",
            ["execution"] = fields.ByIdOrName("execution") ?? "",
            ["_eventId"] = fields.ByIdOrName("_eventId") ?? "submit",
            ["captcha"] = "",
            ["cllt"] = "userNameLogin",
            ["dllt"] = "generalLogin",
            ["rememberMe"] = "true",
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await _noRedirectClient.PostAsync(loginUrl, content);
        var statusCode = (int)response.StatusCode;

        if (statusCode is 301 or 302 or 303 or 307 or 308)
        {
            if (response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(new Uri(loginUrl), location);
                using var landing = await _client.GetAsync(target);
                _ = await landing.Content.ReadAsStringAsync();
            }
        }
        else if (statusCode == 200)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (!text.Contains("退出", StringComparison.Ordinal)
                && !text.Contains("logout", StringComparison.OrdinalIgnoreCase))
            {
                throw new LoginFailedException();
            }
        }
        else if (statusCode is 401 or 403)
        {
            throw new LoginFailedException();
        }
        else
        {
            throw new HttpRequestException($"统一认证登录失败，状态码: {statusCode}");
        }

        var cookie = GetEduCookieHeader();
        if (string.IsNullOrWhiteSpace(cookie))
        {
            // 兜底：带着 CASTGC 再进一次统一认证，换取教务 Cookie
            using var retry = await _client.GetAsync(loginUrl);
            _ = await retry.Content.ReadAsStringAsync();
            cookie = GetEduCookieHeader();
        }

        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new LoginFailedException();
        }

        var studentId = await _cookieCodeService.GetCode(cookie);
        if (string.IsNullOrEmpty(studentId) || studentId == "/student/login")
        {
            throw new LoginFailedException();
        }

        _logger.LogInformation("用户 {Username} 登录成功，学号 {StudentId}", username, studentId);
        return new LoginResponse
        {
            Success = true,
            StudentId = studentId,
            Cookie = cookie,
        };
    }

    private HttpClient CreateClient(bool allowAutoRedirect)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = HttpTimeouts.Slow,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language",
            "zh-CN,zh;q=0.8,en-US;q=0.5,en;q=0.3");
        return client;
    }

    private string GetEduCookieHeader()
    {
        var cookies = _cookies
            .GetCookies(EduCookieProbeUri)
            .Cast<Cookie>()
            .Select(cookie => $"{cookie.Name}={cookie.Value}");
        return string.Join("; ", cookies);
    }
}
