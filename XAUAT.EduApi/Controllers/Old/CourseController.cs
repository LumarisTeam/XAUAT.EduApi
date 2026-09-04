using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 课程控制器
/// 提供学生课程信息查询功能
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
[Consumes("application/json")]
[ServiceFilter(typeof(EduCrawlerRateLimitFilter))]
[EnableRateLimiting("EduCrawler")]
public class CourseController(
    ILogger<CourseController> logger,
    ICourseService courseService,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : LanguageAwareControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 获取学生课程信息
    /// 根据学生ID获取当前学期的课程列表
    /// </summary>
    /// <param name="studentId">学生ID，多个ID用逗号分隔</param>
    /// <returns>课程列表，包含课程名称、教师、上课时间等信息</returns>
    /// <response code="200">成功获取课程信息</response>
    /// <response code="400">参数错误</response>
    /// <response code="404">操作无效，未找到课程信息</response>
    /// <response code="500">服务器内部错误</response>
    /// <response code="502">HTTP请求错误，教务处系统访问失败</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Course?studentId=123456
    /// Header:
    /// x-language: zh-CN (also accepts legacy alias: zh)
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(CourseResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CourseErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(CourseErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(CourseErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(CourseErrorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(CourseErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<CourseResultResponse>> GetCourse(string studentId)
    {
        try
        {
            logger.LogInformation("开始获取课程信息");
            var cookie = Request.GetEduAuthCookie();

            var courses = await courseService.GetCoursesAsync(studentId, cookie, Language);

            // 返回处理后的课程数据
            return Ok(new CourseResultResponse
            {
                Success = true,
                Data = courses,
                ExpirationTime = DateTime.Now.AddDays(7)
            });
        }
        catch (Exceptions.StudentCooldownException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (Exceptions.UnAuthenticationError)
        {
            return Unauthorized(new CourseErrorResponse { Success = false, Message = Message(ApiMessageKey.AuthenticationFailed) });
        }
        catch (ArgumentNullException ex)
        {
            logger.LogWarning(ex, "参数错误");
            return BadRequest(new CourseErrorResponse { Success = false, Message = ex.Message });
        }
        catch (Exceptions.RateLimitException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "HTTP请求错误");
            return StatusCode(502, new CourseErrorResponse { Success = false, Message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "操作无效");
            return NotFound(new CourseErrorResponse { Success = false, Message = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取课程时发生错误");
            return StatusCode(500, new CourseErrorResponse { Success = false, Message = Message(ApiMessageKey.InternalServerError) });
        }
    }
}
