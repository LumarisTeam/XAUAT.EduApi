using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

[ApiController]
[Route("v1/payment")]
[Produces("application/json")]
[Consumes("application/json")]
public class PaymentController(
    IPaymentService paymentService,
    ILogger<PaymentController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<string>>> Login(string id, [FromQuery] string password = "202411")
    {
        if (string.IsNullOrEmpty(password))
        {
            password = "202411";
        }

        try
        {
            logger.LogInformation("Login with card number {id}", id);
            var result = await paymentService.Login(id, password, Language);
            logger.LogInformation("Login result: {result}", result);
            return Ok(SuccessResponse(result));
        }
        catch (PaymentServiceException ex)
        {
            logger.LogError(ex, "Payment service error during login for card {id}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ErrorResponse(ApiCodes.UpstreamError, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during login for card {id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, Message(ApiMessageKey.PaymentLoginUnknownError)));
        }
    }

    [HttpGet("{id}/turnover")]
    [ProducesResponseType(typeof(ApiResponse<PaymentTurnoverResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<PaymentTurnoverResult>>> GetTurnover(string id,
        [FromQuery] string password = "202411")
    {
        if (string.IsNullOrEmpty(password))
        {
            password = "202411";
        }

        try
        {
            logger.LogInformation("Get turnover with card number {id}", id);
            var result = await paymentService.GetTurnoverAsync(id, password, Language);
            logger.LogInformation("Get turnover result: {result}", result);
            return Ok(SuccessResponse(new PaymentTurnoverResult
            {
                Records = result.Records,
                Balance = result.Total
            }));
        }
        catch (PaymentServiceException ex)
        {
            logger.LogError(ex, "Payment service error during fetching turnover for card {id}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ErrorResponse(ApiCodes.UpstreamError, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during fetching turnover for card {id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, Message(ApiMessageKey.PaymentTurnoverUnknownError)));
        }
    }
}