using EduApi.Data.Models;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

/// <summary>
/// 微专业课表接口
/// 数据来源：/student/for-std/adminclass-course-table/print-data（XML）
/// </summary>
[ApiController]
[Route("v1/course/micro")]
[Produces("application/json")]
[Consumes("application/json")]
[ServiceFilter(typeof(EduCrawlerRateLimitFilter))]
[EnableRateLimiting("EduCrawler")]
public class MicroCourseController(
    ILogger<MicroCourseController> logger,
    IMicroCourseService microCourseService,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 获取微专业课表
    /// </summary>
    /// <param name="studentId">学生ID，多个ID用逗号分隔；不传时用 Cookie 服务自动解析</param>
    /// <param name="semesterId">学期ID，不传时自动获取当前学期</param>
    /// <param name="bizTypeId">培养层次ID，默认 2（本科）</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<CourseActivity>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ApiResponse<List<CourseActivity>>>> GetMicroCourse(
        string? studentId = null,
        string? semesterId = null,
        string bizTypeId = "2")
    {
        try
        {
            logger.LogInformation("开始获取微专业课表信息");
            var cookie = Request.GetEduAuthCookie();
            var courses = await microCourseService.GetMicroCoursesAsync(studentId, cookie, semesterId, bizTypeId, Language);
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
            logger.LogError(ex, "获取微专业课表时发生错误");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, Message(ApiMessageKey.InternalServerError)));
        }
    }
}
