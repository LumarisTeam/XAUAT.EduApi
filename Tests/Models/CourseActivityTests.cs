using System.Text.Json;
using EduApi.Data.Models;
using Newtonsoft.Json;

namespace XAUAT.EduApi.Tests.Models;

/// <summary>
/// 上游课程 JSON 的**显式 null** 必须被挡在领域代码之外。
/// <para>
/// 属性初始值（<c>= ""</c> / <c>= []</c>）只能挡"字段缺失"，挡不住
/// <c>"Campus": null</c>——两个序列化器都会把 null 照实写进属性，
/// 让声明为非空的类型真的变成 null。线上就因此出过 500：
/// <c>CalendarService.ResolveCampus</c> 对 null 的 Campus 调用 <c>Contains</c>。
/// </para>
/// </summary>
public class CourseActivityTests
{
    /// <summary>上游 <c>print-data</c> 里出现过的那种"字段在、值为 null"。</summary>
    private const string UpstreamJsonWithExplicitNulls = """
        {
          "studentTableVm": {
            "activities": [
              {
                "lessonId": null,
                "courseName": null,
                "courseCode": null,
                "credits": null,
                "campus": null,
                "room": null,
                "teachers": null,
                "weekIndexes": null,
                "weekday": 1,
                "startUnit": 1,
                "endUnit": 2
              }
            ]
          }
        }
        """;

    private static void AssertNeverNull(CourseActivity activity)
    {
        Assert.Equal("", activity.Campus);
        Assert.Equal("", activity.Room);
        Assert.Equal("", activity.CourseName);
        Assert.Equal("", activity.CourseCode);
        Assert.Equal("", activity.Credits);
        Assert.Equal("", activity.LessonId);
        Assert.Empty(activity.Teachers);
        Assert.Empty(activity.WeekIndexes);
    }

    [Fact]
    public void NewtonsoftDeserialize_ShouldCoerceExplicitNullsToEmpty()
    {
        // 生产解析路径（CourseService 用的是 Newtonsoft）
        var response = JsonConvert.DeserializeObject<CourseResponse>(UpstreamJsonWithExplicitNulls);

        AssertNeverNull(Assert.Single(response!.StudentTableVm.Activities));
    }

    [Fact]
    public void SystemTextJsonDeserialize_ShouldCoerceExplicitNullsToEmpty()
    {
        // 测试夹具 / Redis 缓存的解析路径
        var response = System.Text.Json.JsonSerializer.Deserialize<CourseResponse>(UpstreamJsonWithExplicitNulls,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        AssertNeverNull(Assert.Single(response!.StudentTableVm.Activities));
    }

    [Fact]
    public void ObjectInitializer_ShouldCoerceExplicitNullsToEmpty()
    {
        // 直接 new 出一条"脏数据"也不能把 null 带进领域代码
        var activity = new CourseActivity
        {
            Campus = null!,
            Room = null!,
            CourseName = null!,
            CourseCode = null!,
            Credits = null!,
            LessonId = null!,
            Teachers = null!,
            WeekIndexes = null!
        };

        AssertNeverNull(activity);
    }
}
