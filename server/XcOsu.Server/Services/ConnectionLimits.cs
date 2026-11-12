namespace XcOsu.Server.Services;

public static class ConnectionLimits
{
    private static readonly object sync = new();
    private static readonly Dictionary<string, int> counts = new();
    public static IDisposable? Acquire(int user, string address)
    {
        string account = "user:" + user, ip = "ip:" + address;
        lock (sync)
        {
            // A normal game uses metadata, multiplayer, spectator and notification connections.
            if (counts.GetValueOrDefault(account) >= 32 || counts.GetValueOrDefault(ip) >= 128 || counts.Count >= 20000)
                return null;
            counts[account] = counts.GetValueOrDefault(account) + 1;
            counts[ip] = counts.GetValueOrDefault(ip) + 1;
            return new Lease(account, ip);
        }
    }
    private sealed class Lease(string account, string ip) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                foreach (string key in new[] { account, ip })
                    if (--counts[key] == 0) counts.Remove(key);
            }
        }
    }
}
