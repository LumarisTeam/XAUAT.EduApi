using EduApi.Data.Models;
using Microsoft.Extensions.Options;
using XAUAT.EduApi.Configuration;

namespace XAUAT.EduApi.Services;

public interface ITestAccountResolver
{
    bool IsTestLogin(string username, string password);
    bool IsTestAccount(string? cookie = null, string? studentId = null);
    LoginResponse CreateLoginResponse();
}

public class TestAccountResolver(IOptions<TestAccountOptions> options) : ITestAccountResolver
{
    private readonly TestAccountOptions _options = options.Value;

    public bool IsTestLogin(string username, string password)
    {
        return _options.Enabled &&
               !string.IsNullOrWhiteSpace(_options.Username) &&
               !string.IsNullOrWhiteSpace(_options.Password) &&
               string.Equals(username, _options.Username, StringComparison.Ordinal) &&
               string.Equals(password, _options.Password, StringComparison.Ordinal);
    }

    public bool IsTestAccount(string? cookie = null, string? studentId = null)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(cookie) &&
            !string.IsNullOrWhiteSpace(_options.CookieMarker) &&
            cookie.Contains(_options.CookieMarker, StringComparison.Ordinal))
        {
            return true;
        }

        return MatchesStudentIdentity(studentId);
    }

    public LoginResponse CreateLoginResponse()
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("测试账号未启用");
        }

        return new LoginResponse
        {
            Success = true,
            StudentId = _options.StudentId,
            Cookie = $"__pstsid__={_options.CookieMarker}; SESSION={_options.CookieMarker};"
        };
    }

    private bool MatchesStudentIdentity(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue) || string.IsNullOrWhiteSpace(_options.StudentId))
        {
            return false;
        }

        return rawValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => string.Equals(item, _options.StudentId, StringComparison.Ordinal));
    }
}
