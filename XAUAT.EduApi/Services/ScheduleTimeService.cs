using EduApi.Data.Models;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 课程时间表服务接口
/// 提供课程时间段查询功能，后端不做校区判断，直接返回两个校区的时间数据供前端缓存使用
/// </summary>
public interface IScheduleTimeService
{
    /// <summary>
    /// 获取所有校区和季节的课程时间表
    /// </summary>
    /// <returns>所有时间表数据</returns>
    Task<List<ScheduleTimeTable>> GetAllScheduleTablesAsync();
    
    /// <summary>
    /// 获取指定校区和季节的课程时间表
    /// </summary>
    /// <param name="campus">校区</param>
    /// <param name="season">季节</param>
    /// <returns>对应校区和季节的时间表</returns>
    Task<ScheduleTimeTable?> GetScheduleTableAsync(Campus campus, Season season);
    
    /// <summary>
    /// 获取指定节次的具体时间
    /// </summary>
    /// <param name="campus">校区</param>
    /// <param name="season">季节</param>
    /// <param name="unitIndex">节次索引（从0开始，第0节为早自习）</param>
    /// <returns>该节次的时间信息，若不存在则返回null</returns>
    Task<SchedulePeriod?> GetPeriodAsync(Campus campus, Season season, int unitIndex);
    
    /// <summary>
    /// 获取完整的时间表API响应（包含所有校区和季节的时间表）
    /// </summary>
    /// <returns>时间表响应模型</returns>
    Task<ScheduleTimeResponse> GetScheduleTimeResponseAsync();
}

/// <summary>
/// 课程时间表服务实现
/// 基于静态数据提供课程时间段查询功能
/// 节次索引从0开始：第0节为早自习（草堂8:00-8:20），第1节从8:30开始
/// 后端不做校区判断，前端根据课程的校区字段自行选择使用哪个时间表
/// </summary>
public class ScheduleTimeService : IScheduleTimeService
{
    /// <summary>
    /// 草堂校区开始时间（与Flutter端一致）
    /// 索引0对应第0节课（早自习8:00-8:20），索引1对应第1节课（8:30开始）
    /// </summary>
    private static readonly string[] CanTangTimeStart = {
        "8:00",   // 第0节：早自习
        "8:30",   // 第1节
        "9:20",   // 第2节
        "10:25",  // 第3节
        "11:15",  // 第4节
        "12:10",  // 第5节
        "13:00",  // 第6节
        "14:00",  // 第7节
        "14:50",  // 第8节
        "15:45",  // 第9节
        "16:35",  // 第10节
        "19:30",  // 第11节
        "20:20"   // 第12节
    };

    /// <summary>
    /// 草堂校区结束时间
    /// </summary>
    private static readonly string[] CanTangTimeEnd = {
        "8:20",   // 第0节：早自习
        "9:15",   // 第1节
        "10:05",  // 第2节
        "11:10",  // 第3节
        "12:00",  // 第4节
        "12:55",  // 第5节
        "13:45",  // 第6节
        "14:45",  // 第7节
        "15:35",  // 第8节
        "16:30",  // 第9节
        "17:20",  // 第10节
        "20:15",  // 第11节
        "21:05"   // 第12节
    };

    /// <summary>
    /// 雁塔校区冬季开始时间
    /// 索引0对应第0节课（空），索引1对应第1节课（8:00开始）
    /// </summary>
    private static readonly string[] YanTaDongStart = {
        "",       // 第0节：空（早自习时段）
        "8:00",   // 第1节
        "9:00",   // 第2节
        "10:10",  // 第3节
        "11:10",  // 第4节
        "",       // 第5节：空
        "",       // 第6节：空
        "14:00",  // 第7节
        "15:00",  // 第8节
        "16:00",  // 第9节
        "17:00",  // 第10节
        "19:30",  // 第11节
        "20:30"   // 第12节
    };

    /// <summary>
    /// 雁塔校区冬季结束时间
    /// </summary>
    private static readonly string[] YanTaDongEnd = {
        "",       // 第0节：空
        "8:50",   // 第1节
        "9:50",   // 第2节
        "11:00",  // 第3节
        "12:00",  // 第4节
        "",       // 第5节：空
        "",       // 第6节：空
        "14:50",  // 第7节
        "15:50",  // 第8节
        "16:50",  // 第9节
        "17:50",  // 第10节
        "20:20",  // 第11节
        "21:20"   // 第12节
    };

    /// <summary>
    /// 雁塔校区夏季开始时间
    /// </summary>
    private static readonly string[] YanTaXiaStart = {
        "",       // 第0节：空（早自习时段）
        "8:00",   // 第1节
        "9:00",   // 第2节
        "10:10",  // 第3节
        "11:10",  // 第4节
        "",       // 第5节：空
        "",       // 第6节：空
        "14:30",  // 第7节
        "15:30",  // 第8节
        "16:30",  // 第9节
        "17:30",  // 第10节
        "20:00",  // 第11节
        "21:00"   // 第12节
    };

    /// <summary>
    /// 雁塔校区夏季结束时间
    /// </summary>
    private static readonly string[] YanTaXiaEnd = {
        "",       // 第0节：空
        "8:50",   // 第1节
        "9:50",   // 第2节
        "11:00",  // 第3节
        "12:00",  // 第4节
        "",       // 第5节：空
        "",       // 第6节：空
        "15:20",  // 第7节
        "16:20",  // 第8节
        "17:30",  // 第9节
        "18:20",  // 第10节
        "20:50",  // 第11节
        "21:50"   // 第12节
    };

    /// <summary>
    /// 缓存所有时间表数据
    /// </summary>
    private readonly List<ScheduleTimeTable> _scheduleTables;

    public ScheduleTimeService()
    {
        _scheduleTables = InitializeScheduleTables();
    }

    /// <summary>
    /// 初始化所有时间表数据
    /// </summary>
    /// <returns>所有校区和季节的时间表列表</returns>
    private List<ScheduleTimeTable> InitializeScheduleTables()
    {
        return new List<ScheduleTimeTable>
        {
            // 草堂校区（不分季节）
            new ScheduleTimeTable
            {
                CampusName = "草堂校区",
                Campus = Campus.CanTang,
                SeasonName = "通用",
                Season = Season.Summer,
                Periods = CreatePeriods(CanTangTimeStart, CanTangTimeEnd)
            },
            // 雁塔校区冬季
            new ScheduleTimeTable
            {
                CampusName = "雁塔校区",
                Campus = Campus.YanTa,
                SeasonName = "冬季",
                Season = Season.Winter,
                Periods = CreatePeriods(YanTaDongStart, YanTaDongEnd)
            },
            // 雁塔校区夏季
            new ScheduleTimeTable
            {
                CampusName = "雁塔校区",
                Campus = Campus.YanTa,
                SeasonName = "夏季",
                Season = Season.Summer,
                Periods = CreatePeriods(YanTaXiaStart, YanTaXiaEnd)
            }
        };
    }

    /// <summary>
    /// 根据开始时间和结束时间数组创建课程时段列表
    /// </summary>
    /// <param name="startTimes">开始时间数组</param>
    /// <param name="endTimes">结束时间数组</param>
    /// <returns>课程时段列表</returns>
    private List<SchedulePeriod> CreatePeriods(string[] startTimes, string[] endTimes)
    {
        var periods = new List<SchedulePeriod>();
        
        for (int i = 0; i < startTimes.Length; i++)
        {
            periods.Add(new SchedulePeriod
            {
                UnitIndex = i,
                StartTime = startTimes[i],
                EndTime = endTimes[i]
            });
        }
        
        return periods;
    }

    public Task<List<ScheduleTimeTable>> GetAllScheduleTablesAsync()
    {
        return Task.FromResult(_scheduleTables);
    }

    public Task<ScheduleTimeTable?> GetScheduleTableAsync(Campus campus, Season season)
    {
        if (campus == Campus.CanTang)
        {
            var table = _scheduleTables.FirstOrDefault(t => t.Campus == Campus.CanTang);
            return Task.FromResult(table);
        }
        
        var yanTaTable = _scheduleTables.FirstOrDefault(t => t.Campus == campus && t.Season == season);
        return Task.FromResult(yanTaTable);
    }

    public async Task<SchedulePeriod?> GetPeriodAsync(Campus campus, Season season, int unitIndex)
    {
        var table = await GetScheduleTableAsync(campus, season);
        if (table == null)
            return null;
        
        return table.Periods.FirstOrDefault(p => p.UnitIndex == unitIndex);
    }

    public async Task<ScheduleTimeResponse> GetScheduleTimeResponseAsync()
    {
        var allTables = await GetAllScheduleTablesAsync();
        
        return new ScheduleTimeResponse
        {
            Success = true,
            Data = allTables,
            CurrentDate = DateTime.Now,
            Campuses = Enum.GetNames<Campus>().ToList(),
            Seasons = Enum.GetNames<Season>().ToList()
        };
    }
}