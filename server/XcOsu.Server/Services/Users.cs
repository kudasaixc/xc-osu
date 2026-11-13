// xc-osu! server — user profiles and statistics. xc softworks (kudasaixc)

using Newtonsoft.Json.Linq;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Scoring;
using osu.Game.Users;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed class Users
    {
        private readonly Db db;
        private readonly Accounts accounts;
        private readonly Presence presence;
        private readonly string publicBase;

        public Users(Db db, Accounts accounts, Presence presence, IConfiguration config)
        {
            this.db = db;
            this.accounts = accounts;
            this.presence = presence;
            publicBase = (config["PublicUrl"] ?? "https://osu.xcsoftworks.com").TrimEnd('/');
        }

        public static readonly int[] PP_STATUSES = { 1, 2, 3, 4 };

        public static readonly string[] RULESET_NAMES = { "osu", "taiko", "fruits", "mania" };

        public static int RulesetId(string? name) => name switch { "taiko" => 1, "fruits" => 2, "mania" => 3, _ => 0 };

        public string PublicBase => publicBase;


        public string AvatarUrl(AccountRow row) => $"{publicBase}/api/xc/avatar/{row.Id}?v={row.AvatarVersion}";

        public string? CoverUrl(AccountRow row) => row.CoverVersion > 0 ? $"{publicBase}/api/xc/cover/{row.Id}?v={row.CoverVersion}" : $"{publicBase}/api/xc/cover/default";

        // every compact user carries its team, so a fifty-row ranking page used to run fifty extra queries. Team
        // membership changes only from the team endpoints, which drop this cache.
        private readonly Dictionary<int, (DateTime at, APITeam? team)> teamCache = new Dictionary<int, (DateTime, APITeam?)>();
        private readonly object teamLock = new object();

        public APITeam? Team(int userId)
        {
            lock (teamLock)
                if (teamCache.TryGetValue(userId, out var cached) && cached.at > DateTime.UtcNow.AddSeconds(-30))
                    return cached.team;

            APITeam? team;
            using (var c = db.Open())
                team = c.Query("SELECT t.id, t.name, t.short_name, t.flag_url FROM teams t JOIN team_members m ON m.team_id = t.id WHERE m.user_id = @u",
                    r => new APITeam { Id = r.GetInt32(0), Name = r.GetString(1), ShortName = r.GetString(2), FlagUrl = r.IsDBNull(3) ? null : r.GetString(3) }, ("@u", userId)).FirstOrDefault();

            lock (teamLock) teamCache[userId] = (DateTime.UtcNow, team);
            return team;
        }

        public void InvalidateTeams()
        {
            lock (teamLock) teamCache.Clear();
        }

        public APIUser Compact(AccountRow row)
        {
            var profile = JObject.Parse(string.IsNullOrEmpty(row.ProfileJson) ? "{}" : row.ProfileJson);
            return new APIUser
            {
                Id = row.Id,
                Username = row.Username,
                CountryCode = Enum.TryParse(row.Country, out CountryCode cc) ? cc : CountryCode.Unknown,
                IsAdmin = row.IsAdmin,
                IsSupporter = true,
                SupportLevel = 3,
                Active = !row.Restricted,
                WasRecentlyOnline = presence.IsOnline(row.Id),
                Title = row.Title,
                Colour = row.IsAdmin ? "#ff66aa" : profile.Value<string>("profile_colour"),
                JoinDate = row.JoinDate,
                PlayMode = row.Playmode,
                AvatarUrl = AvatarUrl(row),
                CoverUrl = CoverUrl(row),
                Team = Team(row.Id),
                PMFriendsOnly = profile.Value<bool?>("pm_friends_only") ?? false,
                LastVisit = presence.IsOnline(row.Id) ? DateTimeOffset.UtcNow : row.LastVisit,
            };
        }

        public APIUser? Full(int id, int rulesetId)
        {
            var row = accounts.Get(id);
            return row == null ? null : Full(row, rulesetId);
        }

        public APIUser Full(AccountRow row, int rulesetId)
        {
            var user = Compact(row);
            var profile = JObject.Parse(string.IsNullOrEmpty(row.ProfileJson) ? "{}" : row.ProfileJson);
            var stats = Statistics(row.Id, rulesetId);
            user.Statistics = stats;

            var all = new Dictionary<string, UserStatistics>();
            for (int r = 0; r < RULESET_NAMES.Length; r++)
                all[RULESET_NAMES[r]] = r == rulesetId ? stats : Statistics(row.Id, r);
            user.RulesetsStatistics = all;

            user.Cover = new APIUser.UserCover { Url = CoverUrl(row), CustomUrl = row.CoverVersion > 0 ? CoverUrl(row) : null };
            user.Kudosu = new APIUser.KudosuCount();
            // "badges" and "title" in profile_json are staff-assigned: PUT /api/xc/account never writes them.
            var badges = new List<Badge>();
            if (row.IsAdmin)
                badges.Add(new Badge { Description = "xc softworks team", ImageUrl = $"{publicBase}/api/xc/badge/xc.png", AwardedAt = row.JoinDate });
            foreach (var b in profile["badges"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (b.Value<string>("image") is not { Length: > 0 } image) continue;

                badges.Add(new Badge
                {
                    Description = b.Value<string>("description") ?? string.Empty,
                    ImageUrl = $"{publicBase}/api/xc/badge/{Uri.EscapeDataString(image)}",
                    Url = b.Value<string>("url") ?? string.Empty,
                    AwardedAt = DateTimeOffset.TryParse(b.Value<string>("awarded_at"), out var at) ? at : row.JoinDate,
                });
            }
            user.Badges = badges.ToArray();
            user.Title = profile.Value<string>("title");
            user.Groups = row.IsAdmin
                ? new[] { new APIUserGroup { Id = 1, Identifier = "xc", Name = "xc softworks", ShortName = "XC", Colour = "#ff66aa", IsProbationary = false } }
                : Array.Empty<APIUserGroup>();
            user.ProfileOrder = profile["profile_order"]?.ToObject<string[]>() ?? new[] { "me", "recent_activity", "top_ranks", "medals", "historical", "beatmaps", "kudosu" };
            user.PreviousUsernames = JArray.Parse(row.PreviousUsernames).ToObject<string[]>() ?? Array.Empty<string>();
            user.Location = profile.Value<string>("location");
            user.Interests = profile.Value<string>("interests");
            user.Occupation = profile.Value<string>("occupation");
            user.Twitter = profile.Value<string>("twitter");
            user.Discord = profile.Value<string>("discord");
            user.Website = profile.Value<string>("website");
            user.PlayStyles = (profile["playstyle"]?.ToObject<string[]>() ?? Array.Empty<string>())
                              .Select(p => Enum.TryParse<APIPlayStyle>(p, true, out var ps) ? (APIPlayStyle?)ps : null).OfType<APIPlayStyle>().ToArray();
            user.ProfileHue = profile.Value<int?>("profile_hue");
            if (profile.Value<string>("page") is { Length: > 0 } page)
                user.Page = new APIUserPage { Raw = page, Html = "<div class='bbcode bbcode--profile-page'>" + System.Net.WebUtility.HtmlEncode(page).Replace("\n", "<br />") + "</div>" };

            if (row.RankHighest is int best)
                user.RankHighest = new APIUser.UserRankHighest { Rank = best, UpdatedAt = row.RankHighestAt ?? DateTimeOffset.UtcNow };

            using var c = db.Open();
            string mode = RULESET_NAMES[Math.Clamp(rulesetId, 0, 3)];

            stats.RankHistory = new APIRankHistory { Mode = mode, Data = RankHistory(c, row.Id, rulesetId, stats.GlobalRank) };
            user.MonthlyPlayCounts = c.Query("""
                SELECT substr(ended_at, 1, 7), COUNT(*) FROM scores WHERE user_id = @u GROUP BY substr(ended_at, 1, 7) ORDER BY 1
                """, r => new APIUserHistoryCount { Date = DateTime.Parse(r.GetString(0) + "-01"), Count = r.GetInt64(1) }, ("@u", row.Id)).ToArray();
            user.ReplaysWatchedCounts = c.Query("SELECT month, count FROM replay_watches WHERE user_id = @u ORDER BY month",
                r => new APIUserHistoryCount { Date = DateTime.Parse(r.GetString(0) + "-01"), Count = r.GetInt64(1) }, ("@u", row.Id)).ToArray();
            user.Achievements = c.Query("SELECT achievement_id, achieved_at FROM user_achievements WHERE user_id = @u ORDER BY achieved_at DESC",
                r => new APIUserAchievement { ID = r.GetInt32(0), AchievedAt = DateTimeOffset.Parse(r.GetString(1)) }, ("@u", row.Id)).ToArray();

            user.ScoresBestCount = (int)c.Scalar<long>("SELECT COUNT(DISTINCT beatmap_id) FROM scores WHERE user_id = @u AND ruleset_id = @r AND passed = 1 AND pp IS NOT NULL AND ranked = 1",
                ("@u", row.Id), ("@r", rulesetId));
            user.ScoresRecentCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM scores WHERE user_id = @u AND ruleset_id = @r AND ended_at > @t",
                ("@u", row.Id), ("@r", rulesetId), ("@t", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")));
            user.ScoresFirstCount = FirstPlaceBeatmaps(c, row.Id, rulesetId).Count;
            user.ScoresPinnedCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM pinned_scores WHERE user_id = @u", ("@u", row.Id));
            user.BeatmapPlayCountsCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM beatmap_playcounts WHERE user_id = @u", ("@u", row.Id));
            user.FavouriteBeatmapsetCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM favourites WHERE user_id = @u", ("@u", row.Id));
            user.FollowerCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM friends WHERE target_id = @u", ("@u", row.Id));
            user.CommentsCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM comments WHERE user_id = @u AND deleted_at IS NULL", ("@u", row.Id));

            if (stats.GlobalRank != null)
                user.Rank = new APIUser.GlobalRank { Rank = stats.GlobalRank, RulesetId = rulesetId };

            user.DailyChallengeStatistics = DailyChallengeStatistics(c, row.Id);
            user.MatchmakingStatistics = c.Query("SELECT pool_id, rating, sigma, plays, total_points, first_placements FROM matchmaking_ratings WHERE user_id = @u",
                r => new APIUserMatchmakingStatistics
                {
                    UserId = row.Id,
                    PoolId = r.GetInt32(0),
                    Rating = (int)Math.Round(r.GetDouble(1)),
                    IsRatingProvisional = r.GetDouble(2) > 150,
                    Plays = r.GetInt32(3),
                    TotalPoints = r.GetInt32(4),
                    FirstPlacements = r.GetInt32(5),
                }, ("@u", row.Id)).ToArray();
            foreach (var m in user.MatchmakingStatistics)
            {
                var pool = Matchmaking.POOLS.FirstOrDefault(p => p.Id == m.PoolId);
                if (pool != null) m.Pool = new APIMatchmakingPool { Id = pool.Id, Name = pool.Name, Active = true, RulesetId = pool.RulesetId, VariantId = pool.Variant };
                m.Rank = (int)c.Scalar<long>("SELECT COUNT(*) FROM matchmaking_ratings WHERE pool_id = @p AND rating > @r", ("@p", m.PoolId), ("@r", m.Rating)) + 1;
            }

            return user;
        }

        public APIUserDailyChallengeStatistics DailyChallengeStatistics(Npgsql.NpgsqlConnection c, int userId) =>
            c.Query("""
                SELECT playcount, daily_streak_current, daily_streak_best, weekly_streak_current, weekly_streak_best, top_10p, top_50p, last_day, last_week
                FROM daily_challenge_stats WHERE user_id = @u
                """, r => new APIUserDailyChallengeStatistics
            {
                UserID = userId,
                PlayCount = r.GetInt32(0),
                DailyStreakCurrent = r.GetInt32(1),
                DailyStreakBest = r.GetInt32(2),
                WeeklyStreakCurrent = r.GetInt32(3),
                WeeklyStreakBest = r.GetInt32(4),
                Top10PercentPlacements = r.GetInt32(5),
                Top50PercentPlacements = r.GetInt32(6),
                LastUpdate = r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)),
                LastWeeklyStreak = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)),
            }, ("@u", userId)).FirstOrDefault() ?? new APIUserDailyChallengeStatistics { UserID = userId };

        /// <summary>90 daily rank snapshots, oldest first (0 = unranked that day), like osu-web.</summary>
        public static int[] RankHistory(Npgsql.NpgsqlConnection c, int userId, int rulesetId, int? currentRank)
        {
            var rows = c.Query("SELECT day, rank FROM rank_history WHERE user_id = @u AND ruleset_id = @r AND day >= @d",
                r => (day: r.GetString(0), rank: r.GetInt32(1)), ("@u", userId), ("@r", rulesetId), ("@d", DateTime.UtcNow.AddDays(-89).ToString("yyyy-MM-dd")))
                .ToDictionary(r => r.day, r => r.rank);

            var data = new int[90];
            int last = 0;
            for (int i = 0; i < 90; i++)
            {
                string day = DateTime.UtcNow.AddDays(i - 89).ToString("yyyy-MM-dd");
                if (rows.TryGetValue(day, out int rank)) last = rank;
                data[i] = last;
            }

            if (currentRank != null) data[89] = currentRank.Value;
            return data;
        }

        public sealed record BestScore(long ScoreId, int BeatmapId, double Pp, double Accuracy);

        /// <summary>The best pp-awarding score per beatmap, highest first.</summary>
        public List<BestScore> Bests(Npgsql.NpgsqlConnection c, int userId, int rulesetId) => c.Query("""
            SELECT id, beatmap_id, pp, accuracy FROM (
                SELECT DISTINCT ON (beatmap_id) id, beatmap_id, pp, accuracy FROM scores
                WHERE user_id = @u AND ruleset_id = @r AND passed = 1 AND pp IS NOT NULL AND ranked = 1
                ORDER BY beatmap_id, pp DESC, id) b
            ORDER BY pp DESC, id
            """, r => new BestScore(r.GetInt64(0), r.GetInt32(1), r.GetDouble(2), r.GetDouble(3)), ("@u", userId), ("@r", rulesetId));

        /// <summary>Beatmaps on which this user holds the #1 score (ranked/loved leaderboards).</summary>
        public static List<long> FirstPlaceBeatmaps(Npgsql.NpgsqlConnection c, int userId, int rulesetId) => c.Query("""
            SELECT s.id FROM scores s
            WHERE s.user_id = @u AND s.ruleset_id = @r AND s.passed = 1 AND s.ranked = 1 AND s.playlist_item_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM scores o WHERE o.beatmap_id = s.beatmap_id AND o.ruleset_id = s.ruleset_id AND o.passed = 1 AND o.ranked = 1
                              AND o.playlist_item_id IS NULL AND (o.total_score > s.total_score OR (o.total_score = s.total_score AND o.id < s.id)))
            ORDER BY s.id DESC
            """, r => r.GetInt64(0), ("@u", userId), ("@r", rulesetId));

        public static double WeightedPp(IReadOnlyList<BestScore> bests) =>
            bests.Select((b, i) => b.Pp * Math.Pow(0.95, i)).Sum() + 416.6667 * (1 - Math.Pow(0.9994, bests.Count));

        // Profile statistics run one query per best score, per aggregate and per rank: a ranking page asks for fifty of
        // them at once and hundreds of players read it together. The values only change when a score is submitted, so
        // keep them for a few seconds; a caller receives its own copy (the endpoints attach the account row to it).
        private readonly Dictionary<(int userId, int rulesetId), (DateTime at, StatsSnapshot stats)> statisticsCache = new();
        private readonly object statisticsLock = new object();
        private static readonly TimeSpan statisticsTtl = TimeSpan.FromSeconds(15);

        private sealed record StatsSnapshot(
            decimal PP, double Accuracy, int PlayCount, long TotalScore, long RankedScore, int MaxCombo, int TotalHits,
            int PlayTime, int ReplaysWatched, int? GlobalRank, float? GlobalRankPercent, int? CountryRank,
            int SSPlus, int SS, int SPlus, int S, int A, int Level, int LevelProgress)
        {
            public UserStatistics ToStatistics() => new()
            {
                IsRanked = PP > 0,
                PP = PP,
                Accuracy = Accuracy,
                PlayCount = PlayCount,
                TotalScore = TotalScore,
                RankedScore = RankedScore,
                MaxCombo = MaxCombo,
                TotalHits = TotalHits,
                PlayTime = PlayTime,
                ReplaysWatched = ReplaysWatched,
                GlobalRank = GlobalRank,
                GlobalRankPercent = GlobalRankPercent,
                CountryRank = CountryRank,
                GradesCount = new UserStatistics.Grades { SSPlus = SSPlus, SS = SS, SPlus = SPlus, S = S, A = A },
                Level = new UserStatistics.LevelInfo { Current = Level, Progress = LevelProgress },
                Variants = new List<UserStatistics.Variant>(),
            };
        }

        public UserStatistics Statistics(int userId, int rulesetId)
        {
            var key = (userId, rulesetId);
            lock (statisticsLock)
            {
                if (statisticsCache.TryGetValue(key, out var cached) && cached.at > DateTime.UtcNow - statisticsTtl)
                    return cached.stats.ToStatistics();
            }

            var stats = computeStatistics(userId, rulesetId);
            lock (statisticsLock) statisticsCache[key] = (DateTime.UtcNow, stats);
            return stats.ToStatistics();
        }

        private StatsSnapshot computeStatistics(int userId, int rulesetId)
        {
            List<BestScore> bests;
            (int plays, long total, int combo, long hits, long seconds) agg;
            long rankedScore;
            int ssPlus = 0, ss = 0, sPlus = 0, s = 0, a = 0;
            int replaysWatched;

            // one short-lived connection for the user's own rows, released before any shared cache below asks for one:
            // holding a pooled connection while another thread needs one to finish its refresh is how the pool deadlocks.
            using (var c = db.Open())
            {
                bests = Bests(c, userId, rulesetId);

                agg = c.Query("""
                    SELECT COUNT(*), COALESCE(SUM(total_score), 0), COALESCE(MAX(max_combo), 0), COALESCE(SUM(total_hits), 0), COALESCE(SUM(play_seconds), 0)
                    FROM scores WHERE user_id = @u AND ruleset_id = @r
                    """, r => (plays: r.GetInt32(0), total: r.GetInt64(1), combo: r.GetInt32(2), hits: r.GetInt64(3), seconds: r.GetInt64(4)),
                    ("@u", userId), ("@r", rulesetId)).Single();

                rankedScore = c.Scalar<long>("""
                    SELECT COALESCE(SUM(s), 0) FROM (SELECT MAX(total_score) AS s FROM scores WHERE user_id = @u AND ruleset_id = @r AND passed = 1 AND ranked = 1 GROUP BY beatmap_id) t
                    """, ("@u", userId), ("@r", rulesetId));

                foreach (var (rank, n) in c.Query("""
                             SELECT rank, COUNT(*) FROM (
                                 SELECT s.rank FROM scores s WHERE s.user_id = @u AND s.ruleset_id = @r AND s.passed = 1 AND s.ranked = 1
                                   AND s.id = (SELECT o.id FROM scores o WHERE o.user_id = s.user_id AND o.beatmap_id = s.beatmap_id AND o.ruleset_id = s.ruleset_id
                                               AND o.passed = 1 AND o.ranked = 1 ORDER BY o.total_score DESC, o.id LIMIT 1)) g
                             GROUP BY rank
                             """, r => (r.GetString(0), r.GetInt32(1)), ("@u", userId), ("@r", rulesetId)))
                {
                    switch (rank)
                    {
                        case "XH": ssPlus = n; break;
                        case "X": ss = n; break;
                        case "SH": sPlus = n; break;
                        case "S": s = n; break;
                        case "A": a = n; break;
                    }
                }

                replaysWatched = (int)c.Scalar<long>("SELECT COALESCE(SUM(count), 0) FROM replay_watches WHERE user_id = @u", ("@u", userId));
            }

            double acc = 0, accWeight = 0;
            for (int i = 0; i < bests.Count; i++)
            {
                double w = Math.Pow(0.95, i);
                acc += bests[i].Accuracy * w;
                accWeight += w;
            }

            double pp = bests.Count > 0 ? WeightedPp(bests) : 0;
            int? globalRank = null, countryRank = null;
            float? percent = null;
            if (pp > 0)
            {
                var board = Leaderboard(rulesetId);
                globalRank = board.Count(e => e.pp > pp) + 1;
                percent = board.Count > 0 ? (float)globalRank.Value / board.Count : null;

                var countryOf = countryMap();
                string? country = countryOf.GetValueOrDefault(userId);
                countryRank = board.Count(e => e.pp > pp && countryOf.GetValueOrDefault(e.userId) == country) + 1;
            }

            double level = levelFor(agg.total);
            return new StatsSnapshot(
                (decimal)Math.Round(pp, 2), accWeight > 0 ? acc / accWeight * 100 : 0, agg.plays, agg.total, rankedScore,
                agg.combo, (int)Math.Min(int.MaxValue, agg.hits), (int)Math.Min(int.MaxValue, agg.seconds), replaysWatched,
                globalRank, percent, countryRank, ssPlus, ss, sPlus, s, a,
                (int)level, (int)((level - (int)level) * 100));
        }

        /// <summary>Drops one player's cached statistics (their next profile or ranking read recomputes them).</summary>
        public void InvalidateStatistics(int userId)
        {
            lock (statisticsLock)
                foreach (var key in statisticsCache.Keys.Where(k => k.userId == userId).ToList())
                    statisticsCache.Remove(key);
        }

        private Dictionary<int, string> countryMap()
        {
            lock (countryLock)
                if (countryCache is { } cached && cached.at > DateTime.UtcNow.AddSeconds(-30)) return cached.map;

            Dictionary<int, string> map;
            using (var c = db.Open())
                map = c.Query("SELECT id, country FROM users", r => (r.GetInt32(0), r.GetString(1))).ToDictionary(r => r.Item1, r => r.Item2);

            lock (countryLock) countryCache = (DateTime.UtcNow, map);
            return map;
        }

        private (DateTime at, Dictionary<int, string> map)? countryCache;
        private readonly object countryLock = new object();

        /// <summary>All non-restricted users with pp, unsorted (cached briefly).</summary>
        public List<(int userId, double pp)> Leaderboard(int rulesetId)
        {
            lock (leaderboardLock)
            {
                if (leaderboardCache.TryGetValue(rulesetId, out var cached) && cached.at > DateTime.UtcNow.AddSeconds(-30))
                    return cached.list;
            }

            // computed outside every lock, and on a connection of its own: a thread waiting here may hold a pooled
            // connection, so the thread doing the refresh must never need one from under the same lock.
            leaderboardRefresh.Wait();
            try
            {
                lock (leaderboardLock)
                {
                    if (leaderboardCache.TryGetValue(rulesetId, out var cached) && cached.at > DateTime.UtcNow.AddSeconds(-30))
                        return cached.list;
                }

                List<(int, double)> list;
                using (var c = db.Open())
                {
                    var ids = c.Query("""
                        SELECT DISTINCT s.user_id FROM scores s JOIN users u ON u.id = s.user_id
                        WHERE s.ruleset_id = @r AND s.pp IS NOT NULL AND u.restricted = 0
                        """, r => r.GetInt32(0), ("@r", rulesetId));
                    list = new List<(int, double)>();
                    foreach (int id in ids)
                    {
                        var bests = Bests(c, id, rulesetId);
                        if (bests.Count > 0) list.Add((id, WeightedPp(bests)));
                    }
                }

                lock (leaderboardLock) leaderboardCache[rulesetId] = (DateTime.UtcNow, list);
                return list;
            }
            finally
            {
                leaderboardRefresh.Release();
            }
        }

        public void InvalidateLeaderboard()
        {
            lock (leaderboardLock) leaderboardCache.Clear();
        }

        /// <summary>Records today's rank for every ranked user (also keeps rank_highest up to date).</summary>
        public void SnapshotRanks()
        {
            InvalidateLeaderboard();
            string day = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // every board first, then the inserts: a connection is never held while Leaderboard refreshes.
            var boards = new List<(int ruleset, List<(int userId, double pp)> board)>();
            for (int ruleset = 0; ruleset < RULESET_NAMES.Length; ruleset++)
                boards.Add((ruleset, Leaderboard(ruleset).OrderByDescending(e => e.pp).ToList()));

            using var c = db.Open();
            foreach (var (ruleset, board) in boards)
            {
                for (int i = 0; i < board.Count; i++)
                {
                    c.Exec("INSERT INTO rank_history (user_id, ruleset_id, day, rank, pp) VALUES (@u, @r, @d, @k, @p) ON CONFLICT (user_id, ruleset_id, day) DO UPDATE SET rank = EXCLUDED.rank, pp = EXCLUDED.pp",
                        ("@u", board[i].userId), ("@r", ruleset), ("@d", day), ("@k", i + 1), ("@p", board[i].pp));
                    if (ruleset == 0)
                        c.Exec("UPDATE users SET rank_highest = @k, rank_highest_at = @t WHERE id = @u AND (rank_highest IS NULL OR rank_highest > @k)",
                            ("@k", i + 1), ("@t", DateTimeOffset.UtcNow.ToString("O")), ("@u", board[i].userId));
                }
            }
        }

        private readonly object leaderboardLock = new object();
        private readonly SemaphoreSlim leaderboardRefresh = new SemaphoreSlim(1, 1);
        private readonly Dictionary<int, (DateTime at, List<(int userId, double pp)> list)> leaderboardCache = new Dictionary<int, (DateTime, List<(int, double)>)>();

        private static double scoreForLevel(int n) => n <= 100
            ? 5000.0 / 3 * (4 * Math.Pow(n, 3) - 3 * Math.Pow(n, 2) - n) + 1.25 * Math.Pow(1.8, n - 60)
            : 26931190827 + 99999999999.0 * (n - 100);

        private static double levelFor(long score)
        {
            int n = 1;
            while (n < 120 && scoreForLevel(n + 1) <= score) n++;
            double cur = scoreForLevel(n), next = scoreForLevel(n + 1);
            return n + Math.Clamp((score - cur) / (next - cur), 0, 0.99);
        }

        public static string RankString(ScoreRank rank) => rank switch
        {
            ScoreRank.XH => "XH", ScoreRank.X => "X", ScoreRank.SH => "SH", ScoreRank.S => "S",
            ScoreRank.A => "A", ScoreRank.B => "B", ScoreRank.C => "C", ScoreRank.D => "D", _ => "F",
        };
    }
}
