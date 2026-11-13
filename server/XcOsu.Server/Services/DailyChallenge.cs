// xc-osu! server: daily challenge (one ranked map per UTC day as a playlists room), streak/placement statistics,
// live room score feed for the metadata hub, and the background maintenance loop. xc softworks (kudasaixc)

using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API;
using osu.Game.Online.Metadata;
using XcOsu.Server.Data;
using XcOsu.Server.Hubs;

namespace XcOsu.Server.Services
{
    public sealed class DailyChallenge
    {
        public static string RoomWatchGroup(long roomId) => $"room-watch:{roomId}";

        private static readonly string[] allowed_mods = { "HD", "HR", "DT", "NC", "FL", "SD", "PF", "EZ", "NF", "HT", "DC", "MR" };

        private readonly Db db;
        private readonly RoomStore store;
        private readonly MapPool pool;
        private readonly Chat chat;
        private readonly Accounts accounts;
        private readonly Users users;
        private readonly IHubContext<MetadataHub, IMetadataClient> metadata;
        private readonly ILogger<DailyChallenge> log;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);

        public long? CurrentRoomId { get; private set; }

        public DailyChallenge(Db db, RoomStore store, MapPool pool, Chat chat, Accounts accounts, Users users, IHubContext<MetadataHub, IMetadataClient> metadata,
                              ILogger<DailyChallenge> log)
        {
            this.db = db;
            this.store = store;
            this.pool = pool;
            this.chat = chat;
            this.accounts = accounts;
            this.users = users;
            this.metadata = metadata;
            this.log = log;

            using var c = db.Open();
            CurrentRoomId = c.Scalar<long?>("SELECT room_id FROM daily_challenges WHERE day = @d", ("@d", today()));
        }

        private static string today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

        public DailyChallengeInfo? Info => CurrentRoomId is long id ? new DailyChallengeInfo { RoomID = id } : null;

        public bool IsDailyRoom(long? roomId)
        {
            if (roomId == null) return false;
            if (roomId == CurrentRoomId) return true;
            using var c = db.Open();
            return c.Scalar<long>("SELECT COUNT(*) FROM daily_challenges WHERE room_id = @r", ("@r", roomId)) > 0;
        }

        /// <summary>Makes sure today's challenge exists (creating it and closing yesterday's if needed).</summary>
        public async Task Ensure(CancellationToken ct = default)
        {
            await gate.WaitAsync(ct);
            try
            {
                string day = today();
                long? existing;
                using (var c = db.Open())
                    existing = c.Scalar<long?>("SELECT room_id FROM daily_challenges WHERE day = @d", ("@d", day));
                if (existing != null && store.Rooms.ContainsKey(existing.Value))
                {
                    CurrentRoomId = existing;
                    return;
                }

                // close and score the previous challenges
                List<(string day, long room)> previous;
                using (var c = db.Open())
                    previous = c.Query("SELECT day, room_id FROM daily_challenges WHERE day < @d", r => (r.GetString(0), r.GetInt64(1)), ("@d", day));
                foreach (var (_, room) in previous)
                {
                    if (!store.Rooms.TryGetValue(room, out var old)) continue;
                    store.Close(old);
                    if (old.ChannelId > 0) await chat.CloseRoomChannel(old.ChannelId);
                    awardPlacements(room);
                }

                if (existing != null)
                {
                    CurrentRoomId = existing;
                    return;
                }

                var date = DateTime.UtcNow.Date;
                var rng = new Random(date.GetHashCode());
                double stars = 4 + rng.NextDouble() * 2.5;
                var map = (await pool.Pick(1, stars, recentBeatmaps(), ct)).FirstOrDefault();
                if (map == null)
                {
                    log.LogWarning("daily challenge: no beatmap available from the mirrors yet");
                    return;
                }

                CurrentRoomId = await create(date, map, ct);
                await metadata.Clients.All.DailyChallengeUpdated(Info);
                log.LogInformation("daily challenge {Day}: room {Room}, beatmap {Beatmap} ({Stars:0.00}*)", day, CurrentRoomId, map.BeatmapId, map.Stars);
            }
            finally
            {
                gate.Release();
            }
        }

        private HashSet<int> recentBeatmaps()
        {
            using var c = db.Open();
            return c.Query("SELECT beatmap_id FROM daily_challenges", r => r.GetInt32(0)).ToHashSet();
        }

        private async Task<long> create(DateTime date, MapPool.Map map, CancellationToken ct)
        {
            int? hostId;
            using (var hc = db.Open())
                hostId = (int?)hc.Scalar<long?>("SELECT MIN(id) FROM users WHERE is_admin = 1 AND restricted = 0");
            var host = hostId is int h ? accounts.GetMany(new[] { h }).FirstOrDefault() : null;
            long id = store.AllocateRoomId(host?.Id ?? 0, "daily_challenge", "playlists");
            string name = $"Daily Challenge: {date:MMMM d, yyyy}";

            var item = await store.PlaylistItemJson(store.NextPlaylistItemId(), host?.Id ?? 0, map.BeatmapId, 0, Array.Empty<APIMod>(),
                allowed_mods.Select(a => new APIMod { Acronym = a }), false, 0, null, false, ct);
            item["room_id"] = id;

            int channel = chat.CreateRoomChannel(id, name);
            var serializer = JsonSerializer.Create(Api.Json.SETTINGS);
            var json = new JObject
            {
                ["id"] = id,
                ["name"] = name,
                ["description"] = "A new beatmap every day. Set your best score before the day ends!",
                ["category"] = "daily_challenge",
                ["type"] = "playlists",
                ["host"] = host == null ? null : JObject.FromObject(users.Compact(host), serializer),
                ["starts_at"] = new DateTimeOffset(date, TimeSpan.Zero),
                ["ends_at"] = new DateTimeOffset(date.AddDays(1), TimeSpan.Zero),
                ["duration"] = 24 * 60,
                ["max_attempts"] = null,
                ["participant_count"] = 0,
                ["recent_participants"] = new JArray(),
                ["playlist"] = new JArray(item),
                ["current_playlist_item"] = item,
                ["channel_id"] = channel,
                ["status"] = "idle",
                ["has_password"] = false,
                ["queue_mode"] = "host_only",
                ["playlist_item_stats"] = new JObject { ["count_active"] = 1, ["count_total"] = 1, ["ruleset_ids"] = new JArray(0) },
                ["difficulty_range"] = new JObject { ["min"] = map.Stars, ["max"] = map.Stars },
            };

            var room = new ServerRoom { Id = id, Json = json, ChannelId = channel };
            store.Rooms[id] = room;
            store.Persist(room);
            using var c = db.Open();
            c.Exec("INSERT INTO playlist_items (id, room_id, json) VALUES (@i, @r, @j) ON CONFLICT (id) DO UPDATE SET room_id = EXCLUDED.room_id, json = EXCLUDED.json", ("@i", item.Value<long>("id")), ("@r", id), ("@j", item.ToString(Formatting.None)));
            c.Exec("INSERT INTO daily_challenges (day, room_id, beatmap_id) VALUES (@d, @r, @b) ON CONFLICT (day) DO UPDATE SET room_id = EXCLUDED.room_id, beatmap_id = EXCLUDED.beatmap_id", ("@d", date.ToString("yyyy-MM-dd")), ("@r", id), ("@b", map.BeatmapId));
            return id;
        }

        /// <summary>Top 10% / top 50% placement counters, computed when a day's challenge closes.</summary>
        private void awardPlacements(long roomId)
        {
            using var c = db.Open();
            var board = c.Query("""
                SELECT user_id, MAX(total_score) FROM scores WHERE playlist_item_id IN (SELECT id FROM playlist_items WHERE room_id = @r) AND passed = 1
                GROUP BY user_id ORDER BY 2 DESC
                """, r => r.GetInt32(0), ("@r", roomId));
            for (int i = 0; i < board.Count; i++)
            {
                double percentile = (i + 1) / (double)board.Count;
                if (percentile <= 0.5)
                    c.Exec("UPDATE daily_challenge_stats SET top_50p = top_50p + 1, top_10p = top_10p + @t WHERE user_id = @u", ("@t", percentile <= 0.1 ? 1 : 0), ("@u", board[i]));
            }
        }

        private static DateTime weekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

        /// <summary>Score feed: daily challenge streaks + live updates for every room watched over the metadata hub.</summary>
        public async Task OnScore(Scores.SubmittedScore s)
        {
            if (s.RoomId is not long roomId || s.PlaylistItemId is not long itemId) return;

            if (IsDailyRoom(roomId) && s.Score.Passed)
            {
                using var c = db.Open();
                string? dayString = c.Scalar<string>("SELECT day FROM daily_challenges WHERE room_id = @r", ("@r", roomId));
                if (dayString != null)
                {
                    var day = DateTime.Parse(dayString);
                    var week = weekStart(day);
                    var row = c.Query("SELECT daily_streak_current, daily_streak_best, weekly_streak_current, weekly_streak_best, last_day, last_week FROM daily_challenge_stats WHERE user_id = @u",
                        r => (dc: r.GetInt32(0), db: r.GetInt32(1), wc: r.GetInt32(2), wb: r.GetInt32(3), ld: r.IsDBNull(4) ? (DateTime?)null : DateTime.Parse(r.GetString(4)),
                            lw: r.IsDBNull(5) ? (DateTime?)null : DateTime.Parse(r.GetString(5))), ("@u", s.Score.UserID)).FirstOrDefault();

                    if (row.ld?.Date != day.Date)
                    {
                        int dc = row.ld?.Date == day.Date.AddDays(-1) ? row.dc + 1 : 1;
                        int wc = row.lw?.Date == week ? row.wc : row.lw?.Date == week.AddDays(-7) ? row.wc + 1 : 1;
                        c.Exec("""
                            INSERT INTO daily_challenge_stats (user_id, playcount, daily_streak_current, daily_streak_best, weekly_streak_current, weekly_streak_best, last_day, last_week)
                            VALUES (@u, 1, @dc, @dc, @wc, @wc, @ld, @lw)
                            ON CONFLICT(user_id) DO UPDATE SET playcount = daily_challenge_stats.playcount + 1, daily_streak_current = @dc, daily_streak_best = GREATEST(daily_challenge_stats.daily_streak_best, @dc),
                                weekly_streak_current = @wc, weekly_streak_best = GREATEST(daily_challenge_stats.weekly_streak_best, @wc), last_day = @ld, last_week = @lw
                            """, ("@u", s.Score.UserID), ("@dc", dc), ("@wc", wc), ("@ld", new DateTimeOffset(day, TimeSpan.Zero).ToString("O")),
                            ("@lw", new DateTimeOffset(week, TimeSpan.Zero).ToString("O")));
                    }
                }
            }

            int? newRank = null;
            if (s.Score.Passed)
            {
                using var c = db.Open();
                long previousBest = c.Scalar<long?>("SELECT MAX(total_score) FROM scores WHERE playlist_item_id = @p AND user_id = @u AND passed = 1 AND id != @s",
                    ("@p", itemId), ("@u", s.Score.UserID), ("@s", (long)(s.Score.ID ?? 0))) ?? -1;
                if (s.Score.TotalScore > previousBest)
                {
                    var board = c.Query("""
                        SELECT user_id FROM (SELECT user_id, playlist_item_id, MAX(total_score) AS best FROM scores
                            WHERE playlist_item_id IN (SELECT id FROM playlist_items WHERE room_id = @r) AND passed = 1 GROUP BY user_id, playlist_item_id) t
                        GROUP BY user_id ORDER BY SUM(best) DESC, user_id
                        """, r => r.GetInt32(0), ("@r", roomId));
                    int idx = board.IndexOf(s.Score.UserID);
                    if (idx >= 0) newRank = idx + 1;
                }
            }

            await metadata.Clients.Group(RoomWatchGroup(roomId)).MultiplayerRoomScoreSet(new MultiplayerRoomScoreSetEvent
            {
                RoomID = roomId,
                PlaylistItemID = itemId,
                ScoreID = (long)(s.Score.ID ?? 0),
                UserID = s.Score.UserID,
                TotalScore = s.Score.TotalScore,
                NewRank = newRank,
            });
        }

        /// <summary>Score distribution per playlist item (the daily challenge's live histogram).</summary>
        public MultiplayerPlaylistItemStats[] RoomStats(long roomId)
        {
            using var c = db.Open();
            var items = c.Query("SELECT id FROM playlist_items WHERE room_id = @r", r => r.GetInt64(0), ("@r", roomId));
            var result = new List<MultiplayerPlaylistItemStats>();
            foreach (long item in items)
            {
                var stats = new MultiplayerPlaylistItemStats { PlaylistItemID = item };
                var rows = c.Query("SELECT id, total_score FROM scores WHERE playlist_item_id = @p AND passed = 1", r => (id: r.GetInt64(0), score: r.GetInt64(1)), ("@p", item));
                foreach (var (id, score) in rows)
                {
                    int bin = (int)Math.Clamp(score / 100_000, 0, MultiplayerPlaylistItemStats.TOTAL_SCORE_DISTRIBUTION_BINS - 1);
                    stats.TotalScoreDistribution[bin]++;
                    stats.CumulativeScore += score;
                    stats.LastProcessedScoreID = Math.Max(stats.LastProcessedScoreID, (ulong)id);
                }

                result.Add(stats);
            }

            return result.ToArray();
        }
    }

    /// <summary>Background upkeep: daily challenge rotation, daily rank snapshots, expiring playlists rooms.</summary>
    public sealed class Maintenance : BackgroundService
    {
        private readonly DailyChallenge daily;
        private readonly Users users;
        private readonly RoomStore store;
        private readonly Chat chat;
        private readonly Db db;
        private readonly ILogger<Maintenance> log;

        public Maintenance(DailyChallenge daily, Users users, RoomStore store, Chat chat, Db db, ILogger<Maintenance> log)
        {
            this.daily = daily;
            this.users = users;
            this.store = store;
            this.chat = chat;
            this.db = db;
            this.log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string? lastSnapshot = null;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                try
                {
                    await daily.Ensure(stoppingToken);

                    string day = DateTime.UtcNow.ToString("yyyy-MM-dd");
                    if (lastSnapshot != day)
                    {
                        using (var c = db.Open())
                            lastSnapshot = c.Scalar<string>("SELECT MAX(day) FROM rank_history");
                        if (lastSnapshot != day)
                        {
                            users.SnapshotRanks();
                            lastSnapshot = day;
                            log.LogInformation("rank history snapshot for {Day}", day);
                        }
                    }

                    foreach (var room in store.Rooms.Values.Where(r => !r.Realtime).ToList())
                    {
                        if (RoomStore.EndsAt(room.Json) is DateTimeOffset end && end < DateTimeOffset.UtcNow
                            && room.Json.Value<string>("category") != "daily_challenge")
                        {
                            store.Close(room);
                            if (room.ChannelId > 0) await chat.CloseRoomChannel(room.ChannelId);
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "maintenance pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
