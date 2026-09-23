using System.Globalization;
using System.Text.RegularExpressions;
using EduApi.Data;
using EduApi.Data.Models;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Polly;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Repos;

namespace XAUAT.EduApi.Services;

public interface IExamService
{
    Task<ExamResponse> GetExamArrangementsAsync(
        string cookie,
        string? id,
        string language = "zh",
        IEnumerable<string>? requestStudentIds = null);

    Task<SemesterItem> GetThisSemester(string cookie, string language = "zh");
}

public class ExamService(
    IHttpClientFactory httpClientFactory,
    ILogger<ExamService> logger,
    IInfoService info,
    ICacheService cacheService,
    IExamRepository examRepository,
    ITestAccountResolver? testAccountResolver = null,
    ITestDataProvider? testDataProvider = null,
    IStudentRateLimitExecutor? rateLimitExecutor = null)
    : IExamService
{
    private const string BaseUrl = "https://swjw.xauat.edu.cn";

    /// <summary>
    /// 考试安排页。<b>尾斜杠不能省</b>：实测 <c>/for-std/exam-arrange</c>（无斜杠）会返回
    /// HTTP 200，但内容其实是**「学籍信息」**页——既不重定向也不报错，解析器于是静默返回空列表，
    /// 表现为"日历里从来没有考试事件"。这条是从 XAUAT.LoginApi 的抓取报告里带过来的。
    /// </summary>
    private const string ExamArrangePath = "/student/for-std/exam-arrange/";
    /// <summary>校本部时区。实现已抽到 <see cref="SchoolClock"/>，此处保留别名以免改动过大。</summary>
    private static TimeZoneInfo SchoolTimeZone => SchoolClock.TimeZone;

    private readonly IStudentRateLimitExecutor _rateLimitExecutor =
        rateLimitExecutor ?? NoOpStudentRateLimitExecutor.Instance;

    public async Task<ExamResponse> GetExamArrangementsAsync(
        string cookie,
        string? id,
        string language = "zh",
        IEnumerable<string>? requestStudentIds = null)
    {
        if (testAccountResolver?.IsTestAccount(cookie: cookie, studentId: id) == true)
        {
            logger.LogInformation("测试账号命中考试测试数据，studentId: {StudentId}", id);
            return await testDataProvider!.GetExamResponseAsync();
        }

        if (string.IsNullOrEmpty(id) || !id.Contains(','))
        {
            return await GetExamArrangementAsync(cookie, id, language, requestStudentIds);
        }

        var split = id.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 0)
        {
            return await GetExamArrangementAsync(cookie, null, language, requestStudentIds);
        }

        if (split.Length == 1)
        {
            return await GetExamArrangementAsync(cookie, split[0], language, [split[0]]);
        }

        // 使用 Task.WhenAll 并行获取所有学生的考试安排，解决 N+1 问题
        var tasks = split.Select(s => GetExamArrangementAsync(cookie, s, language, [s])).ToArray();
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        // 合并结果
        var examResponse = new ExamResponse
        {
            CanClick = allResults.Any(result => result.CanClick || result.Exams.Count > 0)
        };
        foreach (var result in allResults)
        {
            examResponse.Exams.AddRange(result.Exams);
        }

        return examResponse;
    }

    /// <summary>
    /// 获取本学期
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="language"></param>
    /// <returns></returns>
    public async Task<SemesterItem> GetThisSemester(string cookie, string language = "zh")
    {
        if (testAccountResolver?.IsTestAccount(cookie: cookie) == true)
        {
            logger.LogInformation("测试账号命中当前学期测试数据");
            return await testDataProvider!.GetCurrentSemesterAsync();
        }

        logger.LogInformation("开始抓取学期数据");

        return await cacheService.GetOrCreateAsync(
            CacheKeys.ThisSemester,
            async () =>
            {
                var retryPolicy = Policy
                    .Handle<HttpRequestException>()
                    .Or<TaskCanceledException>()
                    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(6 * Math.Pow(2, retryAttempt - 1)));

                return await retryPolicy.ExecuteAsync(async () =>
                {
                    using var client = httpClientFactory.CreateClient();
                    client.SetRealisticHeaders();
                    client.Timeout = HttpTimeouts.EduSystem;
                    client.DefaultRequestHeaders.Add("Cookie", cookie);

                    var html = await client.GetStringAsync("https://swjw.xauat.edu.cn/student/for-std/course-table");

                    html.ThrowIfAuthOrRateLimited();

                    return html.ParseNow(info);
                });
            },
            TimeSpan.FromHours(1));
    }

    /// <summary>
    /// 获取考试安排
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="id"></param>
    /// <param name="language"></param>
    /// <param name="requestStudentIds"></param>
    /// <returns></returns>
    private async Task<ExamResponse> GetExamArrangementAsync(
        string cookie,
        string? id = null,
        string language = "zh",
        IEnumerable<string>? requestStudentIds = null)
    {
        // 无id时不使用缓存
        if (string.IsNullOrEmpty(id))
        {
            return await FetchExamArrangementAsync(cookie, null, language, requestStudentIds);
        }

        return await cacheService.GetOrCreateAsync(
            CacheKeys.ExamArrangement(id),
            async () => await FetchExamArrangementAsync(cookie, id, language, requestStudentIds ?? [id]),
            TimeSpan.FromHours(1));
    }

    private async Task<ExamResponse> FetchExamArrangementAsync(
        string cookie,
        string? id,
        string language,
        IEnumerable<string>? requestStudentIds)
    {
        try
        {
            var url = $"{BaseUrl}{ExamArrangePath}";
            if (!string.IsNullOrEmpty(id))
            {
                url += $"info/{id}?";
            }

            var httpClient = httpClientFactory.CreateClient();
            httpClient.SetRealisticHeaders();
            httpClient.Timeout = HttpTimeouts.EduSystem;

            using var cts = new CancellationTokenSource(HttpTimeouts.EduSystem);

            var retryPolicy = Policy
                .Handle<HttpRequestException>()
                .Or<TaskCanceledException>()
                .WaitAndRetryAsync(3, retryAttempt =>
                    TimeSpan.FromSeconds(6 * Math.Pow(2, retryAttempt - 1)));

            return await _rateLimitExecutor.ExecuteAsync(
                requestStudentIds ?? [],
                () => retryPolicy.ExecuteAsync(async ct =>
                {
                    var requestPolicy = new HttpRequestMessage(HttpMethod.Get, url).WithCookie(cookie);
                    var response = await httpClient.SendAsync(requestPolicy, ct);
                    var content = await response.Content.ReadAsStringAsync(ct);

                    // 限流页识别必须放在执行器内部，这样第一次命中才能真正写入冷却状态
                    try
                    {
                        content.ThrowIfAuthOrRateLimited();
                    }
                    catch (Exception)
                    {
                        if (string.IsNullOrEmpty(id)) throw;
                        var data = await examRepository.GetByStudentIdAsync(id);

                        var examRecords = data as ExamRecord[] ?? data.ToArray();
                        if (examRecords.Length != 0)
                        {
                            return new ExamResponse()
                            {
                                Exams = examRecords.Select(x => new ExamInfo()
                                {
                                    Name = x.Name,
                                    Time = x.Time,
                                    Location = x.Location,
                                    Seat = x.Seat,
                                }).ToList()
                            };
                        }

                        throw;
                    }

                    var examData = ParseNow(content);
                    if (examData != null!)
                    {
                        if (string.IsNullOrEmpty(id) || examData.Count <= 0)
                        {
                            return new ExamResponse
                            {
                                Exams = examData,
                                CanClick = examData.Count > 0
                            };
                        }

                        try
                        {
                            var records = examData.Select(i => ExamInfoToRecord(id, i)).ToList();
                            await examRepository.MergeStudentExamsAsync(id, records);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "合并考试数据到数据库时出错，studentId: {StudentId}", id);
                        }

                        return new ExamResponse
                        {
                            Exams = examData,
                            CanClick = examData.Count > 0
                        };
                    }

                    logger.LogWarning("Failed to deserialize exam data");
                    return new ExamResponse
                    {
                        Exams = [],
                        CanClick = false,
                        Error = "Failed to deserialize exam data"
                    };
                }, cts.Token));
        }
        catch (RateLimitException)
        {
            throw;
        }
        catch (UnAuthenticationError)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting exam arrangements");
            return new ExamResponse
            {
                Exams = [],
                CanClick = false,
                Error = ex.Message
            };
        }
    }

    private List<ExamInfo> ParseNow(string html)
    {
        var result = new List<ExamInfo>();
        try
        {
            // 1. 使用正则表达式从内嵌的 <script> 中提取出 studentExamList 的 JSON 字符串
            const string pattern = @"var studentExamList = (\[.*?\]);";
            var match = Regex.Match(html, pattern, RegexOptions.Singleline);

            if (!match.Success)
            {
                Console.WriteLine("未在网页脚本中找到座位数据矩阵！");
                return result;
            }

            var jsonRaw = match.Groups[1].Value;
            // 将强智系统中的单引号替换为标准 JSON 的双引号
            jsonRaw = jsonRaw.Replace("'", "\"");

            // 2. 反序列化为 C# 对象列表
            var examList = JsonConvert.DeserializeObject<List<StudentExam>>(jsonRaw) ?? [];

            // 3. 使用 HtmlAgilityPack 解析 HTML 表格，获取课程和时间等基础文本
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // 定位到表格的行
            var rows = htmlDoc.DocumentNode.SelectNodes("//table[@id='exams']/tbody/tr");
            if (rows == null!) return [];

            foreach (var row in rows)
            {
                var tds = row.SelectNodes("td");
                if (tds == null! || tds.Count < 4) continue;

                var courseName = tds[0].InnerText.Trim();
                var time = tds[1].InnerText.Trim().Replace('~', '-');
                var examPlace = tds[2].InnerText.Trim();

                // 获取对应的座位 <td> 的 id（形如 seat-2666478）
                var seatTdId = tds[3].GetAttributeValue("id", "");
                var examId = long.Parse(seatTdId.Replace("seat-", ""));

                // 从 JSON 列表中匹配出这场考试的详细座位图
                var examData = examList.FirstOrDefault(e => e.Id == examId);

                if (examData != null)
                {
                    // 计算行列坐标（例如 A9）
                    // var alphabetCoordinate = CalculateSeatCoordinate(examData.SeatMap.Map, examData.SeatNo);

                    result.Add(new ExamInfo()
                    {
                        Name = courseName.Split('\n')[0].Trim(),
                        Location = examPlace,
                        Time = time,
                        Seat = examData.SeatNo.ToString()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("解析发生错误: " + ex.Message);
        }

        return result;
    }

    private static ExamRecord ExamInfoToRecord(string studentId, ExamInfo info)
    {
        var timeRaw = info.Time;
        var examTime = ParseExamTime(timeRaw);
        var rawKey = $"{studentId}_{info.Name}_{timeRaw}";
        timeRaw = timeRaw.Replace('~', '-');
        return new ExamRecord
        {
            Key = rawKey.ToHash(),
            StudentId = studentId,
            Name = info.Name,
            Time = timeRaw,
            ExamTime = examTime,
            Location = info.Location,
            Seat = info.Seat
        };
    }

    private static DateTime ParseExamTime(string timeRaw)
        => ToUtcExamTime(ParseExamTimeRange(timeRaw).Start);

    /// <summary>
    /// 把考试时间串解析为**校本部本地时间**的起止时刻。
    /// <para>
    /// 上游给的原始形态是 <c>2026-07-13 14:00~16:00</c>；表格解析那条路径已经把
    /// <c>~</c> 换成 <c>-</c>，所以三种分隔符都要认（见 <see cref="SplitExamTimeRange"/>）。
    /// 结束时刻通常只写 <c>16:00</c>，需要用开始那段的日期补全。
    /// </para>
    /// <para>
    /// 返回值是**墙上时间**（<see cref="DateTimeKind.Unspecified"/>，语义为 Asia/Shanghai），
    /// 不是 UTC：日历要把它原样写进 ICS 的 <c>DTSTART</c>，而 <see cref="ExamRecord.ExamTime"/>
    /// 那条路径再另行转 UTC。<b>不要把这里改成返回 UTC</b>——ICS 的 UID 用的是本地时刻，
    /// 换了时区基准会让已订阅用户看到重复事件。
    /// </para>
    /// </summary>
    /// <returns>解析失败时返回 (<see cref="DateTime.MinValue"/>, <see cref="DateTime.MinValue"/>)。</returns>
    public static (DateTime Start, DateTime End) ParseExamTimeRange(string timeRaw)
    {
        if (string.IsNullOrWhiteSpace(timeRaw))
            return (DateTime.MinValue, DateTime.MinValue);

        var (startText, endText) = SplitExamTimeRange(timeRaw);

        if (!TryParseLocalDateTime(startText, null, out var start))
            return (DateTime.MinValue, DateTime.MinValue);

        if (endText is null)
            return (start, start);

        // 结束时刻通常只有 "16:00"，补上开始那段的日期前缀
        return TryParseLocalDateTime(endText, ExtractDatePrefix(startText), out var end)
            ? (start, end)
            : (start, start);
    }

    /// <summary>
    /// 按 <c>~</c>/<c>～</c> → <c>" - "</c> → 最后一个 <c>-</c> 的顺序切出起止两段。
    /// 最后一种是为了 <c>2026-07-13 14:00-16:00</c>：日期里本来就有两个连字符，
    /// 所以只有"连字符多于两个"时才把最后一个当作时间段分隔符。
    /// </summary>
    private static (string Start, string? End) SplitExamTimeRange(string timeRaw)
    {
        var raw = timeRaw.Trim();

        var tildeIndex = raw.IndexOfAny(['~', '～']);
        if (tildeIndex > 0)
            return (raw[..tildeIndex].Trim(), raw[(tildeIndex + 1)..].Trim());

        var spacedHyphenIndex = raw.IndexOf(" - ", StringComparison.Ordinal);
        if (spacedHyphenIndex > 0)
            return (raw[..spacedHyphenIndex].Trim(), raw[(spacedHyphenIndex + 3)..].Trim());

        if (raw.Count(c => c == '-') > 2)
        {
            var lastHyphenIndex = raw.LastIndexOf('-');
            if (lastHyphenIndex > 0)
                return (raw[..lastHyphenIndex].Trim(), raw[(lastHyphenIndex + 1)..].Trim());
        }

        return (raw, null);
    }

    /// <summary>取 <c>"2026-07-13 14:00"</c> 里的日期部分。</summary>
    private static string? ExtractDatePrefix(string text)
    {
        var lastSpace = text.LastIndexOf(' ');
        return lastSpace > 0 ? text[..lastSpace] : null;
    }

    private static bool TryParseLocalDateTime(string text, string? datePrefix, out DateTime value)
    {
        // 只在结束段（形如 "16:00"、自身不带空格）时才补前缀，避免把完整时间串拼坏
        if (!string.IsNullOrEmpty(datePrefix) && !text.Contains(' '))
        {
            var composed = $"{datePrefix} {text}";
            if (TryParseLocalDateTimeCore(composed, out value))
                return true;
        }

        return TryParseLocalDateTimeCore(text, out value);
    }

    private static bool TryParseLocalDateTimeCore(string text, out DateTime value)
        => DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
               DateTimeStyles.AllowWhiteSpaces, out value) ||
           DateTime.TryParseExact(text, "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture,
               DateTimeStyles.AllowWhiteSpaces, out value) ||
           DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);

    private static DateTime ToUtcExamTime(DateTime examTime)
    {
        if (examTime == DateTime.MinValue)
            return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

        return examTime.Kind switch
        {
            DateTimeKind.Utc => examTime,
            DateTimeKind.Local => examTime.ToUniversalTime(),
            _ => TimeZoneInfo.ConvertTimeToUtc(examTime, SchoolTimeZone)
        };
    }

}

public static class SemesterModelStatic
{
    public static SemesterItem ParseNow(this string html, IInfoService service)
    {
        // 检查是否登录
        if (!Regex.IsMatch(html, "课表"))
        {
            return new SemesterItem();
        }

        // 使用正则表达式匹配所有semester选项
        var regexString = "<option value=\"(.*)\">(.*)</option>";

        if (service.IsLessThanEnd())
        {
            regexString = "<option selected=\"selected\" value=\"(.*)\">(.*)</option>";
        }

        var regex = new Regex(regexString);
        var matches = regex.Matches(html);

        if (matches.Count == 0) return new SemesterItem();

        var match = matches.FirstOrDefault();
        if (match == null) return new SemesterItem();

        var text = match.Groups[2].Value;

        if (text == "" || text[^1] == '3')
        {
            return SemesterItem.Default;
        }

        // 2026-2027-1
        var textSplit = text.Split('-');

        var timeModel = service.GetTime();

        // 2026-9-1
        var startTimeSplit = timeModel.StartTime.Split('-');

        if (int.TryParse(startTimeSplit[1], out var semester) &&
            int.TryParse(startTimeSplit[0], out var startTimeYear))
        {
            var num = -1;
            var startYear = -1;
            var endYear = -1;
            if (textSplit.Length == 3)
            {
                startYear = int.Parse(textSplit[0]);
                endYear = int.Parse(textSplit[1]);
                num = int.Parse(textSplit[2]);
            }

            if (num == -1 || startYear == -1 || endYear == -1)
            {
                return new SemesterItem();
            }

            // 如果月份大于 7，就是本年的秋季学期，例如 2026-2027-1
            if (semester > 7)
            {
                if (num == 1 && startYear == startTimeYear)
                {
                    return new SemesterItem()
                    {
                        Value = matches.First().Groups[1].Value,
                        Text = text
                    };
                }

                var a = matches.FirstOrDefault(x =>
                {
                    if (x.Groups.Count < 2) return false;
                    var semesters = x.Groups[2].Value.Split('-');
                    if (semesters.Length < 2) return false;
                    return semesters[0] == startTimeSplit[0].Trim('0');
                });

                if (a == null)
                {
                    return SemesterItem.Default;
                }

                return new SemesterItem()
                {
                    Value = a.Groups[1].Value,
                    Text = a.Groups[2].Value
                };
            }
            // 如果月份小于 7，就是本年的春季学期，例如 2026-2027-2
            else
            {
                if (num == 2 && endYear == startTimeYear)
                {
                    return new SemesterItem()
                    {
                        Value = matches.First().Groups[1].Value,
                        Text = text
                    };
                }
                
                var a = matches.FirstOrDefault(x =>
                {
                    if (x.Groups.Count < 2) return false;
                    var semesters = x.Groups[2].Value.Split('-');
                    if (semesters.Length < 2) return false;
                    return semesters[1] == startTimeSplit[1].Trim('0');
                });

                if (a == null)
                {
                    return SemesterItem.Default;
                }

                return new SemesterItem()
                {
                    Value = a.Groups[1].Value,
                    Text = a.Groups[2].Value
                };
            }
        }

        return new SemesterItem()
        {
            Value = matches.First().Groups[1].Value,
            Text = text
        };
    }

    /// <summary>
    /// 核心算法：还原 JS 双重循环，根据座位矩阵和座位号计算出类似 "A9" 的排座坐标
    /// </summary>
    private static string CalculateSeatCoordinate(string mapStr, int targetSeatNo)
    {
        // 按行切割 (\r\n 或 \n)
        var rows = mapStr.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            // 按逗号切割列
            var columns = rows[rowIndex].Split(',');

            for (var colIndex = 0; colIndex < columns.Length; colIndex++)
            {
                var seatValue = columns[colIndex].Trim();
                if (string.IsNullOrEmpty(seatValue)) continue;

                // 找到了该同学的座位号
                if (!int.TryParse(seatValue, out var currentSeatNo) || currentSeatNo != targetSeatNo) continue;
                // 列索引转字母 (colIndex + 1)
                var alphabet = NumToString(colIndex + 1);
                // 行索引转数字 (rowIndex + 1)
                var alphabetNo = rowIndex + 1;

                return $"{alphabet}{alphabetNo}"; // 拼接成 A9, B16 等
            }
        }

        return "未找到坐标";
    }

    /// <summary>
    /// 递归函数：将数字列号转换为 Excel 风格的字母（1->A, 26->Z, 27->AA）
    /// </summary>
    private static string NumToString(int numM)
    {
        var stringArray = new List<char>();

        NumToStringAction(numM);
        stringArray.Reverse(); // 翻转
        return new string(stringArray.ToArray());

        void NumToStringAction(int nNum)
        {
            while (true)
            {
                var num = nNum - 1;
                var a = num / 26; // 商
                var b = num % 26; // 余数

                stringArray.Add((char)(64 + (b + 1)));
                if (a > 0)
                {
                    nNum = a;
                    continue;
                }

                break;
            }
        }
    }
}

[Serializable]
public class SeatMapInfo
{
    public int Columns { get; set; }
    public int Rows { get; set; }
    public string Map { get; set; } = "";
}

[Serializable]
public class StudentExam
{
    public long StudentId { get; set; }
    public int BizTypeId { get; set; }
    public long Id { get; set; }
    public int SeatNo { get; set; }
    public SeatMapInfo SeatMap { get; set; } = new();
}