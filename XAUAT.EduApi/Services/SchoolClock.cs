namespace XAUAT.EduApi.Services;

/// <summary>
/// 校本部（Asia/Shanghai）时钟。
/// <para>
/// <b>为什么需要它</b>：容器里没有设置 <c>TZ</c>，也没有装 tzdata，
/// <c>DateTime.Now</c> 拿到的是宿主的 UTC 墙钟。而课表/考试的日期时刻都是
/// 校本部墙上时间，直接拿 <c>DateTime.Now</c> 去比较或换算会整体差 8 小时
/// （例如"只保留未来事件"会多留 8 小时之前的过期事件）。
/// </para>
/// <para>
/// 时区解析带兜底：Linux 容器里走 IANA 的 <c>Asia/Shanghai</c>，
/// Windows 上退到 <c>China Standard Time</c>；两者都没有时自建 +08:00
/// （中国自 1991 年起不用夏令时，固定偏移是准确的）。
/// </para>
/// </summary>
public static class SchoolClock
{
    /// <summary>校本部时区。</summary>
    public static TimeZoneInfo TimeZone { get; } = CreateSchoolTimeZone();

    /// <summary>
    /// 校本部的当前墙上时间（<see cref="DateTimeKind.Unspecified"/>）。
    /// </summary>
    public static DateTime Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone).DateTime;

    private static TimeZoneInfo CreateSchoolTimeZone()
    {
        foreach (var timeZoneId in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Asia/Shanghai",
            TimeSpan.FromHours(8),
            "China Standard Time",
            "China Standard Time");
    }
}
