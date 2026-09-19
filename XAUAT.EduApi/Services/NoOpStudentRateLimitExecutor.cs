using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

public sealed class NoOpStudentRateLimitExecutor : IStudentRateLimitExecutor
{
    public static NoOpStudentRateLimitExecutor Instance { get; } = new();

    public Task<T> ExecuteAsync<T>(IEnumerable<string?> studentIds, Func<Task<T>> action)
    {
        return action();
    }

    public Task ExecuteAsync(IEnumerable<string?> studentIds, Func<Task> action)
    {
        return action();
    }
}
