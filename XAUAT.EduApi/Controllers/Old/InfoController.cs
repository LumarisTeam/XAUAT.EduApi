using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Newtonsoft.Json;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 信息查询控制器
/// 提供学业进度查询和系统时间获取功能
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class InfoController(
    IHttpClientFactory httpClientFactory,
    ILogger<CourseController> logger,
    IInfoService info,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer,
    ITestAccountResolver? testAccountResolver = null,
    ITestDataProvider? testDataProvider = null)
    : LanguageAwareControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 获取学业进度
    /// 从教务处系统获取学生的学业完成情况预览
    /// </summary>
    /// <returns>学业进度数据，包含已完成学分、待修学分等信息</returns>
    /// <response code="200">成功获取学业进度数据</response>
    /// <response code="401">未授权，需要有效的身份认证Cookie</response>
    /// <response code="500">服务器内部错误，无法连接到教务处系统</response>
    /// <response code="502">教务处系统响应错误</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Info/Completion
    /// x-language: zh-CN (also accepts legacy alias: zh)
    /// Cookie: YOUR_AUTH_COOKIE
    /// 
    /// 或使用自定义请求头：
    /// GET /Info/Completion
    /// xauat: YOUR_AUTH_COOKIE
    /// </remarks>
    [HttpGet("Completion")]
    [ProducesResponseType(typeof(StudyModule[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(string), StatusCodes.Status502BadGateway)]
    [ServiceFilter(typeof(EduCrawlerRateLimitFilter))]
    [EnableRateLimiting("EduCrawler")]
    public async Task<IActionResult> GetCompletion()
    {
        try
        {
            logger.LogInformation("开始抓取学业进度");
            var cookie = Request.GetEduAuthCookie();
            var studentIds = HttpContext.GetResolvedStudentIds();

            if (testAccountResolver?.IsTestAccount(cookie: cookie) == true)
            {
                logger.LogInformation("测试账号命中学业进度测试数据");
                var testData = await testDataProvider!.GetCompletionAsync();
                return Ok(testData);
            }

            var services = HttpContext.RequestServices;
            var rateLimitExecutor = (services is null ? null : services.GetService<IStudentRateLimitExecutor>())
                                    ?? NoOpStudentRateLimitExecutor.Instance;

            var data = await rateLimitExecutor
                .ExecuteAsync(studentIds, async () =>
                {
                    using var client = httpClientFactory.CreateClient();
                    client.SetRealisticHeaders();
                    client.Timeout = TimeSpan.FromSeconds(6);
                    client.DefaultRequestHeaders.Add("Cookie", cookie);
                    var response =
                        await client.GetAsync(
                            "https://swjw.xauat.edu.cn/student/ws/student/home-page/programCompletionPreview");
                    var content = await response.Content.ReadAsStringAsync();

                    content.ThrowIfAuthOrRateLimited();

                    return JsonConvert.DeserializeObject<StudyModule[]>(content) ?? [];
                });

            return Ok(data);
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
            logger.LogError(ex, "获取学业进度时出错");
            return StatusCode(500, Message(ApiMessageKey.CompletionFetchFailed));
        }
    }

    /// <summary>
    /// 获取系统时间
    /// 获取服务器当前时间信息
    /// </summary>
    /// <returns>系统时间信息，包含当前日期、时间、星期等</returns>
    /// <response code="200">成功获取系统时间</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Info/Time
    /// </remarks>
    [HttpGet("Time")]
    [ProducesResponseType(typeof(TimeModel), StatusCodes.Status200OK)]
    public ActionResult<TimeModel> GetTime()
    {
        return Ok(info.GetTime());
    }
}

public class CreditInfo
{
    /// <summary>
    /// 学分项名称
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// 实际学分
    /// </summary>
    [JsonPropertyName("actual")]
    public double Actual { get; set; }

    /// <summary>
    /// 满学分
    /// </summary>
    [JsonPropertyName("full")]
    public double Full { get; set; }
}

public class StudyModule
{
    /// <summary>
    /// 类型（如：主修）
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; }= "";

    /// <summary>
    /// 总学分信息
    /// </summary>
    [JsonPropertyName("total")]
    public CreditInfo Total { get; set; } = new();

    /// <summary>
    /// 各教学模块学分明细
    /// </summary>
    [JsonPropertyName("other")]
    public List<CreditInfo> Other { get; set; } = [];
}
