using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

public class StudentRateLimitExecutor(
    IStudentRateLimitState rateLimitState) : IStudentRateLimitExecutor
{
    public async Task<T> ExecuteAsync<T>(IEnumerable<string?> studentIds, Func<Task<T>> action)
    {
        var normalizedKeys = NormalizeRateLimitKeys(studentIds);

        foreach (var rateLimitKey in normalizedKeys)
        {
            if (rateLimitState.TryGetBlockedUntil(rateLimitKey, out var blockedUntil))
            {
                throw new StudentCooldownException(rateLimitKey, blockedUntil);
            }
        }

        try
        {
            var result = await action();

            foreach (var rateLimitKey in normalizedKeys)
            {
                rateLimitState.MarkSuccess(rateLimitKey);
            }

            return result;
        }
        catch (RateLimitException)
        {
            foreach (var rateLimitKey in normalizedKeys)
            {
                rateLimitState.MarkRateLimited(rateLimitKey);
            }

            throw;
        }
    }

    public async Task ExecuteAsync(IEnumerable<string?> studentIds, Func<Task> action)
    {
        await ExecuteAsync(studentIds, async () =>
        {
            await action();
            return true;
        });
    }

    private static string[] NormalizeRateLimitKeys(IEnumerable<string?> studentIds)
    {
        return HttpContextStudentExtensions.CreateRateLimitStateKeys(studentIds);
    }
}
