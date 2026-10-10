namespace EduApi.Data.Models;

public class CourseResponse
{
    public StudentTableVm StudentTableVm { get; set; } = new();
}

[Serializable]
public class StudentTableVm
{
    public List<CourseActivity> Activities { get; set; } = [];
}

/// <summary>
/// 教务 <c>print-data</c> 下发的单条课程安排。
/// <para>
/// <b>属性设置器一律把 null 收敛成空值</b>，别改回自动属性：上游确实会下发
/// <c>"campus": null</c> 这种"字段在、值为 null"的数据，而属性初始值（<c>= ""</c>）
/// 只能挡住"字段缺失"——Newtonsoft 与 System.Text.Json 都会把显式 null 照实写进属性，
/// 让这里声明为非空的类型真的变成 null。线上就因此出过 500：
/// <c>CalendarService.ResolveCampus</c> 对 null 的 Campus 调 <c>Contains</c>。
/// </para>
/// <para>
/// 兜底必须放在设置器而不是调用方：本类型会在 L1 缓存里以对象引用形式存活，
/// 序列化器（Newtonsoft / System.Text.Json）与直接 <c>new</c> 都要走同一条路。
/// </para>
/// </summary>
[Serializable]
public class CourseActivity
{
    private List<int> _weekIndexes = [];
    private string[] _teachers = [];
    private string _campus = "";
    private string _room = "";
    private string _courseName = "";
    private string _courseCode = "";
    private string _credits = "";
    private string _lessonId = "";

    public List<int> WeekIndexes
    {
        get => _weekIndexes;
        set => _weekIndexes = value ?? [];
    }

    public string[] Teachers
    {
        get => _teachers;
        set => _teachers = value ?? [];
    }

    public string Campus
    {
        get => _campus;
        set => _campus = value ?? "";
    }

    public string Room
    {
        get => _room;
        set => _room = value ?? "";
    }

    public string CourseName
    {
        get => _courseName;
        set => _courseName = value ?? "";
    }

    public string CourseCode
    {
        get => _courseCode;
        set => _courseCode = value ?? "";
    }

    public int Weekday { get; set; }
    public int StartUnit { get; set; }

    public int EndUnit { get; set; }

    public string Credits
    {
        get => _credits;
        set => _credits = value ?? "";
    }

    public string LessonId
    {
        get => _lessonId;
        set => _lessonId = value ?? "";
    }
}
