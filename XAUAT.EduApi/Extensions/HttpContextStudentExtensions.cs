using System.Security.Cryptography;
using System.Text;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Extensions;

public static class HttpContextStudentExtensions
{
    private const string ResolvedStudentIdsKey = "__ResolvedStudentIds";
    private const string ResolvedRateLimitKeys = "__ResolvedRateLimitKeys";

    public static void SetResolvedStudentIds(this HttpContext context, IReadOnlyCollection<string> studentIds)
    {
        context.Items[ResolvedStudentIdsKey] = studentIds;
    }

    public static IReadOnlyCollection<string> GetResolvedStudentIds(this HttpContext context)
    {
        return context.Items.TryGetValue(ResolvedStudentIdsKey, out var value) &&
               value is IReadOnlyCollection<string> studentIds
            ? studentIds
            : [];
    }

    public static void SetResolvedRateLimitKeys(this HttpContext context, IReadOnlyCollection<string> keys)
    {
        context.Items[ResolvedRateLimitKeys] = keys;
    }

    public static IReadOnlyCollection<string> GetResolvedRateLimitKeys(this HttpContext context)
    {
        return context.Items.TryGetValue(ResolvedRateLimitKeys, out var value) &&
               value is IReadOnlyCollection<string> keys
            ? keys
            : [];
    }

    public static int? GetRetryAfterSeconds(this HttpContext context, IStudentRateLimitState rateLimitState)
    {
        var keys = context.GetResolvedRateLimitKeys();
        if (keys.Count == 0)
        {
            keys = context.GetResolvedStudentIds();
        }

        var blockedUntil = keys
            .Select(key =>
                rateLimitState.TryGetBlockedUntil(key, out var currentBlockedUntil)
                    ? currentBlockedUntil
                    : (DateTimeOffset?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();

        if (blockedUntil == default)
        {
            return null;
        }

        return Math.Max(1, (int)Math.Ceiling((blockedUntil - DateTimeOffset.UtcNow).TotalSeconds));
    }

    public static string[] ParseStudentIds(string? rawStudentIds)
    {
        if (string.IsNullOrWhiteSpace(rawStudentIds))
        {
            return [];
        }

        return rawStudentIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(studentId => !string.IsNullOrWhiteSpace(studentId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static string? CreateCookieRateLimitIdentity(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(cookie.Trim()));
        return $"cookie:{Convert.ToHexString(bytes)}";
    }

    public static string GetRateLimitPath(this HttpRequest request)
    {
        var path = request.Path.Value;
        return string.IsNullOrWhiteSpace(path)
            ? "/"
            : path.Trim().ToLowerInvariant();
    }

    public static string[] GetRequestStudentIds(this HttpRequest request)
    {
        var identities = new List<string>();

        if (request.Query.TryGetValue("studentId", out var studentIds))
        {
            foreach (var studentId in studentIds)
            {
                identities.AddRange(ParseStudentIds(studentId));
            }
        }

        if (request.Query.TryGetValue("id", out var ids))
        {
            foreach (var id in ids)
            {
                identities.AddRange(ParseStudentIds(id));
            }
        }

        return identities
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static string CreateRequestRateLimitPartitionKey(this HttpRequest request)
    {
        var path = request.GetRateLimitPath();
        var cookieIdentity = CreateCookieRateLimitIdentity(request.GetEduAuthCookie()) ?? "cookie:none";
        var studentIds = request.GetRequestStudentIds();
        var studentSegment = studentIds.Length == 0
            ? "student:none"
            : $"student:{string.Join(",", studentIds.OrderBy(x => x, StringComparer.Ordinal))}";

        return $"{path}|{studentSegment}|{cookieIdentity}";
    }

    private const string RateLimitKeyPrefix = "rate_limit";

    public static string[] CreateRateLimitStateKeys(IEnumerable<string?>? studentIds)
    {
        return (studentIds ?? [])
            .Where(studentId => !string.IsNullOrWhiteSpace(studentId))
            .Select(studentId => studentId!.Trim())
            .Where(studentId => !studentId.StartsWith("cookie:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(studentId => $"{RateLimitKeyPrefix}:{studentId}")
            .ToArray();
    }
}
