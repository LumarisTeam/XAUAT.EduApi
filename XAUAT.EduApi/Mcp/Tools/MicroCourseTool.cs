using System.ComponentModel;
using ModelContextProtocol.Server;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Mcp.Tools;

[McpServerToolType]
public class MicroCourseTool(IMicroCourseService microCourseService)
{
    [McpServerTool, Description("获取微专业课表。需要提供学生学号和教务系统 Cookie。")]
    public async Task<object> GetMicroCourse(
        [Description("教务系统认证 Cookie（__pstsid__ 开头）")] string cookie,
        [Description("学生学号，多个学号用逗号分隔；不传时用 Cookie 服务自动解析")] string? studentId = null,
        [Description("学期ID，可选，不传时自动获取当前学期")] string? semesterId = null)
    {
        var courses = await microCourseService.GetMicroCoursesAsync(studentId, cookie, semesterId);
        return courses;
    }
}
