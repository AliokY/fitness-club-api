using StackExchange.Redis;

namespace WebApi.RateLimiting;

public sealed class EnrollmentRateLimiter(IConnectionMultiplexer redis)
{
    // Use a sliding window, do not count rejected 429 responses, and get time from the shared Redis server
    private const string Script = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - 60000)
        if redis.call('ZCARD', KEYS[1]) >= 60 then return 0 end
        redis.call('ZADD', KEYS[1], now, ARGV[1])
        redis.call('PEXPIRE', KEYS[1], 60000)
        return 1
        """;

    public async Task<bool> TryAcquireAsync(long userId)
    {
        var result = await redis.GetDatabase().ScriptEvaluateAsync(Script,
            new RedisKey[] { $"fitness:requests:{userId}" }, new RedisValue[] { Guid.NewGuid().ToString("N") });
        return (int)result == 1;
    }
}
