using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;

namespace XAUAT.EduApi.Controllers.V1;

[ApiController]
[Route("v1/login")]
[Produces("application/json")]
[Consumes("application/json")]
public class LoginController(
    ILoginService loginService,
    ILogger<LoginController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(ErrorResponse(ApiCodes.ParamError,
                Message(ApiMessageKey.UsernameOrPasswordRequired)));
        }

        try
        {
            var result = await loginService.LoginAsync(request.Username, request.Password, Language);
            return Ok(SuccessResponse(result));
        }
        catch (LoginFailedException)
        {
            return Unauthorized(ErrorResponse(ApiCodes.WrongCredentials,
                Message(ApiMessageKey.InvalidUsernameOrPassword)));
        }
        catch (RateLimitException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (AccountBannedException ex)
        {
            logger.LogWarning("用户 {Username} 已被登录服务封禁，理由 {Reason}，解封时刻 {UnbanAt}",
                request.Username, ex.Reason, ex.UnbanAt);
            return RateLimited(ApiMessageKey.AccountBanned, ex.RetryAfterSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "用户 {Username} 登录失败", request.Username);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.EduSystemAccessFailed)));
        }
    }
}
