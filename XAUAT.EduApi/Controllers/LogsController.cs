using Microsoft.AspNetCore.Mvc;
using Serilog.Events;
using XAUAT.EduApi.Logging;

namespace XAUAT.EduApi.Controllers;

[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public sealed class LogsController(ILogStore logStore) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(LogPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<LogPageResponse> GetLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? level = null,
        [FromQuery] string? search = null)
    {
        if (!IsAuthorized())
        {
            return Unauthorized();
        }

        if (page < 1 || pageSize is < 1 or > 200)
        {
            return BadRequest("page 必须大于等于 1，pageSize 必须在 1-200 之间");
        }

        LogEventLevel? minimumLevel = null;
        var parsedLevel = LogEventLevel.Information;
        if (!string.IsNullOrWhiteSpace(level) && !Enum.TryParse(level, true, out parsedLevel))
        {
            return BadRequest("level 必须是 Trace、Debug、Information、Warning、Error 或 Fatal");
        }
        else if (!string.IsNullOrWhiteSpace(level))
        {
            minimumLevel = parsedLevel;
        }

        var total = logStore.Count(minimumLevel, search);
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return Ok(new LogPageResponse(page, pageSize, total, totalPages, logStore.Query(page, pageSize, minimumLevel, search)));
    }

    private bool IsAuthorized()
    {
        var expected = Environment.GetEnvironmentVariable("LOG_VIEW_TOKEN");
        if (string.IsNullOrWhiteSpace(expected)) return true;

        var supplied = Request.Headers["X-Log-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied) && Request.Headers.Authorization.FirstOrDefault() is { } authorization &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            supplied = authorization[7..];
        }

        return !string.IsNullOrEmpty(supplied) &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(expected.Trim()),
                   System.Text.Encoding.UTF8.GetBytes(supplied.Trim()));
    }
}

public sealed record LogPageResponse(
    int Page,
    int PageSize,
    int Total,
    int TotalPages,
    IReadOnlyList<LogEntry> Items);
