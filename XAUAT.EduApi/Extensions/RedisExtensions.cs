using Newtonsoft.Json;
using StackExchange.Redis;

namespace XAUAT.EduApi.Extensions;

/// <summary>
/// Redis扩展方法
/// 用于简化Redis连接和操作
/// </summary>
public static class RedisExtensions
{
    /// <param name="muxer">Redis连接多路复用器</param>
    extension(IConnectionMultiplexer? muxer)
    {
        /// <summary>
        /// 安全获取Redis数据库
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="redisAvailable">Redis是否可用</param>
        /// <returns>Redis数据库实例，或null如果连接失败</returns>
        public IDatabase? SafeGetDatabase(ILogger? logger, out bool redisAvailable)
        {
            redisAvailable = false;
            if (muxer == null)
            {
                logger?.LogWarning("Redis未配置，将使用数据库作为缓存");
                return null;
            }

            try
            {
                var database = muxer.GetDatabase();
                redisAvailable = true;
                logger?.LogInformation("Redis连接成功");
                return database;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Redis连接失败");
                redisAvailable = false;
                return null;
            }
        }

        /// <summary>
        /// 安全获取Redis数据库
        /// </summary>
        /// <param name="redisAvailable">Redis是否可用</param>
        /// <returns>Redis数据库实例，或null如果连接失败</returns>
        public IDatabase? SafeGetDatabase(out bool redisAvailable)
        {
            return muxer.SafeGetDatabase(null, out redisAvailable);
        }
    }

    /// <param name="database">Redis数据库实例</param>
    extension(IDatabase? database)
    {
        /// <summary>
        /// 安全执行Redis操作
        /// </summary>
        /// <param name="redisAvailable">Redis是否可用</param>
        /// <param name="action">要执行的Redis操作</param>
        /// <param name="logger">日志记录器（可选）</param>
        public void SafeExecute(bool redisAvailable, Action<IDatabase> action, ILogger? logger = null)
        {
            if (redisAvailable && database != null)
            {
                try
                {
                    action(database);
                }
                catch (Exception ex)
                {
                    // 记录Redis操作异常，但不影响主流程
                    logger?.LogWarning(ex, "Redis操作异常: {Message}", ex.Message);
                }
            }
        }

        /// <summary>
        /// 安全执行异步Redis操作
        /// </summary>
        /// <param name="redisAvailable">Redis是否可用</param>
        /// <param name="func">要执行的异步Redis操作</param>
        /// <param name="logger">日志记录器（可选）</param>
        /// <typeparam name="T">操作结果类型</typeparam>
        /// <returns>操作结果，或默认值如果操作失败</returns>
        public async Task<T?> SafeExecuteAsync<T>(bool redisAvailable,
            Func<IDatabase, Task<T>> func, ILogger? logger = null)
        {
            if (redisAvailable && database != null)
            {
                try
                {
                    return await func(database);
                }
                catch (Exception ex)
                {
                    // 记录Redis操作异常，但不影响主流程
                    logger?.LogWarning(ex, "Redis操作异常: {Message}", ex.Message);
                }
            }

            return default;
        }

        /// <summary>
        /// 从Redis获取缓存数据
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="redisAvailable">Redis是否可用</param>
        /// <param name="cacheKey">缓存键</param>
        /// <param name="logger">日志记录器（可选）</param>
        /// <returns>缓存的数据，如果不存在或出错则返回default</returns>
        public async Task<T?> GetCacheAsync<T>(bool redisAvailable, string cacheKey, ILogger? logger = null)
            where T : class
        {
            if (!redisAvailable || database == null)
            {
                return null;
            }

            try
            {
                var redisResult = await database.StringGetAsync(cacheKey).ConfigureAwait(false);
                if (redisResult.HasValue)
                {
                    var cached = JsonConvert.DeserializeObject<T>(redisResult.ToString());
                    if (cached != null)
                    {
                        logger?.LogInformation("已从Redis缓存获取数据，键: {CacheKey}", cacheKey);
                        return cached;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Redis缓存读取失败，键: {CacheKey}", cacheKey);
            }

            return null;
        }

        /// <summary>
        /// 将数据保存到Redis缓存
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="redisAvailable">Redis是否可用</param>
        /// <param name="cacheKey">缓存键</param>
        /// <param name="data">要缓存的数据</param>
        /// <param name="expiry">过期时间</param>
        /// <param name="logger">日志记录器（可选）</param>
        /// <returns>是否成功保存</returns>
        public async Task<bool> SetCacheAsync<T>(bool redisAvailable, string cacheKey, T data, TimeSpan expiry, ILogger? logger = null)
        {
            if (!redisAvailable || database == null || data == null)
            {
                return false;
            }

            try
            {
                await database.StringSetAsync(cacheKey, JsonConvert.SerializeObject(data), expiry).ConfigureAwait(false);
                logger?.LogInformation("数据已缓存到Redis，键: {CacheKey}", cacheKey);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Redis缓存写入失败，键: {CacheKey}", cacheKey);
                return false;
            }
        }

        /// <summary>
        /// 从Redis获取字符串缓存
        /// </summary>
        /// <param name="redisAvailable">Redis是否可用</param>
        /// <param name="cacheKey">缓存键</param>
        /// <param name="logger">日志记录器（可选）</param>
        /// <returns>缓存的字符串，如果不存在或出错则返回null</returns>
        public async Task<string?> GetStringCacheAsync(bool redisAvailable, string cacheKey, ILogger? logger = null)
        {
            if (!redisAvailable || database == null)
            {
                return null;
            }

            try
            {
                var redisResult = await database.StringGetAsync(cacheKey).ConfigureAwait(false);
                if (redisResult is { HasValue: true, IsNullOrEmpty: false })
                {
                    logger?.LogInformation("已从Redis缓存获取字符串，键: {CacheKey}", cacheKey);
                    return redisResult.ToString();
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Redis缓存读取失败，键: {CacheKey}", cacheKey);
            }

            return null;
        }

        /// <summary>
        /// 将字符串保存到Redis缓存
        /// </summary>
        /// <param name="redisAvailable">Redis是否可用</param>
        /// <param name="cacheKey">缓存键</param>
        /// <param name="value">要缓存的字符串</param>
        /// <param name="expiry">过期时间</param>
        /// <param name="logger">日志记录器（可选）</param>
        /// <returns>是否成功保存</returns>
        public async Task<bool> SetStringCacheAsync(bool redisAvailable, string cacheKey, string value, TimeSpan expiry, ILogger? logger = null)
        {
            if (!redisAvailable || database == null || string.IsNullOrEmpty(value))
            {
                return false;
            }

            try
            {
                await database.StringSetAsync(cacheKey, value, expiry).ConfigureAwait(false);
                logger?.LogInformation("字符串已缓存到Redis，键: {CacheKey}", cacheKey);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Redis缓存写入失败，键: {CacheKey}", cacheKey);
                return false;
            }
        }
    }
}

/// <summary>
/// 缓存键生成器
/// 用于统一生成缓存键，避免键冲突
/// </summary>
public static class CacheKeys
{
    private const string Prefix = "eduapi";

    /// <summary>
    /// 对包含特殊字符的参数进行安全编码
    /// 将逗号、冒号等特殊字符替换为安全字符
    /// </summary>
    private static string SafeEncode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "null";
        // 将可能导致冲突的字符替换为安全字符
        return value.Replace(":", "_c_").Replace(",", "_m_");
    }

    /// <summary>
    /// 生成成绩缓存键
    /// </summary>
    public static string Scores(string studentId, string semester)
        => $"{Prefix}:scores:{SafeEncode(studentId)}:{SafeEncode(semester)}";

    /// <summary>
    /// 生成学期结果缓存键
    /// </summary>
    public static string SemesterResult(string? studentId)
        => $"{Prefix}:semester_result:{SafeEncode(studentId)}";

    /// <summary>
    /// 生成当前学期缓存键
    /// </summary>
    public static string ThisSemester => $"{Prefix}:thisSemester";

    /// <summary>
    /// 生成考试安排缓存键
    /// </summary>
    public static string ExamArrangement(string? id)
        => $"{Prefix}:exam_arrangement:{SafeEncode(id)}";

    /// <summary>
    /// 生成支付Token缓存键
    /// </summary>
    public static string PaymentToken(string cardNum)
        => $"{Prefix}:payment:{SafeEncode(cardNum)}";

    /// <summary>
    /// 生成支付列表缓存键
    /// </summary>
    public static string PaymentList(string cardNum)
        => $"{Prefix}:paymentList:{SafeEncode(cardNum)}";

    /// <summary>
    /// 生成电子账户余额缓存键
    /// </summary>
    public static string ElectronicAccount(string cardNum)
        => $"{Prefix}:ele_acc:{SafeEncode(cardNum)}";

    /// <summary>
    /// 生成培养方案缓存键
    /// </summary>
    public static string TrainProgram(string id)
        => $"{Prefix}:train_program:{SafeEncode(id)}";

    /// <summary>
    /// 生成校车缓存键
    /// </summary>
    public static string Bus(string time)
        => $"{Prefix}:bus:{SafeEncode(time)}";

    /// <summary>
    /// 生成新校车数据缓存键
    /// </summary>
    public static string BusNewData(string time)
        => $"{Prefix}:bus_new_data:{SafeEncode(time)}";

    /// <summary>
    /// 生成课程缓存键
    /// </summary>
    public static string Courses(string studentId)
        => $"{Prefix}:courses:{SafeEncode(studentId)}";

    /// <summary>
    /// 生成研究生课程缓存键。
    /// <para>
    /// <b>必须与 <see cref="Courses"/> 分开</b>：同一个学号在本科生/研究生两个系统里
    /// 是两份不同的课表，共用键会让两种模式互相覆盖——而且不会报错，只会返回错系统数据。
    /// </para>
    /// </summary>
    public static string GraduateCourses(string studentId)
        => $"{Prefix}:graduate_courses:{SafeEncode(studentId)}";

    /// <summary>
    /// 生成电费余额缓存键
    /// </summary>
    public static string ElectricityBalance(string sourceUrl)
        => $"{Prefix}:electricity:balance:{SafeEncode(sourceUrl)}";

    /// <summary>
    /// 生成电费周明细缓存键
    /// </summary>
    public static string ElectricityWeeklyData(string sourceUrl)
        => $"{Prefix}:electricity:weekly:{SafeEncode(sourceUrl)}";

    public static string Categories()
        => $"{Prefix}:categories";

    /// <summary>
    /// 生成地图POI缓存键
    /// </summary>
    public static string MapPois()
        => $"{Prefix}:map:pois:all";

    /// <summary>
    /// 生成地图POI分类缓存键
    /// </summary>
    public static string MapPoiCategory(string category)
        => $"{Prefix}:map:pois:category:{SafeEncode(category)}";

    /// <summary>
    /// 生成地图POI校区缓存键
    /// </summary>
    public static string MapPoiCampus(string campus)
        => $"{Prefix}:map:pois:campus:{SafeEncode(campus)}";

    /// <summary>
    /// 生成地图POI详情缓存键
    /// </summary>
    public static string MapPoiDetail(int id)
        => $"{Prefix}:map:pois:detail:{id}";

    /// <summary>
    /// 生成地图分类列表缓存键
    /// </summary>
    public static string MapCategories()
        => $"{Prefix}:map:categories";

    /// <summary>
    /// 生成地图校区列表缓存键
    /// </summary>
    public static string MapCampuses()
        => $"{Prefix}:map:campuses";
}
