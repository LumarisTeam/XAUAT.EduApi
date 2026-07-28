namespace EduApi.Data.Models;

/// <summary>
/// 校区枚举
/// </summary>
public enum Campus
{
    /// <summary>
    /// 草堂校区
    /// </summary>
    CanTang = 0,
    
    /// <summary>
    /// 雁塔校区
    /// </summary>
    YanTa = 1
}

/// <summary>
/// 季节枚举
/// </summary>
public enum Season
{
    /// <summary>
    /// 冬季
    /// </summary>
    Winter = 0,
    
    /// <summary>
    /// 夏季
    /// </summary>
    Summer = 1
}

/// <summary>
/// 课程时段模型
/// 表示一节课的具体时间信息
/// </summary>
[Serializable]
public class SchedulePeriod
{
    /// <summary>
    /// 节次索引（从0开始）
    /// 第0节为早自习（8:00-8:20）
    /// 第1节开始为正常课程（8:30开始）
    /// </summary>
    public int UnitIndex { get; set; }
    
    /// <summary>
    /// 开始时间（HH:mm格式）
    /// </summary>
    public string StartTime { get; set; } = "";
    
    /// <summary>
    /// 结束时间（HH:mm格式）
    /// </summary>
    public string EndTime { get; set; } = "";
    
    /// <summary>
    /// 是否为有效时段（有课程安排）
    /// </summary>
    public bool HasClass => !string.IsNullOrEmpty(StartTime) && !string.IsNullOrEmpty(EndTime);
    
    /// <summary>
    /// 是否为早自习时段
    /// </summary>
    public bool IsEarlyStudy => UnitIndex == 0 && HasClass;
}

/// <summary>
/// 时间表模型
/// 表示某个校区在某个季节的完整课程时间表
/// </summary>
[Serializable]
public class ScheduleTimeTable
{
    /// <summary>
    /// 校区名称
    /// </summary>
    public string CampusName { get; set; } = "";
    
    /// <summary>
    /// 校区枚举值
    /// </summary>
    public Campus Campus { get; set; }
    
    /// <summary>
    /// 季节名称
    /// </summary>
    public string SeasonName { get; set; } = "";
    
    /// <summary>
    /// 季节枚举值
    /// </summary>
    public Season Season { get; set; }
    
    /// <summary>
    /// 各节次的时间安排列表
    /// 索引0对应第0节课（早自习）
    /// </summary>
    public List<SchedulePeriod> Periods { get; set; } = [];
    
    /// <summary>
    /// 有效时段数量（排除空时段）
    /// </summary>
    public int ValidPeriodCount => Periods.Count(p => p.HasClass);
}

/// <summary>
/// 时间表API响应模型
/// </summary>
[Serializable]
public class ScheduleTimeResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// 时间表数据（包含两个校区的所有季节时间表）
    /// 后端不做校区判断，前端根据课程的校区字段自行选择使用哪个时间表
    /// </summary>
    public List<ScheduleTimeTable> Data { get; set; } = [];
    
    /// <summary>
    /// 当前日期
    /// </summary>
    public DateTime CurrentDate { get; set; }
    
    /// <summary>
    /// 校区列表
    /// </summary>
    public List<string> Campuses { get; set; } = [];
    
    /// <summary>
    /// 季节列表
    /// </summary>
    public List<string> Seasons { get; set; } = [];
}