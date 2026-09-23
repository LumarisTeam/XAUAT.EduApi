namespace XAUAT.EduApi.Services;

/// <summary>
/// 课表时间服务接口
/// 根据校区和节次计算课程的开始和结束时间
/// </summary>
public interface IClassTimeService
{
    /// <summary>
    /// 根据校区和节次计算开始时间（按"今天"选季节，等价于传入 <see cref="DateTime.Now"/>）
    /// </summary>
    /// <param name="campus">校区名称</param>
    /// <param name="unit">节次</param>
    /// <returns>开始时间，格式 HH:mm</returns>
    string GetStartTime(string campus, int unit);

    /// <summary>
    /// 根据校区和节次计算结束时间（按"今天"选季节，等价于传入 <see cref="DateTime.Now"/>）
    /// </summary>
    /// <param name="campus">校区名称</param>
    /// <param name="unit">节次</param>
    /// <returns>结束时间，格式 HH:mm</returns>
    string GetEndTime(string campus, int unit);

    /// <summary>
    /// 根据校区、节次与**事件日期**计算开始时间。
    /// </summary>
    /// <param name="campus">校区名称</param>
    /// <param name="unit">节次</param>
    /// <param name="date">
    /// 事件发生的日期。雁塔校区的作息表按季节分两套，季节必须由**事件那天**决定：
    /// 一个学期横跨两个季节（秋季学期 9 月属夏季、10 月起属冬季），
    /// 用"今天"选表会让另一半事件算错时刻。
    /// </param>
    /// <returns>开始时间，格式 HH:mm；该节次无课时返回空串</returns>
    string GetStartTime(string campus, int unit, DateTime date);

    /// <summary>
    /// 根据校区、节次与**事件日期**计算结束时间。语义同
    /// <see cref="GetStartTime(string, int, DateTime)"/>。
    /// </summary>
    string GetEndTime(string campus, int unit, DateTime date);
}

/// <summary>
/// 课表时间服务
/// 管理不同校区的节次时间映射
/// 草堂校区：一套时间表
/// 雁塔校区：夏季/冬季两套时间表
/// </summary>
public class ClassTimeService : IClassTimeService
{
    /// <summary>
    /// 草堂校区时间表（13节课，第0-12节）
    /// </summary>
    private static readonly (string Start, string End)[] CaotangTimeTable =
    {
        ("08:00", "08:20"),  // 第0节
        ("08:30", "09:15"),  // 第1节
        ("09:20", "10:05"),  // 第2节
        ("10:25", "11:10"),  // 第3节
        ("11:15", "12:00"),  // 第4节
        ("12:10", "12:55"),  // 第5节
        ("13:00", "13:45"),  // 第6节
        ("14:00", "14:45"),  // 第7节
        ("14:50", "15:35"),  // 第8节
        ("15:45", "16:30"),  // 第9节
        ("16:35", "17:20"),  // 第10节
        ("19:30", "20:15"),  // 第11节
        ("20:20", "21:05")   // 第12节
    };

    /// <summary>
    /// 雁塔冬季时间表（13节课，第0-12节，第0、5、6节无课）
    /// </summary>
    private static readonly (string Start, string End)[] YantaWinterTimeTable =
    {
        ("", ""),            // 第0节
        ("08:00", "08:50"),  // 第1节
        ("09:00", "09:50"),  // 第2节
        ("10:10", "11:00"),  // 第3节
        ("11:10", "12:00"),  // 第4节
        ("", ""),            // 第5节
        ("", ""),            // 第6节
        ("14:00", "14:50"),  // 第7节
        ("15:00", "15:50"),  // 第8节
        ("16:00", "16:50"),  // 第9节
        ("17:00", "17:50"),  // 第10节
        ("19:30", "20:20"),  // 第11节
        ("20:30", "21:20")   // 第12节
    };

    /// <summary>
    /// 雁塔夏季时间表（13节课，第0-12节，第0、5、6节无课）
    /// </summary>
    private static readonly (string Start, string End)[] YantaSummerTimeTable =
    {
        ("", ""),            // 第0节
        ("08:00", "08:50"),  // 第1节
        ("09:00", "09:50"),  // 第2节
        ("10:10", "11:00"),  // 第3节
        ("11:10", "12:00"),  // 第4节
        ("", ""),            // 第5节
        ("", ""),            // 第6节
        ("14:30", "15:20"),  // 第7节
        ("15:30", "16:20"),  // 第8节
        ("16:30", "17:30"),  // 第9节
        ("17:30", "18:20"),  // 第10节
        ("20:00", "20:50"),  // 第11节
        ("21:00", "21:50")   // 第12节
    };

    /// <summary>
    /// 判断 <paramref name="date"/> 落在雁塔夏季区间内。
    /// <para>
    /// 边界与 <c>ScheduleTimeService</c>（即客户端消费的 <c>/v1/course/ScheduleTime</c>）
    /// 以及客户端 <c>ScheduleTable.coversDate</c> 一致：夏季 <c>05/01~09/30</c>，
    /// 冬季 <c>10/01~04/30</c>。注意 <b>10 月属冬季</b>——这里曾经写成"5..10 月为夏季"，
    /// 与上面两处的定义相反。
    /// </para>
    /// </summary>
    private static bool IsSummerSeason(DateTime date)
        => date.Month is >= 5 and <= 9;

    /// <summary>
    /// 根据校区与日期获取对应的时间表
    /// </summary>
    private (string Start, string End)[] GetTimeTable(string campus, DateTime date)
    {
        if (campus.Contains("草堂"))
        {
            return CaotangTimeTable;
        }

        if (campus.Contains("雁塔"))
        {
            return IsSummerSeason(date) ? YantaSummerTimeTable : YantaWinterTimeTable;
        }

        // 默认使用草堂校区时间表
        return CaotangTimeTable;
    }

    public string GetStartTime(string campus, int unit) => GetStartTime(campus, unit, DateTime.Now);

    public string GetEndTime(string campus, int unit) => GetEndTime(campus, unit, DateTime.Now);

    public string GetStartTime(string campus, int unit, DateTime date)
    {
        var timeTable = GetTimeTable(campus, date);
        if (unit < 0 || unit >= timeTable.Length)
        {
            return "";
        }

        return timeTable[unit].Start;
    }

    public string GetEndTime(string campus, int unit, DateTime date)
    {
        var timeTable = GetTimeTable(campus, date);
        if (unit < 0 || unit >= timeTable.Length)
        {
            return "";
        }

        return timeTable[unit].End;
    }
}
