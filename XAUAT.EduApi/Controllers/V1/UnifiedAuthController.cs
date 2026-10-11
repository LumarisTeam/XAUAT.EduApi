using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;

namespace XAUAT.EduApi.Controllers.V1;

/// <summary>
/// 通用统一身份认证登录：<b>只填目标地址 + 学号 + 密码</b>。
/// <para>
/// 与 <c>POST v1/login</c> 的分工：<c>v1/login</c> 是 XAUAT 教务系统的专用入口
/// （带缓存、限流、封禁，返回教务 cookie）；本接口不绑定任何学校，
/// 只要目标地址背后是 CAS/金智 UAAP 就能用，代价是没有缓存/封禁那套编排，
/// 每次调用都会真的去登录一次。
/// </para>
/// <para>
/// 安全性：<c>url</c> 由调用方指定，为防止 SSRF，服务端做了域名白名单
/// （默认 <c>*.edu.cn</c>，可用 <c>UNIFIED_AUTH_ALLOWED_HOSTS</c> 追加）与
/// 建连前的内网/保留地址拦截，并复用 <c>EduCrawler</c> 并发限流。
/// </para>
/// </summary>
[ApiController]
[Route("v1/login/unified")]
[Produces("application/json")]
[Consumes("application/json")]
[EnableRateLimiting("EduCrawler")]
public class UnifiedAuthController(
    IUnifiedAuthService unifiedAuthService,
    ILogger<UnifiedAuthController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<UnifiedAuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ApiResponse<UnifiedAuthResponse>>> Login([FromBody] UnifiedAuthRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(ErrorResponse(ApiCodes.ParamError, Message(ApiMessageKey.UnifiedAuthParamRequired)));
        }

        try
        {
            var result = await unifiedAuthService.LoginAsync(request, HttpContext.RequestAborted);
            return Ok(SuccessResponse(result));
        }
        catch (UnifiedAuthRequestException ex)
        {
            // 具体原因（哪个 host、为什么）只进日志：它可能暴露内网拓扑
            logger.LogWarning("通用统一认证请求被拒：{Detail}", ex.Message);
            return BadRequest(ErrorResponse(ApiCodes.ParamError, Message(ex.MessageKey)));
        }
        catch (LoginFailedException ex)
        {
            logger.LogWarning("通用统一认证凭据错误：{Message}", ex.Message);
            return Unauthorized(ErrorResponse(ApiCodes.WrongCredentials,
                Message(ApiMessageKey.InvalidUsernameOrPassword)));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("通用统一认证上游失败：{Message}", ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.EduSystemAccessFailed)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "通用统一认证异常");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.InternalServerError)));
        }
    }
}
