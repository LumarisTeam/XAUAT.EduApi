using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 校园导航
/// </summary>
/// <param name="service"></param>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class SchoolNavController(ISchoolNavService service) : ControllerBase
{
    /// <summary>
    /// 获取校园导航
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [ProducesResponseType(typeof(List<CategoryModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<List<CategoryModel>>> GetCategoriesAsync()
    {
        return await service.GetCategoriesAsync();
    }
}