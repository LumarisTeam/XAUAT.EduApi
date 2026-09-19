namespace XAUAT.EduApi.Interfaces;

public interface IStudentRateLimitExecutor
{
    Task<T> ExecuteAsync<T>(IEnumerable<string?> studentIds, Func<Task<T>> action);
    Task ExecuteAsync(IEnumerable<string?> studentIds, Func<Task> action);
}
