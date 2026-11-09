// xc-osu! server — user profiles and statistics. xc softworks (kudasaixc)

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

        public Users(Db db, Accounts accounts, Presence presence)
        {
            this.db = db;
            this.accounts = accounts;
            this.presence = presence;
        }

        public static readonly int[] PP_STATUSES = { 1, 2, 3, 4 };

        public APIUser Compact(AccountRow row) => new APIUser
        {
            Id = row.Id,
            Username = row.Username,
            CountryCode = Enum.TryParse(row.Country, out CountryCode cc) ? cc : CountryCode.Unknown,
            IsAdmin = row.IsAdmin,
            IsSupporter = true,
            SupportLevel = 3,
            Active = true,
            WasRecentlyOnline = presence.IsOnline(row.Id),
            Title = row.Title,
            Colour = row.IsAdmin ? "#ff66aa" : null,
            JoinDate = row.JoinDate,
            PlayMode = row.Playmode,
        };

        public APIUser? Full(int id, int rulesetId)
        {
            var row = accounts.Get(id);
            return row == null ? null : Full(row, rulesetId);
        }

        public APIUser Full(AccountRow row, int rulesetId)
        {
            var user = Compact(row);
            var stats = Statistics(row.Id, rulesetId);
            user.Statistics = stats;
            user.RulesetsStatistics = new Dictionary<string, UserStatistics> { ["osu"] = stats };
            user.Cover = new APIUser.UserCover { Url = null };
            user.Kudosu = new APIUser.KudosuCount();
            user.Badges = row.IsAdmin
                ? new[] { new Badge { Description = "xc softworks team", ImageUrl = null, AwardedAt = row.JoinDate } }
                : Array.Empty<Badge>();
            user.Groups = row.IsAdmin
                ? new[] { new APIUserGroup { Id = 1, Identifier = "xc", Name = "xc softworks", ShortName = "XC", Colour = "#ff66aa", IsProbationary = false } }
                : Array.Empty<APIUserGroup>();
            user.ProfileOrder = new[] { "me", "recent_activity", "top_ranks", "historical", "beatmaps", "kudosu", "medals" };
            user.Achievements = Array.Empty<APIUserAchievement>();
            user.MonthlyPlayCounts = Array.Empty<APIUserHistoryCount>();
            user.ReplaysWatchedCounts = Array.Empty<APIUserHistoryCount>();
            user.LastVisit = DateTimeOffset.UtcNow;

            using var c = db.Open();
            user.ScoresBestCount = (int)c.Scalar<long>("SELECT COUNT(DISTINCT beatmap_id) FROM scores WHERE user_id = $u AND passed = 1 AND pp IS NOT NULL", ("$u", row.Id));
            user.ScoresRecentCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM scores WHERE user_id = $u AND ended_at > $t", ("$u", row.Id), ("$t", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")));
            user.ScoresFirstCount = (int)c.Scalar<long>("""
                SELECT COUNT(*) FROM (SELECT beatmap_id, user_id, MAX(total_score) AS s FROM scores WHERE passed = 1 GROUP BY beatmap_id)
                WHERE user_id = $u
                """, ("$u", row.Id));
            user.FavouriteBeatmapsetCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM favourites WHERE user_id = $u", ("$u", row.Id));
            user.FollowerCount = (int)c.Scalar<long>("SELECT COUNT(*) FROM friends WHERE target_id = $u", ("$u", row.Id));
            if (stats.GlobalRank != null)
                user.Rank = new APIUser.GlobalRank { Rank = stats.GlobalRank, RulesetId = rulesetId };
            return user;
        }

        public sealed record BestScore(long ScoreId, int BeatmapId, double Pp, double Accuracy);

        /// <summary>The best pp-awarding score per beatmap, highest first.</summary>
        public List<BestScore> Bests(Microsoft.Data.Sqlite.SqliteConnection c, int userId, int rulesetId) => c.Query("""
            SELECT id, beatmap_id, MAX(pp), accuracy FROM scores
            WHERE user_id = $u AND ruleset_id = $r AND passed = 1 AND pp IS NOT NULL AND ranked = 1
            GROUP BY beatmap_id ORDER BY MAX(pp) DESC
            """, r => new BestScore(r.GetInt64(0), r.GetInt32(1), r.GetDouble(2), r.GetDouble(3)), ("$u", userId), ("$r", rulesetId));

        public UserStatistics Statistics(int userId, int rulesetId)
        {
            using var c = db.Open();
            var bests = Bests(c, userId, rulesetId);

            double pp = 0, acc = 0, accWeight = 0;
            for (int i = 0; i < bests.Count; i++)
            {
                double w = Math.Pow(0.95, i);
                pp += bests[i].Pp * w;
                acc += bests[i].Accuracy * w;
                accWeight += w;
            }

            // osu-web bonus pp for number of ranked plays
            pp += 416.6667 * (1 - Math.Pow(0.9994, bests.Count));

            var agg = c.Query("""
                SELECT COUNT(*), COALESCE(SUM(total_score), 0), COALESCE(MAX(max_combo), 0) FROM scores WHERE user_id = $u AND ruleset_id = $r
                """, r => (plays: r.GetInt32(0), total: r.GetInt64(1), combo: r.GetInt32(2)), ("$u", userId), ("$r", rulesetId)).Single();

            long rankedScore = c.Scalar<long>("""
                SELECT COALESCE(SUM(s), 0) FROM (SELECT MAX(total_score) AS s FROM scores WHERE user_id = $u AND ruleset_id = $r AND passed = 1 AND ranked = 1 GROUP BY beatmap_id)
                """, ("$u", userId), ("$r", rulesetId));

            var grades = new UserStatistics.Grades();
            foreach (var (rank, n) in c.Query("""
                         SELECT rank, COUNT(*) FROM (SELECT beatmap_id, rank, MAX(total_score) FROM scores WHERE user_id = $u AND ruleset_id = $r AND passed = 1 GROUP BY beatmap_id) GROUP BY rank
                         """, r => (r.GetString(0), r.GetInt32(1)), ("$u", userId), ("$r", rulesetId)))
            {
                switch (rank)
                {
                    case "XH": grades.SSPlus = n; break;
                    case "X": grades.SS = n; break;
                    case "SH": grades.SPlus = n; break;
                    case "S": grades.S = n; break;
                    case "A": grades.A = n; break;
                }
            }

            int? globalRank = null;
            if (pp > 0)
            {
                int better = Leaderboard(rulesetId).Count(e => e.pp > pp);
                globalRank = better + 1;
            }

            double level = levelFor(agg.total);
            return new UserStatistics
            {
                IsRanked = pp > 0,
                PP = (decimal)Math.Round(pp, 2),
                Accuracy = accWeight > 0 ? acc / accWeight * 100 : 0,
                PlayCount = agg.plays,
                TotalScore = agg.total,
                RankedScore = rankedScore,
                MaxCombo = agg.combo,
                GlobalRank = globalRank,
                CountryRank = globalRank,
                GradesCount = grades,
                Level = new UserStatistics.LevelInfo { Current = (int)level, Progress = (int)((level - (int)level) * 100) },
                PlayTime = 0,
            };
        }

        /// <summary>All users with pp, unsorted (small server — cheap enough to compute on demand, cached briefly).</summary>
        public List<(int userId, double pp)> Leaderboard(int rulesetId)
        {
            lock (leaderboardLock)
            {
                if (leaderboardCache.TryGetValue(rulesetId, out var cached) && cached.at > DateTime.UtcNow.AddSeconds(-30))
                    return cached.list;

                using var c = db.Open();
                var ids = c.Query("SELECT DISTINCT user_id FROM scores WHERE ruleset_id = $r AND pp IS NOT NULL", r => r.GetInt32(0), ("$r", rulesetId));
                var list = new List<(int, double)>();
                foreach (int id in ids)
                {
                    var bests = Bests(c, id, rulesetId);
                    double pp = bests.Select((b, i) => b.Pp * Math.Pow(0.95, i)).Sum() + 416.6667 * (1 - Math.Pow(0.9994, bests.Count));
                    list.Add((id, pp));
                }

                leaderboardCache[rulesetId] = (DateTime.UtcNow, list);
                return list;
            }
        }

        public void InvalidateLeaderboard()
        {
            lock (leaderboardLock) leaderboardCache.Clear();
        }

        private readonly object leaderboardLock = new object();
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
