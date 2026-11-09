// xc-osu! server — multiplayer & playlists rooms. xc softworks (kudasaixc)

using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using MatchType = osu.Game.Online.Rooms.MatchType;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed class ServerRoom
    {
        public readonly object Lock = new object();
        public required long Id;
        public required JObject Json;
        public MultiplayerRoom? Live;
        public bool Realtime => Live != null;
        public readonly HashSet<int> Participants = new HashSet<int>();
        public CancellationTokenSource? Countdown;
    }

    public sealed class RoomStore
    {
        private readonly Db db;
        private readonly Mirrors mirrors;
        private long nextPlaylistItemId;

        public readonly ConcurrentDictionary<long, ServerRoom> Rooms = new ConcurrentDictionary<long, ServerRoom>();

        public RoomStore(Db db, Mirrors mirrors)
        {
            this.db = db;
            this.mirrors = mirrors;

            using var c = db.Open();
            nextPlaylistItemId = c.Scalar<long?>("SELECT MAX(id) FROM playlist_items") ?? 0;
            foreach (var (id, json) in c.Query("SELECT id, json FROM rooms WHERE active = 1", r => (r.GetInt64(0), r.GetString(1))))
            {
                var jo = JObject.Parse(json);
                if (jo.Value<DateTimeOffset?>("ends_at") is DateTimeOffset end && end < DateTimeOffset.UtcNow) continue;
                Rooms[id] = new ServerRoom { Id = id, Json = jo };
            }
        }

        public long NextPlaylistItemId()
        {
            long id = Interlocked.Increment(ref nextPlaylistItemId);
            using var c = db.Open();
            c.Exec("INSERT OR IGNORE INTO playlist_items (id, room_id, json) VALUES ($i, 0, '{}')", ("$i", id));
            return id;
        }

        public long AllocateRoomId(int hostId, string category, string type)
        {
            using var c = db.Open();
            return c.Insert("INSERT INTO rooms (json, host_id, category, type, created_at) VALUES ('{}', $h, $c, $t, $d)",
                ("$h", hostId), ("$c", category), ("$t", type), ("$d", DateTimeOffset.UtcNow.ToString("O")));
        }

        public void Persist(ServerRoom room)
        {
            using var c = db.Open();
            c.Exec("UPDATE rooms SET json = $j, active = $a, ends_at = $e WHERE id = $i",
                ("$j", room.Json.ToString(Formatting.None)), ("$a", room.Json.Value<string>("status") == "ended" ? 0 : 1),
                ("$e", room.Json.Value<string>("ends_at")), ("$i", room.Id));
        }

        public void Close(ServerRoom room)
        {
            Rooms.TryRemove(room.Id, out _);
            room.Json["ends_at"] = DateTimeOffset.UtcNow;
            room.Json["status"] = "ended";
            Persist(room);
        }

        /// <summary>API representation of a playlist item (with the beatmap attached from the mirror cache).</summary>
        public async Task<JObject> PlaylistItemJson(long id, int ownerId, int beatmapId, int rulesetId, IEnumerable<APIMod> required, IEnumerable<APIMod> allowed,
                                                    bool expired, ushort order, DateTimeOffset? playedAt, bool freestyle, CancellationToken ct = default)
        {
            var beatmap = await mirrors.GetBeatmap(beatmapId, null, null, ct);
            return new JObject
            {
                ["id"] = id,
                ["room_id"] = 0,
                ["owner_id"] = ownerId,
                ["ruleset_id"] = rulesetId,
                ["beatmap_id"] = beatmapId,
                ["beatmap"] = beatmap ?? new JObject { ["id"] = beatmapId },
                ["required_mods"] = JArray.FromObject(required),
                ["allowed_mods"] = JArray.FromObject(allowed),
                ["expired"] = expired,
                ["playlist_order"] = order,
                ["played_at"] = playedAt == null ? null : JToken.FromObject(playedAt.Value),
                ["freestyle"] = freestyle,
            };
        }

        /// <summary>Refreshes the API JSON of a realtime room from its live state (for the lounge listing).</summary>
        public async Task SyncJson(ServerRoom room, Users users, Accounts accounts)
        {
            MultiplayerRoom live;
            List<MultiplayerPlaylistItem> playlist;
            List<int> userIds;
            lock (room.Lock)
            {
                if (room.Live == null) return;
                live = room.Live;
                playlist = live.Playlist.ToList();
                userIds = live.Users.Select(u => u.UserID).ToList();
            }

            var items = new JArray();
            foreach (var p in playlist)
                items.Add(await PlaylistItemJson(p.ID, p.OwnerID, p.BeatmapID, p.RulesetID, p.RequiredMods, p.AllowedMods, p.Expired, p.PlaylistOrder, p.PlayedAt, p.Freestyle));

            var host = live.Host == null ? null : accounts.Get(live.Host.UserID);
            var participants = accounts.GetMany(userIds).Select(users.Compact).ToList();

            lock (room.Lock)
            {
                var j = room.Json;
                j["name"] = live.Settings.Name;
                j["has_password"] = !string.IsNullOrEmpty(live.Settings.Password);
                j["password"] = null;
                j["type"] = JToken.FromObject(live.Settings.MatchType switch { MatchType.TeamVersus => "team_versus", MatchType.Matchmaking => "matchmaking", _ => "head_to_head" });
                j["queue_mode"] = live.Settings.QueueMode switch { QueueMode.AllPlayers => "all_players", QueueMode.AllPlayersRoundRobin => "all_players_round_robin", _ => "host_only" };
                j["auto_skip"] = live.Settings.AutoSkip;
                j["auto_start_duration"] = (ushort)live.Settings.AutoStartDuration.TotalSeconds;
                j["max_participants"] = live.Settings.MaxParticipants;
                j["host"] = host == null ? null : JObject.FromObject(users.Compact(host), JsonSerializer.Create(Api.Json.SETTINGS));
                j["participant_count"] = participants.Count;
                j["recent_participants"] = JArray.FromObject(participants, JsonSerializer.Create(Api.Json.SETTINGS));
                j["playlist"] = items;
                j["current_playlist_item"] = items.FirstOrDefault(i => i.Value<long>("id") == live.Settings.PlaylistItemId);
                j["status"] = live.State == MultiplayerRoomState.Open ? "idle" : "playing";
                j["playlist_item_stats"] = new JObject
                {
                    ["count_active"] = playlist.Count(p => !p.Expired),
                    ["count_total"] = playlist.Count,
                    ["ruleset_ids"] = new JArray(playlist.Select(p => p.RulesetID).Distinct()),
                };
                var stars = playlist.Where(p => !p.Expired).Select(p => p.StarRating).DefaultIfEmpty(0).ToList();
                j["difficulty_range"] = new JObject { ["min"] = stars.Min(), ["max"] = stars.Max() };
            }
        }
    }
}
