// xc-osu! server — metadata hub: presence, friends, beatmap updates. xc softworks (kudasaixc)

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online.Metadata;
using osu.Game.Users;
using XcOsu.Server.Data;
using XcOsu.Server.Services;

namespace XcOsu.Server.Hubs
{
    [Authorize(AuthenticationSchemes = XcTokenAuthHandler.SCHEME)]
    public sealed class MetadataHub : Hub<IMetadataClient>
    {
        private const string presence_watchers = "presence-watchers";

        private readonly Presence presence;
        private readonly Db db;
        private readonly DailyChallenge daily;

        public MetadataHub(Presence presence, Db db, DailyChallenge daily)
        {
            this.presence = presence;
            this.db = db;
            this.daily = daily;
        }

        private int me => Context.User!.UserId() ?? throw new HubException("unauthorised");

        private static string friendsOf(int userId) => $"friends-of:{userId}";

        public override async Task OnConnectedAsync()
        {
            int u = me;
            presence.Connect(u);
            await RefreshFriends();
            await broadcast(u);
            await Clients.Caller.DailyChallengeUpdated(daily.Info);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            int u = me;
            if (presence.Disconnect(u))
            {
                await Clients.Group(presence_watchers).UserPresenceUpdated(u, null);
                await Clients.Group(friendsOf(u)).FriendPresenceUpdated(u, null);
            }

            await base.OnDisconnectedAsync(exception);
        }

        private async Task broadcast(int u)
        {
            var p = presence.Online.TryGetValue(u, out var e) && e.Status != UserStatus.Offline ? presence.Get(u) : null;
            await Clients.Group(presence_watchers).UserPresenceUpdated(u, p);
            await Clients.Group(friendsOf(u)).FriendPresenceUpdated(u, p);
        }

        public Task<BeatmapUpdates> GetChangesSince(int queueId) => Task.FromResult(new BeatmapUpdates(Array.Empty<int>(), queueId));

        public async Task UpdateActivity(UserActivity? activity)
        {
            int u = me;
            if (presence.Online.TryGetValue(u, out var e)) e.Activity = activity;
            await broadcast(u);
        }

        public async Task UpdateStatus(UserStatus? status)
        {
            int u = me;
            if (presence.Online.TryGetValue(u, out var e)) e.Status = status;
            await broadcast(u);
        }

        public async Task BeginWatchingUserPresence()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, presence_watchers);
            foreach (var (id, e) in presence.Online)
                if (e.Connections > 0 && e.Status != UserStatus.Offline)
                    await Clients.Caller.UserPresenceUpdated(id, new UserPresence { Status = e.Status, Activity = e.Activity });
        }

        public Task EndWatchingUserPresence() => Groups.RemoveFromGroupAsync(Context.ConnectionId, presence_watchers);

        public async Task<MultiplayerPlaylistItemStats[]> BeginWatchingMultiplayerRoom(long id)
        {
            using (var c = db.Open())
            {
                string? json = c.Scalar<string>("SELECT json FROM rooms WHERE id = @i", ("@i", id));
                if (json == null) throw new HubException("Room not found.");
                var room = Newtonsoft.Json.Linq.JObject.Parse(json);
                if ((room.Value<bool?>("has_password") == true || room.Value<string>("category") == "matchmaking")
                    && MultiplayerHub.RoomOf(me) != id) throw new HubException("Room is private.");
            }
            var watched = Context.Items.TryGetValue("watched-rooms", out var value) ? (HashSet<long>)value! : new HashSet<long>();
            if (!watched.Contains(id) && watched.Count >= 16) throw new HubException("Too many watched rooms.");
            watched.Add(id);
            Context.Items["watched-rooms"] = watched;
            await Groups.AddToGroupAsync(Context.ConnectionId, DailyChallenge.RoomWatchGroup(id));
            return daily.RoomStats(id);
        }

        public Task EndWatchingMultiplayerRoom(long id)
        {
            if (Context.Items.TryGetValue("watched-rooms", out var value)) ((HashSet<long>)value!).Remove(id);
            return Groups.RemoveFromGroupAsync(Context.ConnectionId, DailyChallenge.RoomWatchGroup(id));
        }

        public async Task RefreshFriends()
        {
            int u = me;
            List<int> friends;
            using (var c = db.Open())
                friends = c.Query("SELECT target_id FROM friends WHERE user_id = @u", r => r.GetInt32(0), ("@u", u));

            var previous = Context.Items.TryGetValue("friends", out var old) ? (HashSet<int>)old! : new HashSet<int>();
            foreach (int removed in previous.Except(friends))
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, friendsOf(removed));
                await Clients.Caller.FriendPresenceUpdated(removed, null);
            }
            Context.Items["friends"] = friends.ToHashSet();
            foreach (int f in friends)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, friendsOf(f));
                var p = presence.IsOnline(f) ? presence.Get(f) : null;
                await Clients.Caller.FriendPresenceUpdated(f, p);
            }
        }
    }
}
