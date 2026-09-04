using EduApi.Data.Models;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using XAUAT.EduApi.Controllers.Old;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

[ApiController]
[Route("v1/info")]
[Produces("application/json")]
public class InfoController(
    IHttpClientFactory httpClientFactory,
    ILogger<InfoController> logger,
    IInfoService info,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer,
    ITestAccountResolver? testAccountResolver = null,
    ITestDataProvider? testDataProvider = null)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    [HttpGet("Completion")]
    [ProducesResponseType(typeof(ApiResponse<List<StudyModule>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status502BadGateway)]
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
                return Ok(SuccessListResponse(testData.ToList()));
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

            return Ok(SuccessListResponse(data.ToList()));
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
        catch (Exceptions.RateLimitException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取学业进度时出错");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.DataFetchFailed, Message(ApiMessageKey.CompletionFetchFailed)));
        }
    }

    [HttpGet("Time")]
    [ProducesResponseType(typeof(ApiResponse<TimeModel>), StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<TimeModel>> GetTime()
    {
        return Ok(SuccessResponse(info.GetTime()));
    }
}
