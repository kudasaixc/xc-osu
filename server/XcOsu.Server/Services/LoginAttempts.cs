// xc-osu! server: password guessing protection. xc softworks (kudasaixc)

using Microsoft.Extensions.Caching.Memory;

namespace XcOsu.Server.Services;

/// <summary>
/// Back-off after failed sign-ins, kept per (account, address): somebody else failing on your name from their address
/// cannot lock you out of yours. An account-wide budget still caps guessing spread over many addresses, but an address that
/// signed in successfully to that account before keeps working through it.
/// </summary>
public static class LoginAttempts
{
    public const int ACCOUNT_ATTEMPTS_PER_MINUTE = 30;

    private sealed record Failure(int Count, DateTimeOffset RetryAfter);
    private static readonly MemoryCache failures = new(new MemoryCacheOptions { SizeLimit = 200_000 });
    private static readonly MemoryCache trusted = new(new MemoryCacheOptions { SizeLimit = 200_000 });
    private static readonly object sync = new();

    private static string pair(string account, string address) => account + "|" + address;

    public static bool CanTry(string account, string address)
    {
        lock (sync)
        {
            if (failures.TryGetValue(pair(account, address), out Failure? failure) && failure!.RetryAfter > DateTimeOffset.UtcNow) return false;
        }
        // the account-wide budget only binds addresses that never got in.
        return AbuseGuard.Allow("login-account:" + account, ACCOUNT_ATTEMPTS_PER_MINUTE, TimeSpan.FromMinutes(1)) || trusted.TryGetValue(pair(account, address), out _);
    }

    public static void Record(string account, string address, bool success)
    {
        string key = pair(account, address);
        lock (sync)
        {
            if (success)
            {
                failures.Remove(key);
                trusted.Set(key, true, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromDays(30) });
                return;
            }
            failures.TryGetValue(key, out Failure? old);
            int count = Math.Min(10, (old?.Count ?? 0) + 1);
            int seconds = count < 3 ? 0 : Math.Min(60, 1 << (count - 2));
            failures.Set(key, new Failure(count, DateTimeOffset.UtcNow.AddSeconds(seconds)),
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });
        }
    }
}
