namespace XAUAT.EduApi.Services;

public interface IInfoService
{
    /// <summary>
    /// 获取时间
    /// </summary>
    /// <returns></returns>
    TimeModel GetTime();
    
    /// <summary>
    /// 是否大于开始时间
    /// </summary>
    /// <returns></returns>
    bool IsGreatThanStart();
    
    /// <summary>
    /// 是否大于开始时间
    /// </summary>
    /// <param name="time"></param>
    /// <returns></returns>
    bool IsGreatThanStart(int time);
    
    /// <summary>
    /// 是否小于结束时间
    /// </summary>
    /// <returns></returns>
    bool IsLessThanEnd();
    
    /// <summary>
    /// 是否在学期内
    /// </summary>
    /// <returns></returns>
    bool IsInSchool();
    
    /// <summary>
    /// 是否小于结束时间
    /// </summary>
    /// <param name="time"></param>
    /// <returns></returns>
    bool IsLessThanEnd(int time);
}

[Serializable]
public class TimeModel
{
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
}

public class InfoService : IInfoService
{
    public TimeModel GetTime()
    {
        var start = Environment.GetEnvironmentVariable("START", EnvironmentVariableTarget.Process);
        var end = Environment.GetEnvironmentVariable("END", EnvironmentVariableTarget.Process);
        return new TimeModel() { StartTime = start ?? "2026-03-01", EndTime = end ?? "2026-07-18" };
    }

    public bool IsGreatThanStart()
    {
        var time = GetTime();
        return DateTime.Now >= DateTime.Parse(time.StartTime);
    }

    public bool IsGreatThanStart(int time)
    {
        var timeModel = GetTime();
        return DateTime.Now >= DateTime.Parse(timeModel.StartTime).AddDays(time * 7);
    }

    public bool IsInSchool()
    {
        var time = GetTime();
        return DateTime.Now >= DateTime.Parse(time.StartTime) && DateTime.Now <= DateTime.Parse(time.EndTime);
    }

    public bool IsLessThanEnd()
    {
        var time = GetTime();
        return DateTime.Now <= DateTime.Parse(time.EndTime);
    }

    public bool IsLessThanEnd(int time)
    {
        var timeModel = GetTime();
        return DateTime.Now <= DateTime.Parse(timeModel.EndTime).AddDays(-time * 7);
    }
}