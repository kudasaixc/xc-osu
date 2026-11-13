// xc-osu! server — live connections bound to the session that opened them. xc softworks (kudasaixc)

using System.Collections.Concurrent;

namespace XcOsu.Server.Services;

/// <summary>
/// Every long-lived connection (SignalR hubs, the notification socket) is registered with the session it authenticated with.
/// When that session ends — sign-out, password or email change, reset, restriction, absolute expiry — the connection is closed,
/// even if it never invokes anything again. A normal token rotation keeps the session id, so matches survive it.
/// </summary>
public sealed class SessionConnections : IDisposable
{
    private sealed record Entry(int UserId, string SessionId, Action Abort);

    private readonly Accounts accounts;
    private readonly ILogger<SessionConnections> log;
    private readonly ConcurrentDictionary<long, Entry> entries = new();
    private readonly Timer sweeper;
    private long nextId;

    public SessionConnections(Accounts accounts, ILogger<SessionConnections> log)
    {
        this.accounts = accounts;
        this.log = log;
        accounts.SessionsRevoked += user => check(user);
        // catches what no code path announces: natural expiry and operator changes made directly in the database.
        sweeper = new Timer(_ => check(null), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public int Count => entries.Count;

    /// <summary>Returns null when the session is already gone (the caller must refuse the connection).</summary>
    public IDisposable? Register(int userId, string? sessionId, Action abort)
    {
        if (!accounts.HasActiveSession(userId, sessionId)) return null;
        long id = Interlocked.Increment(ref nextId);
        entries[id] = new Entry(userId, sessionId!, abort);
        return new Lease(this, id);
    }

    private void check(int? userId)
    {
        try
        {
            foreach (var (id, entry) in entries)
            {
                if (userId != null && entry.UserId != userId) continue;
                if (accounts.HasActiveSession(entry.UserId, entry.SessionId)) continue;
                if (!entries.TryRemove(id, out _)) continue;
                try { entry.Abort(); } catch { /* already closing */ }
            }
        }
        catch (Exception e) { log.LogWarning("Session sweep failed: {Type}", e.GetType().Name); }
    }

    public void Dispose() => sweeper.Dispose();

    private sealed class Lease(SessionConnections owner, long id) : IDisposable
    {
        public void Dispose() => owner.entries.TryRemove(id, out _);
    }
}
