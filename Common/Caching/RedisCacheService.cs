using System.Text.Json;
using StackExchange.Redis;

namespace Common.Caching;

public class RedisCacheService : ICacheService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _connection;
    private readonly TimeSpan _defaultTtl;

    public RedisCacheService(IConnectionMultiplexer connection, TimeSpan defaultTtl)
    {
        _connection = connection;
        _defaultTtl = defaultTtl;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var value = await _connection.GetDatabase().StringGetAsync(key);

        if (value.IsNullOrEmpty)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(value!, SerializerOptions);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(value, SerializerOptions);

        await _connection.GetDatabase().StringSetAsync(key, payload, ttl ?? _defaultTtl);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _connection.GetDatabase().KeyDeleteAsync(key);
    }
}
