using Microsoft.Extensions.Caching.Memory;

namespace XcOsu.Server.Services;

public static class LoginAttempts
{
    private sealed record Failure(int Count, DateTimeOffset RetryAfter);
    private static readonly MemoryCache failures = new(new MemoryCacheOptions { SizeLimit = 20000 });
    private static readonly object sync = new();
    public static bool CanTry(string account)
    {
        lock (sync)
            return !failures.TryGetValue(account, out Failure? failure) || failure!.RetryAfter <= DateTimeOffset.UtcNow;
    }
    public static void Record(string account, bool success)
    {
        lock (sync)
        {
            if (success) { failures.Remove(account); return; }
            failures.TryGetValue(account, out Failure? old);
            int count = Math.Min(10, (old?.Count ?? 0) + 1);
            int seconds = count < 3 ? 0 : Math.Min(30, 1 << (count - 3));
            failures.Set(account, new Failure(count, DateTimeOffset.UtcNow.AddSeconds(seconds)),
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });
        }
    }
}
