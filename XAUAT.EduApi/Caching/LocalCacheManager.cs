using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.Extensions.Caching.Memory;

namespace XAUAT.EduApi.Caching;

/// <summary>
/// 本地内存缓存管理器
/// </summary>
internal class LocalCacheManager
{
    private readonly IMemoryCache _cache;
    private readonly CacheOptions _options;
    private readonly ConcurrentDictionary<string, CacheItemMetadata> _metadata;

    // 统计信息
    private long _hitCount;
    private long _missCount;
    private long _expiredItems;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="options">缓存配置选项</param>
    public LocalCacheManager(CacheOptions options)
    {
        _options = options;

        // 创建内存缓存配置
        var cacheOptions = new MemoryCacheOptions
        {
            SizeLimit = _options.LocalCacheMaxSize,
            ExpirationScanFrequency = _options.RefreshInterval,
            CompactionPercentage = 0.1
        };

        _cache = new MemoryCache(cacheOptions);
        _metadata = new ConcurrentDictionary<string, CacheItemMetadata>();
    }

    /// <summary>
    /// 获取缓存项
    /// </summary>
    /// <typeparam name="T">缓存值类型</typeparam>
    /// <param name="key">缓存键</param>
    /// <returns>缓存项，如果不存在则返回null</returns>
    public T? Get<T>(string key)
    {
        if (_cache.TryGetValue(key, out CacheItem<T>? cacheItem))
        {
            if (cacheItem is { IsExpired: false })
            {
                // 更新访问信息 (这里存在极小的并发竞态条件，但对于统计信息可以接受)
                cacheItem.HitCount++;
                cacheItem.LastAccessTime = DateTime.Now;

                // 更新缓存项
                Set(key, cacheItem.Value, cacheItem.RemainingTime, cacheItem.BusinessPriority);

                Interlocked.Increment(ref _hitCount);
                return cacheItem.Value;
            }
        }

        Interlocked.Increment(ref _missCount);
        return default;
    }

    /// <summary>
    /// 设置缓存项
    /// </summary>
    /// <typeparam name="T">缓存值类型</typeparam>
    /// <param name="key">缓存键</param>
    /// <param name="value">缓存值</param>
    /// <param name="expiration">过期时间</param>
    /// <param name="businessPriority">业务优先级</param>
    /// <returns>是否设置成功</returns>
    public bool Set<T>(string key, T? value, TimeSpan? expiration = null, int businessPriority = 5)
    {
        if (value == null)
        {
            return Remove(key);
        }

        var actualExpiration = expiration ?? _options.DefaultExpiration;
        var now = DateTime.Now;

        var cacheItem = new CacheItem<T>
        {
            Key = key,
            Value = value,
            CreatedTime = now,
            ExpirationTime = now.Add(actualExpiration),
            HitCount = 0,
            LastAccessTime = now,
            Level = CacheLevel.Local,
            BusinessPriority = businessPriority
        };

        // 创建缓存项选项
        var cacheEntryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = cacheItem.ExpirationTime,
            Size = 1, // 每个缓存项大小为1
            Priority = businessPriority >= 8 ? CacheItemPriority.NeverRemove : CacheItemPriority.Normal
        };

        // 添加过期回调
        cacheEntryOptions.RegisterPostEvictionCallback((cacheKey, cacheValue, reason, state) =>
        {
            if (reason != EvictionReason.Removed)
            {
                if (cacheKey != null)
                {
                    _metadata.TryRemove(cacheKey.ToString()!, out _);
                }
                
                if (reason == EvictionReason.Expired)
                {
                    Interlocked.Increment(ref _expiredItems);
                }
            }
        });

        // 设置缓存
        _cache.Set(key, cacheItem, cacheEntryOptions);

        // 更新元数据
        _metadata[key] = new CacheItemMetadata
        {
            Key = key,
            CreatedTime = now,
            ExpirationTime = now.Add(actualExpiration),
            BusinessPriority = businessPriority,
            HitCount = 0,
            LastAccessTime = now
        };

        return true;
    }

    /// <summary>
    /// 移除缓存项
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <returns>是否移除成功</returns>
    public bool Remove(string key)
    {
        _cache.Remove(key);
        return _metadata.TryRemove(key, out _);
    }

    /// <summary>
    /// 检查缓存项是否存在
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <returns>是否存在</returns>
    public bool Exists(string key)
    {
        // 这里不能用 CacheItem<object> 去取：存进来的是 CacheItem<T>，
        // 泛型不变，CacheItem<string> 不是 CacheItem<object>，取不到就等于永远"不存在"。
        // 只读视图 ICacheItem 与值类型无关，才是正确入口。
        if (_cache.TryGetValue(key, out var rawValue) && rawValue is ICacheItem cacheItem)
        {
            if (!cacheItem.IsExpired)
            {
                return true;
            }

            // 如果已过期，移除它
            Remove(key);
            Interlocked.Increment(ref _expiredItems);
        }

        return false;
    }

    /// <summary>
    /// 清除所有缓存项
    /// </summary>
    public void Clear()
    {
        // 清除所有缓存项
        foreach (var key in _metadata.Keys.ToList())
        {
            _cache.Remove(key);
        }

        _metadata.Clear();
    }

    /// <summary>
    /// 搜索缓存键
    /// </summary>
    /// <param name="pattern">键匹配模式</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>匹配的缓存键列表</returns>
    public IAsyncEnumerable<string> SearchKeysAsync(string pattern,
        CancellationToken cancellationToken = default)
    {
        return new AsyncEnumerableWrapper<string>([MustDisposeResource]() =>
        {
            var keys = _metadata.Keys.Where(key =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return WildcardMatch(key, pattern);
            });

            return keys.GetEnumerator();
        });
    }

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    /// <returns>缓存统计信息</returns>
    public LocalCacheStatistics GetStatistics()
    {
        var totalItems = _metadata.Count;
        var hitCount = Interlocked.Read(ref _hitCount);
        var missCount = Interlocked.Read(ref _missCount);
        var expiredItems = Interlocked.Read(ref _expiredItems);

        return new LocalCacheStatistics
        {
            TotalItems = totalItems,
            HitCount = hitCount,
            MissCount = missCount,
            HitRate = totalItems > 0 ? (double)hitCount / (hitCount + missCount) : 0,
            ExpiredItems = expiredItems,
            MemorySize = totalItems * 1024 // 粗略估计
        };
    }

    /// <summary>
    /// 通配符匹配
    /// </summary>
    /// <param name="input">输入字符串</param>
    /// <param name="pattern">匹配模式</param>
    /// <returns>是否匹配</returns>
    private static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return string.IsNullOrEmpty(input);
        }

        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 异步可枚举包装器
    /// </summary>
    /// <typeparam name="T">元素类型</typeparam>
    private class AsyncEnumerableWrapper<T>(Func<IEnumerator<T>> enumeratorFactory) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new AsyncEnumeratorWrapper<T>(enumeratorFactory(), cancellationToken);
        }
    }

    /// <summary>
    /// 异步枚举器包装器
    /// </summary>
    /// <typeparam name="T">元素类型</typeparam>
    private class AsyncEnumeratorWrapper<T>(IEnumerator<T> enumerator, CancellationToken cancellationToken)
        : IAsyncEnumerator<T>
    {
        public T Current => enumerator.Current;

        public ValueTask DisposeAsync()
        {
            enumerator.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(enumerator.MoveNext());
        }
    }

    /// <summary>
    /// 缓存项元数据
    /// </summary>
    private class CacheItemMetadata
    {
        /// <summary>
        /// 缓存键
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedTime { get; set; }

        /// <summary>
        /// 过期时间
        /// </summary>
        public DateTime? ExpirationTime { get; set; }

        /// <summary>
        /// 业务优先级
        /// </summary>
        public int BusinessPriority { get; set; }

        /// <summary>
        /// 命中次数
        /// </summary>
        public int HitCount { get; set; }

        /// <summary>
        /// 最后访问时间
        /// </summary>
        public DateTime LastAccessTime { get; set; }
    }

    /// <summary>
    /// 本地缓存统计信息
    /// </summary>
    public class LocalCacheStatistics
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
        public double HitRate { get; set; }

        /// <summary>
        /// 过期项数量
        /// </summary>
        public long ExpiredItems { get; set; }

        /// <summary>
        /// 内存大小（字节）
        /// </summary>
        public long MemorySize { get; set; }
    }
}