using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 培养方案控制器
/// 提供专业培养计划查询功能，获取学生所属专业的课程培养方案
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
[ServiceFilter(typeof(EduCrawlerRateLimitFilter))]
[EnableRateLimiting("EduCrawler")]
public class ProgramController(
    ILogger<ProgramController> logger,
    IProgramService program,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer) : LanguageAwareControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 获取所有培养方案
    /// 从教务处系统获取指定专业的所有培养方案，并支持按名称过滤
    /// </summary>
    /// <param name="id">专业ID</param>
    /// <param name="name">可选，课程名称过滤条件</param>
    /// <returns>培养方案列表，包含课程名称、学分、课程类型等信息</returns>
    /// <response code="200">成功获取培养方案列表</response>
    /// <response code="401">未授权，需要有效的身份认证Cookie</response>
    /// <response code="500">服务器内部错误，无法获取培养方案</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Program?id=123456
    /// Header:
    /// x-language: zh-CN (also accepts legacy alias: zh)
    /// 
    /// 按名称过滤：
    /// GET /Program?id=123456& name=计算机
    /// 
    /// 请求头：
    /// Cookie: YOUR_AUTH_COOKIE
    /// 
    /// 或使用自定义请求头：
    /// xauat: YOUR_AUTH_COOKIE
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(List<PlanCourse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<List<PlanCourse>>> GetAllTrainProgram(string id, string? name)
    {
        try
        {
            var cookie = Request.GetEduAuthCookie();

            var result = await program.GetAllTrainProgram(cookie, id, Language);
            if (!string.IsNullOrEmpty(name))
            {
                result = result.Where(x => x.Name.Contains(name)).ToList();
            }

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
            logger.LogError(ex, "Failed to get training program");
            return StatusCode(500, Message(ApiMessageKey.ProgramFetchFailed));
        }
    }

    /// <summary>
    /// 获取培养方案字典
    /// 从教务处系统获取指定专业的培养方案字典数据
    /// </summary>
    /// <param name="id">专业ID</param>
    /// <returns>培养方案字典，包含课程分类和对应课程列表</returns>
    /// <response code="200">成功获取培养方案字典</response>
    /// <response code="401">未授权，需要有效的身份认证Cookie</response>
    /// <response code="500">服务器内部错误，无法获取培养方案字典</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Program/GetDic?id=123456
    /// Header:
    /// x-language: zh-CN (also accepts legacy alias: zh)
    /// 
    /// 请求头：
    /// Cookie: YOUR_AUTH_COOKIE
    /// 
    /// 或使用自定义请求头：
    /// xauat: YOUR_AUTH_COOKIE
    /// </remarks>
    [HttpGet("GetDic")]
    [ProducesResponseType(typeof(Dictionary<string, List<PlanCourse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<Dictionary<string, List<PlanCourse>>>> GetAllTrainPrograms(string id)
    {
        try
        {
            var cookie = Request.GetEduAuthCookie();

            var result = await program.GetAllTrainPrograms(cookie, id, Language);
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
            logger.LogError(ex, "Failed to get training program");
            return StatusCode(500, Message(ApiMessageKey.ProgramFetchFailed));
        }
    }
}
