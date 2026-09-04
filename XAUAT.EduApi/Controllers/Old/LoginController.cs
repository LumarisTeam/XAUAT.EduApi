using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 登录控制器
/// 提供学生登录功能，获取教务处系统的Cookie
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
[Consumes("application/json")]
public class LoginController(
    ILoginService loginService,
    ILogger<LoginController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : LanguageAwareControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 学生登录
    /// 使用学号和密码登录教务处系统，获取访问Cookie
    /// </summary>
    /// <param name="request">登录请求，包含学号和密码</param>
    /// <returns>登录结果，包含学生ID和Cookie</returns>
    /// <response code="200">登录成功，返回学生ID和Cookie</response>
    /// <response code="400">参数错误，用户名或密码不能为空</response>
    /// <response code="401">登录失败，用户名或密码错误</response>
    /// <response code="500">服务器内部错误，教务处系统访问失败</response>
    /// <remarks>
    /// 示例请求：
    /// POST /Login
    /// Header:
    /// x-language: zh-CN (also accepts legacy alias: zh)
    /// {
    ///   "username": "123456",
    ///   "password": "password123"
    /// }
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(Message(ApiMessageKey.UsernameOrPasswordRequired));
            }

            var result = await loginService.LoginAsync(request.Username, request.Password, Language);
            return Ok(result);
        }
        catch (LoginFailedException)
        {
            return Unauthorized(Message(ApiMessageKey.InvalidUsernameOrPassword));
        }
        catch (RateLimitException)
        {
            return RateLimited(ApiMessageKey.EduSystemRateLimited);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "用户 {Username} 登录失败", request.Username);
            return StatusCode(500, Message(ApiMessageKey.EduSystemAccessFailed));
        }
    }
}
