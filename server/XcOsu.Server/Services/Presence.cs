// xc-osu! server: online presence (fed by the metadata hub). xc softworks (kudasaixc)

using System.Collections.Concurrent;
using osu.Game.Users;

namespace XcOsu.Server.Services
{
    public sealed class Presence
    {
        public sealed class Entry
        {
            public int Connections;
            public UserStatus? Status = UserStatus.Online;
            public UserActivity? Activity;
            public bool Watched;
        }

        public readonly ConcurrentDictionary<int, Entry> Online = new ConcurrentDictionary<int, Entry>();

        public bool IsOnline(int userId) => Online.TryGetValue(userId, out var e) && e.Connections > 0 && e.Status != UserStatus.Offline;

        public Entry Connect(int userId)
        {
            var e = Online.GetOrAdd(userId, _ => new Entry());
            Interlocked.Increment(ref e.Connections);
            return e;
        }

        public bool Disconnect(int userId)
        {
            if (!Online.TryGetValue(userId, out var e)) return true;
            if (Interlocked.Decrement(ref e.Connections) > 0) return false;
            Online.TryRemove(userId, out _);
            return true;
        }

        public UserPresence? Get(int userId) =>
            Online.TryGetValue(userId, out var e) && e.Connections > 0
                ? new UserPresence { Status = e.Status, Activity = e.Activity }
                : null;
    }
}
