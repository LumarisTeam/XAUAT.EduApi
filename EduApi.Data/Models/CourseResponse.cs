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

[Serializable]
public class CourseActivity
{
    public List<int> WeekIndexes { get; set; } = [];

    public string[] Teachers { get; set; } = [];
    public string Campus { get; set; } = "";
    public string Room { get; set; } = "";

    public string CourseName { get; set; } = "";

    public string CourseCode { get; set; } = "";

    public int Weekday { get; set; }
    public int StartUnit { get; set; }

    public int EndUnit { get; set; }

    public string Credits { get; set; } = "";
    public string LessonId { get; set; } = "";

    // 根据需要添加其他属性
}