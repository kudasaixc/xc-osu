// xc-osu! server — score submission and lookup. xc softworks (kudasaixc)

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API.Requests.Responses;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed class Scores
    {
        private readonly Db db;
        private readonly Users users;
        private readonly Accounts accounts;
        private readonly Mirrors mirrors;
        private readonly Performance performance;
        private readonly ILogger<Scores> log;

        public event Action<SoloScoreInfo, long?, long?>? Submitted;

        public Scores(Db db, Users users, Accounts accounts, Mirrors mirrors, Performance performance, ILogger<Scores> log)
        {
            this.db = db;
            this.users = users;
            this.accounts = accounts;
            this.mirrors = mirrors;
            this.performance = performance;
            this.log = log;
            Directory.CreateDirectory(ReplayDirectory);
        }

        public string ReplayDirectory => Path.Combine(db.DataDirectory, "replays");

        public string? ReplayPath(long scoreId)
        {
            string p = Path.Combine(ReplayDirectory, $"{scoreId}.osr");
            return File.Exists(p) ? p : null;
        }

        /// <summary>Finalises a score against its token. Returns the stored score, or null if the token is not valid for this user.</summary>
        public async Task<SoloScoreInfo?> Submit(int userId, int beatmapId, long token, long? playlistItemId, SoloScoreInfo score, CancellationToken ct)
        {
            using (var c = db.Open())
            {
                var t = c.Query("SELECT user_id, beatmap_id, score_id FROM score_tokens WHERE id = $t", r => (u: r.GetInt32(0), b: r.GetInt32(1), s: r.IsDBNull(2) ? (long?)null : r.GetInt64(2)), ("$t", token)).FirstOrDefault();
                if (t == default || t.u != userId || t.b != beatmapId) return null;
                if (t.s != null)
                    return Load(c, new[] { t.s.Value }, accounts, users, withBeatmap: false).FirstOrDefault();
            }

            score.UserID = userId;
            score.BeatmapID = beatmapId;
            score.EndedAt = score.EndedAt == default ? DateTimeOffset.UtcNow : score.EndedAt;
            score.Accuracy = Math.Clamp(score.Accuracy, 0, 1);

            var map = await mirrors.GetBeatmap(beatmapId, null, null, ct);
            int status = map?["status"]?.Type == JTokenType.String
                ? map.Value<string>("status") switch { "ranked" => 1, "approved" => 2, "qualified" => 3, "loved" => 4, _ => 0 }
                : map?.Value<int?>("ranked") ?? 0;
            bool ranked = Users.PP_STATUSES.Contains(status) && score.Mods.All(m => m.Acronym is not ("AT" or "CN" or "RX" or "AP" or "TP" or "DA" or "WU" or "WD" or "AS"));

            double? pp = null;
            if (score.Passed && ranked)
                pp = await performance.CalculatePp(beatmapId, score, ct);
            if (status == 4) pp = pp == null ? null : 0; // loved: leaderboard but no pp, like osu!

            score.PP = pp;
            score.Ranked = ranked;

            long id;
            using (var c = db.Open())
            {
                id = c.Insert("""
                    INSERT INTO scores (user_id, beatmap_id, ruleset_id, playlist_item_id, total_score, accuracy, max_combo, rank, passed, pp, ranked, mods, data, ended_at, has_replay)
                    VALUES ($u, $b, $r, $p, $ts, $a, $mc, $rk, $ps, $pp, $rd, $m, $d, $e, 0)
                    """,
                    ("$u", userId), ("$b", beatmapId), ("$r", score.RulesetID), ("$p", playlistItemId), ("$ts", score.TotalScore), ("$a", score.Accuracy),
                    ("$mc", score.MaxCombo), ("$rk", Users.RankString(score.Rank)), ("$ps", score.Passed ? 1 : 0), ("$pp", pp), ("$rd", ranked ? 1 : 0),
                    ("$m", JsonConvert.SerializeObject(score.Mods)), ("$d", "{}"), ("$e", score.EndedAt.ToString("O")));

                score.ID = (ulong)id;
                score.User = null;
                score.Beatmap = null;
                c.Exec("UPDATE scores SET data = $d WHERE id = $i", ("$d", JsonConvert.SerializeObject(score, Api.Json.SETTINGS)), ("$i", id));
                c.Exec("UPDATE score_tokens SET score_id = $s WHERE id = $t", ("$s", id), ("$t", token));
            }

            users.InvalidateLeaderboard();
            log.LogInformation("score {Id}: user {User} beatmap {Beatmap} {Score} pp={Pp}", id, userId, beatmapId, score.TotalScore, pp);

            var stored = Load(db.Open(), new[] { id }, accounts, users, withBeatmap: false).First();
            Submitted?.Invoke(stored, playlistItemId, token);
            return stored;
        }

        public void AttachReplay(long scoreToken, byte[] osr)
        {
            using var c = db.Open();
            long? scoreId = c.Scalar<long?>("SELECT score_id FROM score_tokens WHERE id = $t", ("$t", scoreToken));
            if (scoreId == null) return;
            File.WriteAllBytes(Path.Combine(ReplayDirectory, $"{scoreId}.osr"), osr);
            c.Exec("UPDATE scores SET has_replay = 1 WHERE id = $i", ("$i", scoreId));
        }

        public static List<SoloScoreInfo> Load(Microsoft.Data.Sqlite.SqliteConnection c, IEnumerable<long> ids, Accounts accounts, Users users, bool withBeatmap)
        {
            var idList = ids.ToList();
            if (idList.Count == 0) return new List<SoloScoreInfo>();

            var rows = c.Query($"SELECT id, data, pp, has_replay, ranked, beatmap_id FROM scores WHERE id IN ({string.Join(',', idList)})",
                r => (id: r.GetInt64(0), data: r.GetString(1), pp: r.IsDBNull(2) ? (double?)null : r.GetDouble(2), replay: r.GetInt32(3) != 0, ranked: r.GetInt32(4) != 0, beatmap: r.GetInt32(5)))
                .ToDictionary(r => r.id);

            var result = new List<SoloScoreInfo>();
            var userCache = new Dictionary<int, APIUser>();
            foreach (long id in idList)
            {
                if (!rows.TryGetValue(id, out var row)) continue;
                var s = JsonConvert.DeserializeObject<SoloScoreInfo>(row.data, Api.Json.SETTINGS)!;
                s.ID = (ulong)row.id;
                s.PP = row.pp;
                s.HasReplay = row.replay;
                s.Ranked = row.ranked;
                if (!userCache.TryGetValue(s.UserID, out var u))
                {
                    var acc = accounts.Get(s.UserID);
                    userCache[s.UserID] = u = acc != null ? users.Compact(acc) : new APIUser { Id = s.UserID, Username = "?" };
                }

                s.User = u;

                if (withBeatmap)
                {
                    string? json = c.Scalar<string>("SELECT json FROM beatmaps WHERE id = $b", ("$b", row.beatmap));
                    if (json != null)
                    {
                        var jo = JObject.Parse(json);
                        s.Beatmap = jo.ToObject<APIBeatmap>();
                        if (jo["beatmapset"] is JObject set) s.BeatmapSet = set.ToObject<APIBeatmapSet>();
                    }
                }

                result.Add(s);
            }

            return result;
        }
    }
}
