namespace XAUAT.EduApi.Caching;

/// <summary>
/// 缓存级别
/// </summary>
public enum CacheLevel
{
    /// <summary>
    /// 本地内存缓存（一级缓存）
    /// </summary>
    Local = 1,
    
    /// <summary>
    /// 分布式缓存（二级缓存）
    /// </summary>
    Distributed = 2,
    
    /// <summary>
    /// 多级缓存（本地+分布式）
    /// </summary>
    MultiLevel = 3
}

/// <summary>
/// 缓存策略类型
/// </summary>
public enum CacheStrategyType
{
    /// <summary>
    /// 固定TTL策略
    /// </summary>
    FixedTtl = 1,
    
    /// <summary>
    /// LRU（最近最少使用）策略
    /// </summary>
    Lru = 2,
    
    /// <summary>
    /// LFU（最不经常使用）策略
    /// </summary>
    Lfu = 3,
    
    /// <summary>
    /// 混合策略（TTL + LRU/LFU）
    /// </summary>
    Hybrid = 4
}

/// <summary>
/// 缓存项的只读视图。
/// <para>
/// <see cref="CacheItem{T}"/> 是不变（invariant）泛型，用
/// <c>CacheItem&lt;object&gt;</c> 去取一个以 <c>CacheItem&lt;string&gt;</c> 存入的条目
/// 时类型对不上，会被当成"不存在"。凡是<b>不关心值类型</b>的地方（如存在性检查）
/// 一律通过这个接口访问。
/// </para>
/// </summary>
public interface ICacheItem
{
    /// <summary>
    /// 是否过期
    /// </summary>
    bool IsExpired { get; }
}

/// <summary>
/// 缓存项
/// </summary>
/// <typeparam name="T">缓存值类型</typeparam>
public class CacheItem<T> : ICacheItem
{
    /// <summary>
    /// 缓存键
    /// </summary>
    public string Key { get; set; } = string.Empty;
    
    /// <summary>
    /// 缓存值
    /// </summary>
    public T? Value { get; set; }
    
    /// <summary>
    /// 缓存创建时间
    /// </summary>
    public DateTime CreatedTime { get; set; }
    
    /// <summary>
    /// 缓存过期时间
    /// </summary>
    public DateTime? ExpirationTime { get; set; }
    
    /// <summary>
    /// 缓存命中率
    /// </summary>
    public int HitCount { get; set; }
    
    /// <summary>
    /// 最后访问时间
    /// </summary>
    public DateTime LastAccessTime { get; set; }
    
    /// <summary>
    /// 缓存级别
    /// </summary>
    public CacheLevel Level { get; set; }
    
    /// <summary>
    /// 业务优先级（1-10，10最高）
    /// </summary>
    public int BusinessPriority { get; set; } = 5;
    
    /// <summary>
    /// 是否过期
    /// </summary>
    public bool IsExpired => ExpirationTime.HasValue && DateTime.Now > ExpirationTime.Value;
    
    /// <summary>
    /// 剩余过期时间
    /// </summary>
    public TimeSpan? RemainingTime => ExpirationTime.HasValue ? ExpirationTime.Value - DateTime.Now : null;
}

/// <summary>
/// 缓存配置选项
/// </summary>
public class CacheOptions
{
    /// <summary>
    /// 缓存键前缀
    /// </summary>
    public string KeyPrefix { get; set; } = "EduApi:";
    
    /// <summary>
    /// 默认缓存过期时间
    /// </summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromHours(1);
    
    /// <summary>
    /// 缓存策略类型
    /// </summary>
    public CacheStrategyType StrategyType { get; set; } = CacheStrategyType.Hybrid;
    
    /// <summary>
    /// 本地缓存最大容量
    /// </summary>
    public int LocalCacheMaxSize { get; set; } = 1000;
    
    /// <summary>
    /// 预更新时间阈值（默认过期前5分钟）
    /// </summary>
    public TimeSpan PreUpdateThreshold { get; set; } = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// 缓存刷新间隔
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// 缓存预热项
/// </summary>
public class CacheWarmupItem
{
    /// <summary>
    /// 缓存键
    /// </summary>
    public string Key { get; set; } = string.Empty;
    
    /// <summary>
    /// 缓存值获取委托
    /// </summary>
    public Func<Task<object>> ValueFactory { get; set; } = () => Task.FromResult<object>(null!);
    
    /// <summary>
    /// 缓存过期时间
    /// </summary>
    public TimeSpan? Expiration { get; set; }
    
    /// <summary>
    /// 预热优先级（1-10，10最高）
    /// </summary>
    public int Priority { get; set; } = 5;
    
    /// <summary>
    /// 业务标签
    /// </summary>
    public string[] BusinessTags { get; set; } = [];
}

/// <summary>
/// 缓存统计信息
/// </summary>
public class CacheStatistics
{
    /// <summary>
    /// 缓存项总数
    /// </summary>
    public long TotalItems { get; set; }
    
    /// <summary>
    /// 命中次数
    /// </summary>
    public long HitCount { get; set; }
    
    /// <summary>
    /// 未命中次数
    /// </summary>
    public long MissCount { get; set; }
    
    /// <summary>
    /// 命中率
    /// </summary>
    public double HitRate => TotalRequests > 0 ? (double)HitCount / TotalRequests : 0;
    
    /// <summary>
    /// 总请求次数
    /// </summary>
    public long TotalRequests => HitCount + MissCount;
    
    /// <summary>
    /// 缓存占用内存大小（字节）
    /// </summary>
    public long MemorySize { get; set; }
    
    /// <summary>
    /// 过期项数量
    /// </summary>
    public long ExpiredItems { get; set; }
    
    /// <summary>
    /// 缓存级别统计
    /// </summary>
    public Dictionary<CacheLevel, long> LevelStatistics { get; set; } = new();
}
