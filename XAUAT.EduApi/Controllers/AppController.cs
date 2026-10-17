using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace XAUAT.EduApi.Controllers;

/// <summary>
/// 应用更新控制器
/// 提供移动端应用版本检测和更新功能
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class AppController(IHttpClientFactory httpClientFactory)
    : ControllerBase
{
    /// <summary>
    /// 获取最新应用版本
    /// 代理发布平台的最新版本接口，返回 Gitee Release 兼容的裸数组
    /// </summary>
    /// <returns>最新版本信息数组，字段包含 tag_name、name、body、assets 等</returns>
    /// <response code="200">成功获取最新版本信息</response>
    /// <response code="401">未授权</response>
    /// <response code="500">服务器内部错误，无法连接到发布平台</response>
    /// <remarks>
    /// 同时挂载在 /App 与 /App/GetTag 上。
    /// 响应必须是裸数组：App 端直接按 []ReleaseInfo 反序列化，
    /// 包成 {code,message,data} 会导致客户端更新检查静默失效。
    /// </remarks>
    [HttpGet]
    [HttpGet("GetTag")]
    [ProducesResponseType(typeof(List<ReleaseInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTagModel()
    {
        using var client = httpClientFactory.CreateClient();
        var response = await client.GetAsync(
            "https://appapi.xauat.site/api/App/5f278ffc-5a70-4805-a6bf-0543040981a8/latest?channelId=9e1a198a-a0c2-4017-b492-f2d0e5bee437");

        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, "获取标签失败");
        }

        var jsonString = await response.Content.ReadAsStringAsync();

        var obj = JObject.Parse(jsonString);

        var result = new List<ReleaseInfo>
        {
            new()
            {
                TagName = obj["releaseId"]?.ToObject<string>(),
                Name = obj["releaseId"]?.ToObject<string>(),
                Body = obj["context"]?.ToObject<string>(),
                Assets =
                [
                    new AssetInfo()
                    {
                        Name = obj["softs"]?[0]?["name"]?.ToObject<string>(),
                        BrowserDownloadUrl = obj["softs"]?[0]?["softUrl"]?.ToObject<string>()
                    }
                ]
            }
        };


        return Ok(result);
    }
}
