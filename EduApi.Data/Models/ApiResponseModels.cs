namespace EduApi.Data.Models;

[Serializable]
public class LoginResponse
{
    public bool Success { get; set; }
    public string StudentId { get; set; } = "";
    public string Cookie { get; set; } = "";

    /// <summary>需要二次认证；此时 <see cref="Mfa"/> 非空，调用方应走 /v1/login/mfa。</summary>
    public bool MfaRequired { get; set; }

    /// <summary>二次认证挑战信息。</summary>
    public MfaChallenge? Mfa { get; set; }

    /// <summary>统一身份认证密码已过期，需用户自行改密。</summary>
    public bool PasswordExpired { get; set; }

    /// <summary>改密页面地址（浏览器打开）。</summary>
    public string? PasswordChangeUrl { get; set; }
}

[Serializable]
public class CourseResultResponse
{
    public bool Success { get; set; }
    public List<CourseActivity> Data { get; set; } = [];
    public DateTime ExpirationTime { get; set; }
}

[Serializable]
public class CourseErrorResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

[Serializable]
public class ErrorResponse
{
    public string error { get; set; } = "";
}

[Serializable]
public class ErrorWithMessageResponse : ErrorResponse
{
    public string message { get; set; } = "";
}

[Serializable]
public class RateLimitErrorResponse : ErrorResponse
{
    public string message { get; set; } = "";
    public int retryAfterSeconds { get; set; }
}
