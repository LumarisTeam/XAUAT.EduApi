using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using StackExchange.Redis;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

public class StudentRateLimitState : IStudentRateLimitState
{
    private static readonly TimeSpan[] CooldownSteps =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4)
    ];

    private const string RedisKeyPrefix = "EduApi:rate_limit:";

    private readonly ConcurrentDictionary<string, StudentRateLimitEntry> _states = new();
    private readonly IDatabase? _redis;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public StudentRateLimitState(IConnectionMultiplexer? redis = null)
    {
        if (redis != null)
        {
            try
            {
                _redis = redis.GetDatabase();
            }
            catch
            {
                _redis = null;
            }
        }
    }

    public bool TryGetBlockedUntil(string identityKey, out DateTimeOffset blockedUntil)
    {
        blockedUntil = default;

        if (string.IsNullOrWhiteSpace(identityKey))
        {
            return false;
        }

        if (_states.TryGetValue(identityKey, out var entry))
        {
            if (entry.BlockedUntil > DateTimeOffset.UtcNow)
            {
                blockedUntil = entry.BlockedUntil;
                return true;
            }

            _states.TryRemove(identityKey, out _);
            return false;
        }

        if (_redis != null && TryGetFromRedis(identityKey, out var redisEntry))
        {
            _states.TryAdd(identityKey, redisEntry);
            if (redisEntry.BlockedUntil > DateTimeOffset.UtcNow)
            {
                blockedUntil = redisEntry.BlockedUntil;
                return true;
            }
        }

        return false;
    }

    public TimeSpan MarkRateLimited(string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey))
        {
            return TimeSpan.Zero;
        }

        var now = DateTimeOffset.UtcNow;
        var entry = _states.AddOrUpdate(
            identityKey,
            _ => CreateEntry(1, now),
            (_, current) =>
            {
                var nextHitCount = Math.Min(current.HitCount + 1, CooldownSteps.Length);
                return CreateEntry(nextHitCount, now);
            });

        PersistToRedis(identityKey, entry);

        return entry.BlockedUntil - now;
    }

    public void MarkSuccess(string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey))
        {
            return;
        }

        _states.TryRemove(identityKey, out _);
        RemoveFromRedis(identityKey);
    }

    private static StudentRateLimitEntry CreateEntry(int hitCount, DateTimeOffset now)
    {
        var cooldown = CooldownSteps[hitCount - 1];
        return new StudentRateLimitEntry
        {
            HitCount = hitCount,
            LastRateLimitedAt = now,
            BlockedUntil = now.Add(cooldown)
        };
    }

    private bool TryGetFromRedis(string identityKey, out StudentRateLimitEntry entry)
    {
        entry = default!;
        if (_redis == null) return false;

        try
        {
            var redisKey = GetRedisKey(identityKey);
            var value = _redis.StringGet(redisKey);
            if (value.IsNull) return false;

            var dto = JsonSerializer.Deserialize<RateLimitRedisDto>(value.ToString(), JsonOptions);
            if (dto == null) return false;

            entry = new StudentRateLimitEntry
            {
                HitCount = dto.HitCount,
                LastRateLimitedAt = dto.LastRateLimitedAt,
                BlockedUntil = dto.BlockedUntil
            };
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Redis read failed for rate limit key {identityKey}: {ex.Message}");
            return false;
        }
    }

    private void PersistToRedis(string identityKey, StudentRateLimitEntry entry)
    {
        if (_redis == null) return;

        try
        {
            var redisKey = GetRedisKey(identityKey);
            var ttl = entry.BlockedUntil - DateTimeOffset.UtcNow;
            if (ttl <= TimeSpan.Zero) return;

            var dto = new RateLimitRedisDto
            {
                HitCount = entry.HitCount,
                LastRateLimitedAt = entry.LastRateLimitedAt,
                BlockedUntil = entry.BlockedUntil
            };
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            _redis.StringSet(redisKey, json, ttl, flags: CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Redis write failed for rate limit key {identityKey}: {ex.Message}");
        }
    }

    private void RemoveFromRedis(string identityKey)
    {
        if (_redis == null) return;

        try
        {
            _redis.KeyDelete(GetRedisKey(identityKey), CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Redis delete failed for rate limit key {identityKey}: {ex.Message}");
        }
    }

    private static RedisKey GetRedisKey(string identityKey)
    {
        return $"{RedisKeyPrefix}{identityKey}";
    }

    private sealed class StudentRateLimitEntry
    {
        public int HitCount { get; init; }
        public DateTimeOffset LastRateLimitedAt { get; init; }
        public DateTimeOffset BlockedUntil { get; init; }
    }

    private sealed class RateLimitRedisDto
    {
        public int HitCount { get; set; }
        public DateTimeOffset LastRateLimitedAt { get; set; }
        public DateTimeOffset BlockedUntil { get; set; }
    }
}
