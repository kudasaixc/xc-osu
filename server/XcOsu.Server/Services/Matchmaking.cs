// xc-osu! server — quick play (2-8 players, 5 rounds, points by placement) and ranked play (1v1 card duel), matchmaker queues, duels and ratings.
// The stage machine mirrors the lazer client's expectations (MatchmakingStage / RankedPlayStage). xc softworks (kudasaixc)

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Multiplayer.MatchTypes.Matchmaking;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Game.Online.RankedPlay;
using osu.Game.Online.Rooms;
using osu.Game.Online;
using MatchType = osu.Game.Online.Rooms.MatchType;
using XcOsu.Server.Data;
using XcOsu.Server.Hubs;

namespace XcOsu.Server.Services
{
    /// <summary>Ranked osu! beatmaps (from the mirrors) used for quick play, ranked play and the daily challenge.</summary>
    public sealed class MapPool
    {
        public sealed record Map(int BeatmapId, int SetId, string Checksum, double Stars, double Length);

        private readonly Mirrors mirrors;
        private readonly ILogger<MapPool> log;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        private List<Map> maps = new List<Map>();
        private DateTimeOffset fetchedAt;

        public MapPool(Mirrors mirrors, ILogger<MapPool> log)
        {
            this.mirrors = mirrors;
            this.log = log;
        }

        public async Task<IReadOnlyList<Map>> All(CancellationToken ct = default)
        {
            if (maps.Count > 0 && fetchedAt > DateTimeOffset.UtcNow.AddHours(-6)) return maps;

            await gate.WaitAsync(ct);
            try
            {
                if (maps.Count > 0 && fetchedAt > DateTimeOffset.UtcNow.AddHours(-6)) return maps;

                var list = new List<Map>();
                var seen = new HashSet<int>();
                foreach (string sort in new[] { "plays_desc" })
                {
                    for (int page = 0; page < 12; page++)
                    {
                        var query = new QueryCollection(new Dictionary<string, StringValues> { ["m"] = "0", ["s"] = "ranked", ["sort"] = sort, ["nsfw"] = "false" });
                        var sets = await mirrors.Search(query, page, ct);
                        foreach (var set in sets.OfType<Newtonsoft.Json.Linq.JObject>())
                        {
                            foreach (var b in (set["beatmaps"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray()).OfType<Newtonsoft.Json.Linq.JObject>())
                            {
                                int mode = b.Value<int?>("mode_int") ?? (b.Value<string>("mode") == "osu" ? 0 : -1);
                                string? checksum = b.Value<string>("checksum");
                                double length = b.Value<double?>("total_length") ?? 0;
                                if (mode != 0 || checksum == null || length < 45 || length > 330) continue;
                                if (Scores.BeatmapStatus(b) is not (1 or 2)) continue;
                                int id = b.Value<int>("id");
                                if (!seen.Add(id)) continue;
                                list.Add(new Map(id, b.Value<int?>("beatmapset_id") ?? set.Value<int>("id"), checksum, b.Value<double?>("difficulty_rating") ?? 0, length));
                            }
                        }
                    }
                }

                if (list.Count > 0)
                {
                    maps = list;
                    fetchedAt = DateTimeOffset.UtcNow;
                    log.LogInformation("map pool refreshed: {Count} ranked osu! beatmaps", list.Count);
                }

                return maps;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Picks <paramref name="count"/> beatmaps from distinct sets, as close to <paramref name="targetStars"/> as the pool allows.</summary>
        public async Task<List<Map>> Pick(int count, double targetStars, ICollection<int>? excludeBeatmaps = null, CancellationToken ct = default)
        {
            var all = await All(ct);
            foreach (double spread in new[] { 0.4, 0.75, 1.25, 2, 100 })
            {
                var candidates = all.Where(m => Math.Abs(m.Stars - targetStars) <= spread && excludeBeatmaps?.Contains(m.BeatmapId) != true)
                                    .OrderBy(_ => Random.Shared.Next()).DistinctBy(m => m.SetId).Take(count).ToList();
                if (candidates.Count >= count || spread >= 100) return candidates;
            }

            return new List<Map>();
        }

        public static double StarsForRating(double rating) => Math.Clamp(4 + (rating - 1500) / 300, 1.5, 8.5);
    }

    public sealed class Matchmaking
    {
        public const int QUICK_PLAY_POOL = 1;
        public const int RANKED_PLAY_POOL = 2;

        public static readonly MatchmakingPool[] POOLS =
        {
            new MatchmakingPool { Id = QUICK_PLAY_POOL, RulesetId = 0, Variant = 0, Name = "Quick Play", Type = MatchmakingPoolType.QuickPlay },
            new MatchmakingPool { Id = RANKED_PLAY_POOL, RulesetId = 0, Variant = 0, Name = "Ranked Play", Type = MatchmakingPoolType.RankedPlay },
        };

        public static string LobbyGroup(int poolId) => $"mm-lobby:{poolId}";

        private const int quick_play_max_players = 8;
        private const int quick_play_rounds = 5;
        private const int quick_play_candidates = 12;
        private const int ranked_hand_size = 5;
        private static readonly int[] placement_points = { 8, 7, 6, 5, 4, 3, 2, 1 };

        private static readonly TimeSpan invitation_timeout = TimeSpan.FromSeconds(15);

        private readonly RoomStore store;
        private readonly Db db;
        private readonly Accounts accounts;
        private readonly Users users;
        private readonly MapPool pool;
        private readonly IServiceProvider services;
        private readonly IHubContext<MultiplayerHub, IXcMultiplayerClient> hub;
        private readonly ILogger<Matchmaking> log;

        private sealed class QueueEntry
        {
            public required int UserId;
            public required int PoolId;
            public required double Rating;
            public DateTimeOffset JoinedAt = DateTimeOffset.UtcNow;
        }

        private sealed class Invitation
        {
            public required int PoolId;
            public required List<QueueEntry> Entries;
            public readonly HashSet<int> Accepted = new HashSet<int>();
            public readonly HashSet<int> Declined = new HashSet<int>();
            public readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
            public bool Resolved;
        }

        private sealed record Duel(Guid Id, int From, int To, int PoolId, DateTimeOffset At);

        /// <summary>A running quick play / ranked play match.</summary>
        private sealed class Match
        {
            public required ServerRoom Room;
            public required int PoolId;
            public required HashSet<int> Invited;
            public readonly HashSet<int> Joined = new HashSet<int>();
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1);
            public int StageVersion;
            public DateTimeOffset StageStartedAt = DateTimeOffset.UtcNow;
            public bool Finished;

            // quick play
            public readonly Dictionary<int, HashSet<long>> Selections = new Dictionary<int, HashSet<long>>();

            // ranked play
            public readonly Dictionary<Guid, MultiplayerPlaylistItem> Cards = new Dictionary<Guid, MultiplayerPlaylistItem>();
            public readonly Dictionary<Guid, int> CardOwners = new Dictionary<Guid, int>();
            public readonly HashSet<int> Discarded = new HashSet<int>();
            public readonly List<MapPool.Map> Deck = new List<MapPool.Map>();
            public RankedPlayCardItem? PlayedCard;

            public MultiplayerRoom Live => Room.Live!;
            public bool Ranked => PoolId == RANKED_PLAY_POOL;
        }

        private readonly object queueLock = new object();
        private readonly List<QueueEntry> queue = new List<QueueEntry>();
        private readonly Dictionary<int, Invitation> invitations = new Dictionary<int, Invitation>();
        private readonly ConcurrentDictionary<Guid, Duel> duels = new ConcurrentDictionary<Guid, Duel>();
        private readonly ConcurrentDictionary<long, Match> matches = new ConcurrentDictionary<long, Match>();
        private readonly ConcurrentDictionary<int, int> lobby = new ConcurrentDictionary<int, int>();
        private static int countdown_ids = 1_000_000;

        public Matchmaking(RoomStore store, Db db, Accounts accounts, Users users, MapPool pool, IServiceProvider services,
                           IHubContext<MultiplayerHub, IXcMultiplayerClient> hub, ILogger<Matchmaking> log)
        {
            this.store = store;
            this.db = db;
            this.accounts = accounts;
            this.users = users;
            this.pool = pool;
            this.services = services;
            this.hub = hub;
            this.log = log;

            _ = Task.Run(loop);
        }

        /// <summary>Hub instance for room operations that don't need a caller context (state changes, match start).</summary>
        private MultiplayerHub engine() => ActivatorUtilities.CreateInstance<MultiplayerHub>(services);

        private static IXcMultiplayerClient? client(IHubContext<MultiplayerHub, IXcMultiplayerClient> hub, int userId) =>
            MultiplayerHub.user_connections.TryGetValue(userId, out string? conn) ? hub.Clients.Client(conn) : null;

        private IXcMultiplayerClient group(Match m) => hub.Clients.Group($"room:{m.Room.Id}");

        // ------------------------------------------------------------------ ratings

        private (double rating, double sigma) rating(int userId, int poolId)
        {
            using var c = db.Open();
            return c.Query("SELECT rating, sigma FROM matchmaking_ratings WHERE user_id = @u AND pool_id = @p", r => (r.GetDouble(0), r.GetDouble(1)), ("@u", userId), ("@p", poolId))
                    .FirstOrDefault((1500, 350));
        }

        /// <summary>Multi-player Elo: every pair of players is a duel decided by final placement. Returns the new ratings.</summary>
        private Dictionary<int, int> applyRatings(int poolId, IReadOnlyList<int> orderedUsers, IReadOnlyDictionary<int, int> points)
        {
            var current = orderedUsers.ToDictionary(u => u, u => rating(u, poolId));
            var result = new Dictionary<int, int>();
            int n = orderedUsers.Count;

            using var c = db.Open();
            for (int i = 0; i < n; i++)
            {
                int u = orderedUsers[i];
                var (r, sigma) = current[u];
                double k = 16 + 48 * Math.Clamp((sigma - 60) / 290, 0, 1);
                double delta = 0;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    double expected = 1 / (1 + Math.Pow(10, (current[orderedUsers[j]].rating - r) / 400));
                    double actual = i < j ? 1 : 0;
                    delta += actual - expected;
                }

                double newRating = r + (n > 1 ? k * delta / (n - 1) * Math.Min(2, Math.Sqrt(n - 1)) : 0);
                double newSigma = Math.Max(60, sigma * 0.88);
                c.Exec("""
                    INSERT INTO matchmaking_ratings (user_id, pool_id, rating, sigma, plays, first_placements, total_points) VALUES (@u, @p, @r, @s, 1, @f, @t)
                    ON CONFLICT(user_id, pool_id) DO UPDATE SET rating = @r, sigma = @s, plays = matchmaking_ratings.plays + 1, first_placements = matchmaking_ratings.first_placements + @f, total_points = matchmaking_ratings.total_points + @t
                    """, ("@u", u), ("@p", poolId), ("@r", newRating), ("@s", newSigma), ("@f", i == 0 ? 1 : 0), ("@t", points.GetValueOrDefault(u)));
                result[u] = (int)Math.Round(newRating);
            }

            return result;
        }

        // ------------------------------------------------------------------ lobby

        private MatchmakingLobbyStatus lobbyStatus(int poolId, int? userId)
        {
            int[] inQueue;
            lock (queueLock) inQueue = queue.Where(q => q.PoolId == poolId).Select(q => q.UserId).ToArray();

            using var c = db.Open();
            var distribution = c.Query("SELECT trunc(rating / 100)::int * 100, COUNT(*) FROM matchmaking_ratings WHERE pool_id = @p GROUP BY 1 ORDER BY 1",
                r => (r.GetInt32(0), r.GetInt32(1)), ("@p", poolId)).ToArray();

            var status = new MatchmakingLobbyStatus { UsersInQueue = inQueue, RatingDistribution = distribution };
            if (userId != null)
            {
                var (r, sigma) = rating(userId.Value, poolId);
                status.UserRating = (int)Math.Round(r);
                status.UserSigma = sigma;
                status.UserRank = (int)c.Scalar<long>("SELECT COUNT(*) FROM matchmaking_ratings WHERE pool_id = @p AND rating > @r", ("@p", poolId), ("@r", r)) + 1;
            }

            return status;
        }

        public async Task JoinLobby(int userId, int poolId)
        {
            lobby[userId] = poolId;
            if (client(hub, userId) is { } cl) await cl.MatchmakingLobbyStatusChanged(lobbyStatus(poolId, userId));
        }

        private async Task broadcastLobby(int poolId)
        {
            foreach (var (u, p) in lobby)
                if (p == poolId && client(hub, u) is { } cl)
                    await cl.MatchmakingLobbyStatusChanged(lobbyStatus(poolId, u));
        }

        // ------------------------------------------------------------------ queue

        public async Task JoinQueue(int userId, int poolId)
        {
            if (POOLS.All(p => p.Id != poolId)) throw new HubException("Unknown pool.");
            if (accounts.Get(userId) is not { Restricted: false }) throw new HubException("Your account can't use matchmaking.");

            lock (queueLock)
            {
                queue.RemoveAll(q => q.UserId == userId);
                queue.Add(new QueueEntry { UserId = userId, PoolId = poolId, Rating = rating(userId, poolId).rating });
            }

            if (client(hub, userId) is { } cl)
            {
                await cl.MatchmakingQueueJoined();
                await cl.MatchmakingQueueStatusChanged(new MatchmakingQueueStatus.Searching());
            }

            await broadcastLobby(poolId);
        }

        public async Task LeaveQueue(int userId)
        {
            int? poolId = null;
            lock (queueLock)
            {
                var e = queue.FirstOrDefault(q => q.UserId == userId);
                if (e != null)
                {
                    poolId = e.PoolId;
                    queue.Remove(e);
                }

                if (invitations.TryGetValue(userId, out var inv))
                    inv.Declined.Add(userId);
            }

            foreach (var d in duels.Values.Where(d => d.From == userId)) duels.TryRemove(d.Id, out _);
            if (client(hub, userId) is { } cl) await cl.MatchmakingQueueLeft();
            if (poolId != null) await broadcastLobby(poolId.Value);
        }

        public async Task Disconnected(int userId)
        {
            lobby.TryRemove(userId, out _);
            await LeaveQueue(userId);
        }

        public async Task RespondToInvitation(int userId, bool accept)
        {
            Invitation? inv;
            lock (queueLock)
            {
                if (!invitations.TryGetValue(userId, out inv) || inv.Resolved) return;
                if (accept) inv.Accepted.Add(userId);
                else inv.Declined.Add(userId);
            }

            if (accept && client(hub, userId) is { } cl) await cl.MatchmakingQueueStatusChanged(new MatchmakingQueueStatus.JoiningMatch());
            if (!accept && client(hub, userId) is { } cl2) await cl2.MatchmakingQueueLeft();
            await tryResolve(inv, force: false);
        }

        public async Task IssueDuel(int from, int to, int poolId)
        {
            if (from == to) throw new HubException("You can't duel yourself.");
            if (POOLS.All(p => p.Id != poolId)) throw new HubException("Unknown pool.");
            if (client(hub, to) is not { } target) throw new HubException("That player isn't online right now.");
            using (var c = db.Open())
                if (c.Scalar<long>("SELECT COUNT(*) FROM blocks WHERE (user_id = @a AND target_id = @b) OR (user_id = @b AND target_id = @a)", ("@a", from), ("@b", to)) > 0)
                    throw new HubException("You can't duel this player.");

            var duel = new Duel(Guid.NewGuid(), from, to, poolId, DateTimeOffset.UtcNow);
            duels[duel.Id] = duel;
            await target.MatchmakingDuelIssued(new MatchmakingDuelIssuedParams { Id = duel.Id, UserId = from, Pool = POOLS.First(p => p.Id == poolId) });
            if (client(hub, from) is { } self)
            {
                await self.MatchmakingQueueJoined();
                await self.MatchmakingQueueStatusChanged(new MatchmakingQueueStatus.Searching());
            }
        }

        public async Task AcceptDuel(int userId, Guid id)
        {
            if (!duels.TryRemove(id, out var duel) || duel.To != userId) throw new HubException("This duel has expired.");
            if (DateTimeOffset.UtcNow - duel.At > TimeSpan.FromMinutes(5)) throw new HubException("This duel has expired.");
            if (!MultiplayerHub.user_connections.ContainsKey(duel.From)) throw new HubException("Your opponent went offline.");

            lock (queueLock) queue.RemoveAll(q => q.UserId == duel.From || q.UserId == duel.To);
            var entries = new[] { duel.From, duel.To }.Select(u => new QueueEntry { UserId = u, PoolId = duel.PoolId, Rating = rating(u, duel.PoolId).rating }).ToList();
            foreach (var e in entries)
                if (client(hub, e.UserId) is { } cl)
                    await cl.MatchmakingQueueStatusChanged(new MatchmakingQueueStatus.JoiningMatch());
            await createMatch(duel.PoolId, entries);
        }

        private async Task loop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    await tick();
                }
                catch (Exception e)
                {
                    log.LogError(e, "matchmaking tick failed");
                }
            }
        }

        private async Task tick()
        {
            var now = DateTimeOffset.UtcNow;
            var newInvitations = new List<Invitation>();
            var expired = new List<Invitation>();

            lock (queueLock)
            {
                foreach (var inv in invitations.Values.Distinct())
                    if (!inv.Resolved && now - inv.CreatedAt > invitation_timeout)
                        expired.Add(inv);

                foreach (var poolDef in POOLS)
                {
                    var waiting = queue.Where(q => q.PoolId == poolDef.Id).OrderBy(q => q.JoinedAt).ToList();
                    if (waiting.Count < 2) continue;

                    List<QueueEntry> group;
                    if (poolDef.Type == MatchmakingPoolType.RankedPlay)
                    {
                        // pair the longest-waiting player with the closest rating inside a window that widens over time.
                        var first = waiting[0];
                        double window = 150 + 20 * (now - first.JoinedAt).TotalSeconds;
                        var opponent = waiting.Skip(1).OrderBy(q => Math.Abs(q.Rating - first.Rating)).First();
                        if (Math.Abs(opponent.Rating - first.Rating) > window) continue;
                        group = new List<QueueEntry> { first, opponent };
                    }
                    else
                    {
                        bool full = waiting.Count >= quick_play_max_players;
                        bool waitedLongEnough = now - waiting[0].JoinedAt > TimeSpan.FromSeconds(12);
                        if (!full && !waitedLongEnough) continue;
                        group = waiting.Take(quick_play_max_players).ToList();
                    }

                    foreach (var e in group) queue.Remove(e);
                    var invitation = new Invitation { PoolId = poolDef.Id, Entries = group };
                    foreach (var e in group) invitations[e.UserId] = invitation;
                    newInvitations.Add(invitation);
                }
            }

            foreach (var inv in newInvitations)
            {
                var type = POOLS.First(p => p.Id == inv.PoolId).Type;
                foreach (var e in inv.Entries)
                {
                    if (client(hub, e.UserId) is not { } cl)
                    {
                        lock (queueLock) inv.Declined.Add(e.UserId);
                        continue;
                    }

                    await cl.MatchmakingQueueStatusChanged(new MatchmakingQueueStatus.MatchFound());
                    await cl.MatchmakingRoomInvitedWithParams(new MatchmakingRoomInvitationParams { Type = type });
                }

                await broadcastLobby(inv.PoolId);
                await tryResolve(inv, force: false);
            }

            foreach (var inv in expired) await tryResolve(inv, force: true);

            // drop stale duel invitations
            foreach (var d in duels.Values.Where(d => now - d.At > TimeSpan.FromMinutes(5))) duels.TryRemove(d.Id, out _);

            // matches stuck in a stage (disconnected clients, lost countdowns) get pushed forward.
            foreach (var m in matches.Values)
                if (!m.Finished && now - m.StageStartedAt > TimeSpan.FromMinutes(12))
                    await advance(m, m.StageVersion);
        }

        private async Task tryResolve(Invitation inv, bool force)
        {
            List<QueueEntry> accepted;
            List<QueueEntry> others;
            lock (queueLock)
            {
                if (inv.Resolved) return;
                bool everyoneAnswered = inv.Entries.All(e => inv.Accepted.Contains(e.UserId) || inv.Declined.Contains(e.UserId));
                if (!everyoneAnswered && !force) return;
                inv.Resolved = true;
                foreach (var e in inv.Entries) invitations.Remove(e.UserId);
                accepted = inv.Entries.Where(e => inv.Accepted.Contains(e.UserId)).ToList();
                others = inv.Entries.Where(e => !inv.Accepted.Contains(e.UserId)).ToList();
            }

            foreach (var e in others)
                if (client(hub, e.UserId) is { } cl)
                    await cl.MatchmakingQueueLeft();

            int needed = 2;
            if (accepted.Count < needed)
            {
                // not enough players accepted: everyone who did goes back to the front of the queue.
                lock (queueLock)
                    foreach (var e in accepted)
                        queue.Insert(0, e);
                foreach (var e in accepted)
                    if (client(hub, e.UserId) is { } cl)
                        await cl.MatchmakingQueueStatusChanged(new MatchmakingQueueStatus.Searching());
                return;
            }

            if (inv.PoolId == RANKED_PLAY_POOL && accepted.Count > 2) accepted = accepted.Take(2).ToList();
            await createMatch(inv.PoolId, accepted);
        }

        // ------------------------------------------------------------------ match creation

        private MultiplayerPlaylistItem item(MapPool.Map map, int owner, ushort order) => new MultiplayerPlaylistItem
        {
            ID = store.NextPlaylistItemId(),
            OwnerID = owner,
            BeatmapID = map.BeatmapId,
            BeatmapChecksum = map.Checksum,
            RulesetID = 0,
            StarRating = map.Stars,
            RequiredMods = Array.Empty<APIMod>(),
            AllowedMods = Array.Empty<APIMod>(),
            PlaylistOrder = order,
        };

        private async Task createMatch(int poolId, List<QueueEntry> entries)
        {
            bool ranked = poolId == RANKED_PLAY_POOL;
            double avgRating = entries.Average(e => e.Rating);
            double stars = MapPool.StarsForRating(avgRating);
            int owner = entries.Min(e => e.UserId);

            var maps = await pool.Pick(ranked ? 30 : quick_play_candidates, stars);
            if (maps.Count < (ranked ? ranked_hand_size * 2 + 2 : 3))
            {
                log.LogWarning("matchmaking: map pool unavailable, can't create a match");
                foreach (var e in entries)
                    if (client(hub, e.UserId) is { } cl)
                        await cl.MatchmakingQueueLeft();
                return;
            }

            long id = store.AllocateRoomId(owner, "matchmaking", ranked ? "RankedPlay" : "Matchmaking");
            string password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

            var live = new MultiplayerRoom(id)
            {
                Settings = new MultiplayerRoomSettings
                {
                    Name = ranked ? "Ranked Play" : "Quick Play",
                    MatchType = ranked ? MatchType.RankedPlay : MatchType.Matchmaking,
                    QueueMode = QueueMode.AllPlayers,
                    Password = password,
                    AutoStartDuration = TimeSpan.Zero,
                },
                State = MultiplayerRoomState.Open,
            };

            var match = new Match
            {
                Room = new ServerRoom
                {
                    Id = id,
                    Live = live,
                    Json = new Newtonsoft.Json.Linq.JObject
                    {
                        ["id"] = id, ["category"] = "matchmaking", ["starts_at"] = DateTimeOffset.UtcNow, ["ends_at"] = null, ["channel_id"] = 0,
                    },
                },
                PoolId = poolId,
                Invited = entries.Select(e => e.UserId).ToHashSet(),
            };

            int channel = services.GetRequiredService<Chat>().CreateRoomChannel(id, live.Settings.Name);
            match.Room.ChannelId = channel;
            live.ChannelID = channel;
            match.Room.Json["channel_id"] = channel;

            if (ranked)
            {
                match.Deck.AddRange(maps);
                // the room needs one playlist item from the start; the first card played replaces it as the current item.
                var placeholder = item(match.Deck[^1], owner, 0);
                match.Deck.RemoveAt(match.Deck.Count - 1);
                placeholder.Expired = true;
                live.Playlist.Add(placeholder);
                live.Settings.PlaylistItemId = placeholder.ID;
                live.MatchState = new RankedPlayRoomState
                {
                    Stage = RankedPlayStage.WaitForJoin,
                    StarRating = stars,
                    Users = entries.ToDictionary(e => e.UserId, e => new RankedPlayUserInfo { Rating = (int)Math.Round(e.Rating) }),
                };
            }
            else
            {
                ushort order = 0;
                foreach (var map in maps) live.Playlist.Add(item(map, owner, order++));
                live.Settings.PlaylistItemId = live.Playlist[0].ID;
                var state = new MatchmakingRoomState { Stage = MatchmakingStage.WaitingForClientsJoin };
                foreach (var e in entries) state.Users.GetOrAdd(e.UserId);
                live.MatchState = state;
            }

            store.Rooms[id] = match.Room;
            matches[id] = match;
            log.LogInformation("matchmaking: created {Kind} room {Room} for {Users}", ranked ? "ranked play" : "quick play", id, string.Join(",", match.Invited));

            await setStage(match, ranked ? RankedPlayStage.WaitForJoin : MatchmakingStage.WaitingForClientsJoin, TimeSpan.FromSeconds(30));

            foreach (var e in entries)
            {
                if (client(hub, e.UserId) is { } cl)
                {
                    await cl.MatchmakingQueueStatusChanged(new MatchmakingQueueStatus.JoiningMatch());
                    await cl.MatchmakingRoomReady(id, password);
                }
            }

            await broadcastLobby(poolId);
        }

        public bool IsInvited(long roomId, int userId) => matches.TryGetValue(roomId, out var m) && m.Invited.Contains(userId) && !m.Finished;

        public async Task UserJoined(ServerRoom room, int userId)
        {
            if (!matches.TryGetValue(room.Id, out var m)) return;
            bool everyone;
            lock (room.Lock)
            {
                m.Joined.Add(userId);
                everyone = m.Invited.All(m.Joined.Contains);
            }

            if (m.Ranked)
            {
                // each player sees their own cards; the opponent only sees card backs until they're played.
                var state = (RankedPlayRoomState)m.Live.MatchState!;
                if (state.Users.TryGetValue(userId, out var info) && client(hub, userId) is { } cl)
                    foreach (var card in info.Hand)
                        await cl.RankedPlayCardRevealed(card, m.Cards[card.ID]);
            }

            bool waiting = m.Ranked ? ((RankedPlayRoomState)m.Live.MatchState!).Stage == RankedPlayStage.WaitForJoin
                                    : ((MatchmakingRoomState)m.Live.MatchState!).Stage == MatchmakingStage.WaitingForClientsJoin;
            if (everyone && waiting) await advance(m, m.StageVersion);
        }

        public async Task UserLeft(ServerRoom room, int userId)
        {
            if (!matches.TryGetValue(room.Id, out var m) || m.Finished) return;

            if (m.Ranked)
            {
                var state = (RankedPlayRoomState)m.Live.MatchState!;
                if (state.Stage is RankedPlayStage.WaitForJoin or RankedPlayStage.Ended) return;
                // leaving a ranked match forfeits it.
                int? winner = state.Users.Keys.FirstOrDefault(u => u != userId);
                await endRanked(m, winner == 0 ? null : winner);
                return;
            }

            var qp = (MatchmakingRoomState)m.Live.MatchState!;
            lock (room.Lock) qp.Users.GetOrAdd(userId).AbandonedAt = DateTimeOffset.UtcNow;
            await group(m).MatchRoomStateChanged(qp);

            int remaining;
            lock (room.Lock) remaining = m.Live.Users.Count;
            if (remaining <= 1 && qp.Stage != MatchmakingStage.Ended) await endQuickPlay(m);
        }

        public void RoomClosed(ServerRoom room)
        {
            if (matches.TryRemove(room.Id, out var m)) m.Finished = true;
        }

        public async Task SkipStage(ServerRoom room)
        {
            if (matches.TryGetValue(room.Id, out var m)) await advance(m, m.StageVersion);
        }

        // ------------------------------------------------------------------ stage machine

        private async Task setStage(Match m, Enum stage, TimeSpan? duration)
        {
            MultiplayerCountdown? countdown = null;
            List<MultiplayerCountdown> stopped;
            int version;

            lock (m.Room.Lock)
            {
                version = ++m.StageVersion;
                m.StageStartedAt = DateTimeOffset.UtcNow;
                var live = m.Live;
                stopped = live.ActiveCountdowns.Where(c => c is MatchmakingStageCountdown or RankedPlayStageCountdown).ToList();
                foreach (var c in stopped) live.ActiveCountdowns.Remove(c);

                switch (live.MatchState)
                {
                    case MatchmakingRoomState qp:
                        qp.Stage = (MatchmakingStage)stage;
                        if (duration != null) countdown = new MatchmakingStageCountdown { ID = Interlocked.Increment(ref countdown_ids), TimeRemaining = duration.Value, Stage = qp.Stage };
                        break;

                    case RankedPlayRoomState rp:
                        rp.Stage = (RankedPlayStage)stage;
                        if (duration != null) countdown = new RankedPlayStageCountdown { ID = Interlocked.Increment(ref countdown_ids), TimeRemaining = duration.Value, Stage = rp.Stage };
                        break;
                }

                if (countdown != null) live.ActiveCountdowns.Add(countdown);
            }

            // a new round starts from a clean slate: users still on the previous results go back to idle so they can ready up again.
            if (stage is MatchmakingStage.RoundWarmupTime or MatchmakingStage.UserBeatmapSelect or MatchmakingStage.ServerBeatmapFinalised or MatchmakingStage.WaitingForClientsBeatmapDownload
                or RankedPlayStage.RoundWarmup or RankedPlayStage.CardDiscard or RankedPlayStage.FinishCardDiscard or RankedPlayStage.CardPlay or RankedPlayStage.FinishCardPlay or RankedPlayStage.GameplayWarmup)
            {
                List<MultiplayerRoomUser> stale;
                lock (m.Room.Lock) stale = m.Live.Users.Where(u => u.State == MultiplayerUserState.Results).ToList();
                if (stale.Count > 0)
                {
                    var e = engine();
                    foreach (var u in stale) await e.setState(m.Room, u, MultiplayerUserState.Idle);
                }
            }

            var g = group(m);
            foreach (var c in stopped) await g.MatchEvent(new CountdownStoppedEvent(c.ID));
            await g.MatchRoomStateChanged(m.Live.MatchState!);
            if (countdown != null) await g.MatchEvent(new CountdownStartedEvent(countdown));

            if (duration != null)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(duration.Value);
                    await advance(m, version);
                });
            }
        }

        /// <summary>Moves a match to its next stage, if it's still in the stage identified by <paramref name="version"/>.</summary>
        private async Task advance(Match m, int version)
        {
            if (m.Finished) return;
            await m.Gate.WaitAsync();
            try
            {
                if (m.Finished || m.StageVersion != version) return;
                if (m.Ranked) await advanceRanked(m);
                else await advanceQuickPlay(m);
            }
            catch (Exception e)
            {
                log.LogError(e, "matchmaking room {Room}: stage transition failed", m.Room.Id);
            }
            finally
            {
                m.Gate.Release();
            }
        }

        private bool everyoneHasBeatmap(Match m)
        {
            lock (m.Room.Lock)
                return m.Live.Users.All(u => u.State == MultiplayerUserState.Ready || u.BeatmapAvailability.State == DownloadState.LocallyAvailable);
        }

        private async Task waitForDownloads(Match m, int version, TimeSpan max)
        {
            // poll so the stage ends as soon as everyone has the beatmap (or the timeout passes).
            var until = DateTimeOffset.UtcNow + max;
            while (DateTimeOffset.UtcNow < until && !m.Finished && m.StageVersion == version && !everyoneHasBeatmap(m))
                await Task.Delay(1000);
            await advance(m, version);
        }

        private async Task setCurrentItem(Match m, long itemId)
        {
            lock (m.Room.Lock) m.Live.Settings.PlaylistItemId = itemId;
            await group(m).SettingsChanged(m.Live.Settings);
        }

        private async Task startGameplay(Match m)
        {
            var e = engine();
            await e.startMatch(m.Room, force: true);

            // nobody was ready (no one has the beatmap): treat as a played round with no scores.
            if (m.Live.State == MultiplayerRoomState.Open)
                _ = GameplayFinished(m.Room); // not awaited: it re-enters the stage gate we're holding.
        }

        /// <summary>Called by the hub once every player of the current item has finished (or aborted).</summary>
        public async Task GameplayFinished(ServerRoom room)
        {
            if (!matches.TryGetValue(room.Id, out var m)) return;
            bool inGameplay = m.Ranked ? ((RankedPlayRoomState)m.Live.MatchState!).Stage == RankedPlayStage.Gameplay
                                       : ((MatchmakingRoomState)m.Live.MatchState!).Stage == MatchmakingStage.Gameplay;
            if (!inGameplay) return;

            int version = m.StageVersion;
            // give the clients a moment to finish submitting their scores.
            await Task.Delay(2500);
            await advance(m, version);
        }

        private List<SoloScoreInfo> roundScores(long itemId, IEnumerable<int> players)
        {
            var ids = players.ToHashSet();
            using var c = db.Open();
            var rows = c.Query("SELECT id, user_id FROM scores WHERE playlist_item_id = @p ORDER BY total_score DESC, id ASC", r => (id: r.GetInt64(0), user: r.GetInt32(1)), ("@p", itemId));
            var seen = new HashSet<int>();
            return Scores.Load(c, rows.Where(r => ids.Contains(r.user) && seen.Add(r.user)).Select(r => r.id), accounts, users, withBeatmap: false);
        }

        // ------------------------------------------------------------------ quick play

        private async Task advanceQuickPlay(Match m)
        {
            var state = (MatchmakingRoomState)m.Live.MatchState!;
            switch (state.Stage)
            {
                case MatchmakingStage.WaitingForClientsJoin:
                    if (m.Joined.Count < 2)
                    {
                        await endQuickPlay(m);
                        return;
                    }

                    lock (m.Room.Lock) state.AdvanceRound();
                    await setStage(m, MatchmakingStage.RoundWarmupTime, TimeSpan.FromSeconds(state.CurrentRound == 1 ? 12 : 8));
                    break;

                case MatchmakingStage.RoundWarmupTime:
                    lock (m.Room.Lock)
                    {
                        m.Selections.Clear();
                        state.CandidateItems = Array.Empty<long>();
                        state.CandidateItem = 0;
                        state.GameplayItem = 0;
                    }

                    await setStage(m, MatchmakingStage.UserBeatmapSelect, TimeSpan.FromSeconds(30));
                    break;

                case MatchmakingStage.UserBeatmapSelect:
                {
                    long[] pending;
                    long[] picked;
                    lock (m.Room.Lock)
                    {
                        pending = m.Live.Playlist.Where(p => !p.Expired).Select(p => p.ID).ToArray();
                        picked = m.Selections.Values.SelectMany(s => s).Where(id => id == -1 || pending.Contains(id)).Distinct().ToArray();
                    }

                    if (pending.Length == 0)
                    {
                        await endQuickPlay(m);
                        return;
                    }

                    long[] candidates = picked.Length > 0 ? picked : new[] { -1L };
                    long candidate = candidates[Random.Shared.Next(candidates.Length)];
                    long gameplay = candidate == -1 ? pending[Random.Shared.Next(pending.Length)] : candidate;

                    lock (m.Room.Lock)
                    {
                        state.CandidateItems = candidates;
                        state.CandidateItem = candidate;
                        state.GameplayItem = gameplay;
                    }

                    await setCurrentItem(m, gameplay);
                    await setStage(m, MatchmakingStage.ServerBeatmapFinalised, TimeSpan.FromSeconds(candidates.Length > 1 ? 8 : 5));
                    break;
                }

                case MatchmakingStage.ServerBeatmapFinalised:
                    await setStage(m, MatchmakingStage.WaitingForClientsBeatmapDownload, null);
                    _ = waitForDownloads(m, m.StageVersion, TimeSpan.FromSeconds(60));
                    break;

                case MatchmakingStage.WaitingForClientsBeatmapDownload:
                    await setStage(m, MatchmakingStage.GameplayWarmupTime, TimeSpan.FromSeconds(5));
                    break;

                case MatchmakingStage.GameplayWarmupTime:
                    await setStage(m, MatchmakingStage.Gameplay, null);
                    await startGameplay(m);
                    break;

                case MatchmakingStage.Gameplay:
                {
                    var players = m.Live.Users.Select(u => u.UserID).ToList();
                    var scores = roundScores(state.GameplayItem, players).ToArray();
                    lock (m.Room.Lock)
                    {
                        if (scores.Length > 0) state.RecordScores(scores, placement_points);
                        if (m.Live.Playlist.FirstOrDefault(p => p.ID == state.GameplayItem) is { Expired: false } played)
                        {
                            played.Expired = true;
                            played.PlayedAt = DateTimeOffset.UtcNow;
                        }
                    }

                    if (m.Live.Playlist.FirstOrDefault(p => p.ID == state.GameplayItem) is { } p) await group(m).PlaylistItemChanged(p);
                    await setStage(m, MatchmakingStage.ResultsDisplaying, TimeSpan.FromSeconds(12));
                    break;
                }

                case MatchmakingStage.ResultsDisplaying:
                    bool more;
                    lock (m.Room.Lock) more = state.CurrentRound < quick_play_rounds && m.Live.Playlist.Any(p => !p.Expired) && m.Live.Users.Count >= 2;
                    if (!more)
                    {
                        await endQuickPlay(m);
                        return;
                    }

                    lock (m.Room.Lock) state.AdvanceRound();
                    await setStage(m, MatchmakingStage.RoundWarmupTime, TimeSpan.FromSeconds(8));
                    break;
            }
        }

        private async Task endQuickPlay(Match m)
        {
            if (m.Finished) return;
            m.Finished = true;
            var state = (MatchmakingRoomState)m.Live.MatchState!;

            List<MatchmakingUser> ordered;
            lock (m.Room.Lock) ordered = state.Users.Order(new MatchmakingUserComparer(state.CurrentRound)).ToList();
            var players = ordered.Where(u => u.Rounds.Count > 0 || u.AbandonedAt == null).ToList();
            if (state.CurrentRound > 0 && players.Count >= 2)
                applyRatings(m.PoolId, players.Select(u => u.UserId).ToList(), players.ToDictionary(u => u.UserId, u => u.Points));

            await setStage(m, MatchmakingStage.Ended, null);
            log.LogInformation("quick play room {Room} ended", m.Room.Id);
        }

        public async Task ToggleSelection(ServerRoom room, int userId, long playlistItemId)
        {
            if (!matches.TryGetValue(room.Id, out var m) || m.Ranked) return;
            var state = (MatchmakingRoomState)m.Live.MatchState!;
            if (state.Stage != MatchmakingStage.UserBeatmapSelect) return;

            bool selected;
            lock (room.Lock)
            {
                if (playlistItemId != -1 && m.Live.Playlist.All(p => p.ID != playlistItemId || p.Expired)) return;
                if (!m.Selections.TryGetValue(userId, out var set)) m.Selections[userId] = set = new HashSet<long>();
                selected = set.Add(playlistItemId);
                if (!selected) set.Remove(playlistItemId);
            }

            if (selected) await group(m).MatchmakingItemSelected(userId, playlistItemId);
            else await group(m).MatchmakingItemDeselected(userId, playlistItemId);
        }

        // ------------------------------------------------------------------ ranked play

        private async Task deal(Match m, int userId, int count)
        {
            var state = (RankedPlayRoomState)m.Live.MatchState!;
            var dealt = new List<(RankedPlayCardItem card, MultiplayerPlaylistItem item)>();
            lock (m.Room.Lock)
            {
                for (int i = 0; i < count && m.Deck.Count > 0; i++)
                {
                    var map = m.Deck[^1];
                    m.Deck.RemoveAt(m.Deck.Count - 1);
                    var card = new RankedPlayCardItem();
                    var pi = item(map, userId, (ushort)(m.Live.Playlist.Count + i));
                    m.Cards[card.ID] = pi;
                    m.CardOwners[card.ID] = userId;
                    state.Users[userId].Hand.Add(card);
                    dealt.Add((card, pi));
                }
            }

            if (m.Deck.Count < 4)
            {
                // refill the deck so long matches never run dry.
                var used = m.Cards.Values.Select(c => c.BeatmapID).ToHashSet();
                m.Deck.AddRange(await pool.Pick(20, state.StarRating, used));
            }

            var owner = client(hub, userId);
            foreach (var (card, pi) in dealt)
            {
                await group(m).RankedPlayCardAdded(userId, card);
                if (owner != null) await owner.RankedPlayCardRevealed(card, pi);
            }
        }

        private async Task advanceRanked(Match m)
        {
            var state = (RankedPlayRoomState)m.Live.MatchState!;
            switch (state.Stage)
            {
                case RankedPlayStage.WaitForJoin:
                    if (m.Joined.Count < 2)
                    {
                        await endRanked(m, m.Joined.Count == 1 ? m.Joined.First() : null, rated: false);
                        return;
                    }

                    goto case RankedPlayStage.Results;

                case RankedPlayStage.RoundWarmup:
                    lock (m.Room.Lock) m.Discarded.Clear();
                    await setStage(m, RankedPlayStage.CardDiscard, TimeSpan.FromSeconds(state.CurrentRound == 1 ? 30 : 20));
                    break;

                case RankedPlayStage.CardDiscard:
                    await setStage(m, RankedPlayStage.FinishCardDiscard, TimeSpan.FromSeconds(3));
                    break;

                case RankedPlayStage.FinishCardDiscard:
                {
                    var order = state.Users.Keys.OrderBy(u => u).ToArray();
                    lock (m.Room.Lock)
                    {
                        state.ActiveUserId = order[(state.CurrentRound - 1) % order.Length];
                        m.PlayedCard = null;
                    }

                    await setStage(m, RankedPlayStage.CardPlay, TimeSpan.FromSeconds(30));
                    break;
                }

                case RankedPlayStage.CardPlay:
                {
                    // the active player ran out of time: play a random card for them.
                    RankedPlayCardItem? card;
                    lock (m.Room.Lock) card = m.PlayedCard ?? state.ActiveUser?.Hand.OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
                    if (card == null)
                    {
                        await endRanked(m, null);
                        return;
                    }

                    await revealPlayedCard(m, card);
                    break;
                }

                case RankedPlayStage.FinishCardPlay:
                    await setStage(m, RankedPlayStage.GameplayWarmup, null);
                    _ = waitForDownloads(m, m.StageVersion, TimeSpan.FromSeconds(60));
                    break;

                case RankedPlayStage.GameplayWarmup:
                    await setStage(m, RankedPlayStage.Gameplay, null);
                    await startGameplay(m);
                    break;

                case RankedPlayStage.Gameplay:
                    await rankedResults(m);
                    break;

                case RankedPlayStage.Results:
                {
                    if (state.Users.Values.Any(u => u.Life <= 0))
                    {
                        var winner = state.Users.OrderByDescending(u => u.Value.Life).First().Key;
                        await endRanked(m, winner);
                        return;
                    }

                    lock (m.Room.Lock)
                    {
                        state.CurrentRound++;
                        foreach (var u in state.Users.Values) u.DamageInfo = null;
                    }

                    foreach (var (userId, info) in state.Users.ToArray())
                        await deal(m, userId, ranked_hand_size - info.Hand.Count);

                    await setStage(m, RankedPlayStage.RoundWarmup, TimeSpan.FromSeconds(state.CurrentRound == 1 ? 10 : 5));
                    break;
                }
            }
        }

        private async Task revealPlayedCard(Match m, RankedPlayCardItem card)
        {
            var state = (RankedPlayRoomState)m.Live.MatchState!;
            var pi = m.Cards[card.ID];
            int owner = m.CardOwners[card.ID];
            lock (m.Room.Lock)
            {
                m.PlayedCard = card;
                state.Users[owner].Hand.RemoveAll(c => c.ID == card.ID);
                pi.PlaylistOrder = (ushort)m.Live.Playlist.Count;
                m.Live.Playlist.Add(pi);
            }

            var g = group(m);
            await g.RankedPlayCardRevealed(card, pi);
            await g.RankedPlayCardPlayed(card);
            await g.RankedPlayCardRemoved(owner, card);
            await g.PlaylistItemAdded(pi);
            await setCurrentItem(m, pi.ID);
            await setStage(m, RankedPlayStage.FinishCardPlay, TimeSpan.FromSeconds(4));
        }

        public async Task PlayCard(ServerRoom room, int userId, RankedPlayCardItem card)
        {
            if (!matches.TryGetValue(room.Id, out var m) || !m.Ranked) return;
            await m.Gate.WaitAsync();
            try
            {
                var state = (RankedPlayRoomState)m.Live.MatchState!;
                if (state.Stage != RankedPlayStage.CardPlay || state.ActiveUserId != userId || m.PlayedCard != null) throw new HubException("It's not your turn to play a card.");
                if (state.Users[userId].Hand.All(c => c.ID != card.ID)) throw new HubException("That card isn't in your hand.");
                await revealPlayedCard(m, state.Users[userId].Hand.First(c => c.ID == card.ID));
            }
            finally
            {
                m.Gate.Release();
            }
        }

        public async Task DiscardCards(ServerRoom room, int userId, RankedPlayCardItem[] cards)
        {
            if (!matches.TryGetValue(room.Id, out var m) || !m.Ranked) return;
            var state = (RankedPlayRoomState)m.Live.MatchState!;
            if (state.Stage != RankedPlayStage.CardDiscard) throw new HubException("Cards can only be replaced during the discard phase.");

            List<RankedPlayCardItem> removed;
            bool bothDone;
            lock (m.Room.Lock)
            {
                if (!m.Discarded.Add(userId)) throw new HubException("You already replaced your cards this round.");
                var ids = cards.Select(c => c.ID).ToHashSet();
                removed = state.Users[userId].Hand.Where(c => ids.Contains(c.ID)).ToList();
                state.Users[userId].Hand.RemoveAll(c => ids.Contains(c.ID));
                bothDone = state.Users.Keys.All(m.Discarded.Contains);
            }

            foreach (var card in removed) await group(m).RankedPlayCardRemoved(userId, card);
            await deal(m, userId, removed.Count);

            if (bothDone)
            {
                // a short pause so the replacement animation can finish (the client expects ~3 seconds).
                int version = m.StageVersion;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    await advance(m, version);
                });
            }
        }

        private async Task rankedResults(Match m)
        {
            var state = (RankedPlayRoomState)m.Live.MatchState!;
            long itemId = m.Live.Settings.PlaylistItemId;
            var players = state.Users.Keys.ToList();
            var scores = roundScores(itemId, players).ToDictionary(s => s.UserID, s => s.TotalScore);

            lock (m.Room.Lock)
            {
                long a = scores.GetValueOrDefault(players[0]);
                long b = scores.GetValueOrDefault(players[1]);
                if (m.Live.Playlist.FirstOrDefault(p => p.ID == itemId) is { } played)
                {
                    played.Expired = true;
                    played.PlayedAt = DateTimeOffset.UtcNow;
                }

                foreach (var u in state.Users.Values)
                    u.DamageInfo = new RankedPlayDamageInfo { OldLife = u.Life, NewLife = u.Life, Multiplier = u.DamageMultiplier };

                if (a != b)
                {
                    int winner = a > b ? players[0] : players[1];
                    int loser = a > b ? players[1] : players[0];
                    var info = state.Users[loser];
                    int direct = (int)Math.Min(1_000_000, Math.Abs(a - b));
                    int bonus = 50_000;
                    int damage = (int)Math.Round(direct * info.DamageMultiplier) + bonus;
                    info.DamageInfo = new RankedPlayDamageInfo
                    {
                        DirectDamage = direct, Multiplier = info.DamageMultiplier, BonusDamage = bonus, RawDamage = direct + bonus, Damage = damage,
                        OldLife = info.Life, NewLife = Math.Max(0, info.Life - damage),
                    };
                    info.Life = Math.Max(0, info.Life - damage);
                    state.Users[winner].RoundsWon++;
                }

                foreach (var u in state.Users.Values) u.DamageMultiplier += 0.25;
                state.DamageMultiplier += 0.25;
            }

            if (m.Live.Playlist.FirstOrDefault(p => p.ID == itemId) is { } item2) await group(m).PlaylistItemChanged(item2);
            await setStage(m, RankedPlayStage.Results, TimeSpan.FromSeconds(12));
        }

        private async Task endRanked(Match m, int? winner, bool rated = true)
        {
            if (m.Finished) return;
            m.Finished = true;
            var state = (RankedPlayRoomState)m.Live.MatchState!;

            if (rated && winner != null && state.Users.Count == 2)
            {
                int loser = state.Users.Keys.First(u => u != winner);
                var after = applyRatings(m.PoolId, new[] { winner.Value, loser }, new Dictionary<int, int> { [winner.Value] = 1 });
                lock (m.Room.Lock)
                    foreach (var (u, r) in after)
                        state.Users[u].RatingAfter = r;
            }

            lock (m.Room.Lock)
            {
                state.WinningUserId = winner;
                state.ActiveUserId = null;
            }

            await setStage(m, RankedPlayStage.Ended, null);
            log.LogInformation("ranked play room {Room} ended, winner {Winner}", m.Room.Id, winner);
        }
    }
}
