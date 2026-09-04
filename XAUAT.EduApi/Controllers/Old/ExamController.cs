using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 考试控制器
/// 提供学生考试安排查询功能
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
[Consumes("application/json")]
[ServiceFilter(typeof(EduCrawlerRateLimitFilter))]
[EnableRateLimiting("EduCrawler")]
public class ExamController(
    IExamService examService,
    ILogger<ExamController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer) : LanguageAwareControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 获取考试安排
    /// 根据学生ID获取考试安排信息
    /// </summary>
    /// <param name="studentId">学生ID，多个ID用逗号分隔</param>
    /// <returns>考试安排列表</returns>
    /// <response code="200">成功获取考试安排</response>
    /// <response code="401">认证失败，需要重新登录</response>
    /// <response code="500">服务器内部错误，获取考试安排失败</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Exam?studentId=123456
    /// 
    /// Header:
    /// x-language: zh-CN (also accepts legacy alias: zh)
    /// 
    /// 注意：
    /// 需要在Headers中添加xauat的Cookie信息，或者直接使用Cookie头
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ExamResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ExamResponse>> GetExamArrangements(string? studentId)
    {
        try
        {
            var cookie = Request.GetEduAuthCookie();

            var requestStudentIds = HttpContext.GetResolvedStudentIds();
            var result = await examService.GetExamArrangementsAsync(cookie, studentId, Language, requestStudentIds);
            return Ok(result);
        }
        catch (Exceptions.StudentCooldownException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (Exceptions.UnAuthenticationError)
        {
            return Unauthorized(Message(ApiMessageKey.AuthenticationFailed));
        }
        catch (Exceptions.RateLimitException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get exam arrangements");
            return StatusCode(500, Message(ApiMessageKey.ExamFetchFailed));
        }
    }
}
