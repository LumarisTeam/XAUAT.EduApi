using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Login.Mfa;

namespace XAUAT.EduApi.Controllers.V1;

/// <summary>二次认证第二步：提交 OTP/动态码，或轮询扫码状态。</summary>
[ApiController]
[Route("v1/login/mfa")]
[Produces("application/json")]
[Consumes("application/json")]
[EnableRateLimiting("EduCrawler")]
public sealed class MfaController(
    IMfaLoginService mfaLoginService,
    ILogger<MfaController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Complete([FromBody] MfaSubmitRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MfaSessionId))
        {
            return BadRequest(ErrorResponse(ApiCodes.ParamError, Message(ApiMessageKey.MfaSessionExpired)));
        }

        try
        {
            var result = await mfaLoginService.CompleteAsync(request, HttpContext.RequestAborted);
            return Ok(SuccessResponse(result));
        }
        catch (MfaBusyException ex)
        {
            return RateLimited(ApiMessageKey.MfaBusy, ex.RetryAfterSeconds);
        }
        catch (MfaSessionExpiredException)
        {
            return StatusCode(StatusCodes.Status410Gone,
                ErrorResponse(ApiCodes.MfaSessionExpired, Message(ApiMessageKey.MfaSessionExpired)));
        }
        catch (MfaFailedException)
        {
            return Unauthorized(ErrorResponse(ApiCodes.MfaInvalidCode, Message(ApiMessageKey.MfaInvalidCode)));
        }
        catch (MfaMethodUnsupportedException)
        {
            return BadRequest(ErrorResponse(ApiCodes.MfaMethodUnsupported,
                Message(ApiMessageKey.MfaMethodUnsupported)));
        }
        catch (PasswordExpiredException)
        {
            return StatusCode(StatusCodes.Status428PreconditionRequired,
                ErrorResponse(ApiCodes.PasswordExpired, Message(ApiMessageKey.PasswordExpired)));
        }
        catch (MfaProtocolException ex)
        {
            logger.LogError(ex, "MFA 协议解析失败");
            return StatusCode(StatusCodes.Status502BadGateway,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.MfaProtocolError)));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "MFA 上游失败");
            return StatusCode(StatusCodes.Status502BadGateway,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.EduSystemAccessFailed)));
        }
        catch (LoginFailedException)
        {
            return Unauthorized(ErrorResponse(ApiCodes.WrongCredentials,
                Message(ApiMessageKey.InvalidUsernameOrPassword)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MFA 完成失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.InternalServerError)));
        }
    }

    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<MfaStatusResponse>>> Status([FromQuery] string mfaSessionId)
    {
        if (string.IsNullOrWhiteSpace(mfaSessionId))
        {
            return BadRequest(ErrorResponse(ApiCodes.ParamError, Message(ApiMessageKey.MfaSessionExpired)));
        }

        try
        {
            var result = await mfaLoginService.PollQrAsync(mfaSessionId, HttpContext.RequestAborted);
            if (result.Status == "success")
            {
                return Ok(SuccessResponse(result));
            }

            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted, new ApiResponse<MfaStatusResponse>
            {
                Data = result,
                Code = ApiCodes.MfaRequired,
                Message = Message(ApiMessageKey.MfaRequired)
            });
        }
        catch (MfaBusyException ex)
        {
            return RateLimited(ApiMessageKey.MfaBusy, ex.RetryAfterSeconds);
        }
        catch (MfaSessionExpiredException)
        {
            return StatusCode(StatusCodes.Status410Gone,
                ErrorResponse(ApiCodes.MfaSessionExpired, Message(ApiMessageKey.MfaSessionExpired)));
        }
        catch (MfaFailedException)
        {
            return Unauthorized(ErrorResponse(ApiCodes.MfaInvalidCode, Message(ApiMessageKey.MfaInvalidCode)));
        }
        catch (PasswordExpiredException)
        {
            return StatusCode(StatusCodes.Status428PreconditionRequired,
                ErrorResponse(ApiCodes.PasswordExpired, Message(ApiMessageKey.PasswordExpired)));
        }
        catch (MfaProtocolException ex)
        {
            logger.LogError(ex, "MFA 扫码协议解析失败");
            return StatusCode(StatusCodes.Status502BadGateway,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.MfaProtocolError)));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "MFA 扫码上游失败");
            return StatusCode(StatusCodes.Status502BadGateway,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.EduSystemAccessFailed)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MFA 扫码状态查询失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.UpstreamError, Message(ApiMessageKey.InternalServerError)));
        }
    }
}
