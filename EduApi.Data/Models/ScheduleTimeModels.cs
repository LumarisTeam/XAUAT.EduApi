namespace EduApi.Data.Models;

/// <summary>
/// 时间表模型
/// 表示某个校区在某个季节的完整课程时间表
/// </summary>
[Serializable]
public class ScheduleTimeModel
{
    /// <summary>
    /// 校区名称
    /// </summary>
    public string CampusName { get; set; } = "";

    /// <summary>
    /// 时间区间，例如 05/01 - 10/01
    /// </summary>
    public string Time { get; set; } = "";
    
    /// <summary>
    /// 
    /// </summary>
    public List<string> Start { get; set; } = [];
    
    public List<string> End { get; set; } = [];
}