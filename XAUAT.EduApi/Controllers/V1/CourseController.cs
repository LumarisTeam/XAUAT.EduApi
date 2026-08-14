using EduApi.Data.Models;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

[ApiController]
[Route("v1/course")]
[Produces("application/json")]
[Consumes("application/json")]
[ServiceFilter(typeof(EduCrawlerRateLimitFilter))]
[EnableRateLimiting("EduCrawler")]
public class CourseController(
    ILogger<CourseController> logger,
    ICourseService courseService,
    IScheduleTimeService scheduleTimeService,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<CourseActivity>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ApiResponse<List<CourseActivity>>>> GetCourse(string studentId)
    {
        try
        {
            logger.LogInformation("开始获取课程信息");
            var cookie = Request.GetEduAuthCookie();
            var courses = await courseService.GetCoursesAsync(studentId, cookie, Language);
            return Ok(SuccessListResponse(courses));
        }
        catch (Exceptions.StudentCooldownException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (Exceptions.UnAuthenticationError)
        {
            return Unauthorized(ErrorResponse(ApiCodes.AuthFailed,
                Message(ApiMessageKey.AuthenticationFailed)));
        }
        catch (ArgumentNullException ex)
        {
            logger.LogWarning(ex, "参数错误");
            return BadRequest(ErrorResponse(ApiCodes.ParamError, ex.Message));
        }
        catch (Exceptions.RateLimitException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "HTTP请求错误");
            return StatusCode(StatusCodes.Status502BadGateway,
                ErrorResponse(ApiCodes.UpstreamError, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "操作无效");
            return NotFound(ErrorResponse(ApiCodes.NotFound, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取课程时发生错误");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, Message(ApiMessageKey.InternalServerError)));
        }
    }

    [HttpGet("Calendar")]
    public ActionResult GetCalendarSubscription(string username, string password, string type = "webcal")
    {
        if (type != "webcal") type = "https";
        return Redirect($"{type}://schedule.xauat.site/class?school=xauat&username={username}&password={password}");
    }

    /// <summary>
    /// 获取所有校区和季节的课程时间表
    /// </summary>
    /// <returns>所有时间表数据</returns>
    /// <response code="200">成功获取时间表数据</response>
    /// <remarks>
    /// 示例请求：
    /// GET /ScheduleTime
    /// 
    /// 返回数据包含：
    /// - 草堂校区：通用时间表
    /// - 雁塔校区：冬季时间表（10月1日-次年5月1日）
    /// - 雁塔校区：夏季时间表（5月1日-10月1日）
    /// 
    /// 后端不做校区判断，前端根据课程的校区字段自行选择使用哪个时间表
    /// </remarks>
    [HttpGet("ScheduleTime")]
    [ProducesResponseType(typeof(List<ScheduleTimeModel>), StatusCodes.Status200OK)]
    public ActionResult<List<ScheduleTimeModel>> GetAllScheduleTables()
    {
        try
        {
            logger.LogInformation("开始获取所有校区和季节的课程时间表");
            var response = scheduleTimeService.GetScheduleTimeResponseAsync();
            return Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取课程时间表时发生错误");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }
}