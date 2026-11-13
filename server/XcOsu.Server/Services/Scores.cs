// xc-osu! server: score submission and lookup. xc softworks (kudasaixc)

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
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

        public sealed record SubmittedScore(SoloScoreInfo Score, long? PlaylistItemId, long? RoomId, double? StarRating, int? BeatmapMaxCombo);

        /// <summary>Raised after a score is stored.</summary>
        public event Func<SubmittedScore, Task>? Submitted;

        /// <summary>Raised with the score id once its replay is on disk.</summary>
        public event Action<long>? ReplayAttached;

        public const int TOKEN_LIFETIME_HOURS = 24;

        /// <summary>lazer's standardised score is ≤ 1,000,000 × mod multiplier; anything far above is not a legitimate play.</summary>
        private const long max_plausible_score = 2_000_000;

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

        public static int BeatmapStatus(JObject? map) => map?["status"]?.Type == JTokenType.String
            ? map.Value<string>("status") switch { "ranked" => 1, "approved" => 2, "qualified" => 3, "loved" => 4, "pending" => 0, "wip" => -1, "graveyard" => -2, _ => 0 }
            : map?.Value<int?>("ranked") ?? 0;

        public sealed class SubmitException : Exception
        {
            public readonly int Status;
            public SubmitException(string message, int status = 422) : base(message) => Status = status;
        }

        /// <summary>Creates a score token. Throws <see cref="SubmitException"/> when the play can't be submitted.</summary>
        public long CreateToken(int userId, int beatmapId, int rulesetId, string? beatmapHash, long? playlistItemId, long? roomId)
        {
            if (accounts.Get(userId) is not AccountRow row) throw new SubmitException("unknown user", 401);
            if (row.Restricted) throw new SubmitException("Your account is restricted.", 403);
            if (Performance.Ruleset(rulesetId) == null) throw new SubmitException("This ruleset isn't available on xc-osu!.");

            if (!AbuseGuard.Allow("score-token:" + userId, 30, TimeSpan.FromMinutes(1))) throw new SubmitException("Too many score tokens", 429);
            using var c = db.Open();
            using var tokenTransaction = c.BeginTransaction();
            // the capacity below is a count followed by an insert: two simultaneous submissions for the same player would
            // both see room for one more. Locking their row makes the pair behave like the single writer SQLite gave us.
            c.Exec("SELECT 1 FROM users WHERE id = @u FOR UPDATE", ("@u", userId));
            c.Exec("DELETE FROM score_tokens WHERE score_id IS NULL AND created_at < @t", ("@t", DateTimeOffset.UtcNow.AddHours(-TOKEN_LIFETIME_HOURS).ToUnixTimeSeconds()));
            if (c.Scalar<long>("SELECT COUNT(*) FROM score_tokens WHERE user_id = @u AND score_id IS NULL", ("@u", userId)) >= 100)
                throw new SubmitException("Too many outstanding score tokens", 429);
            long tokenId = c.Insert("""
                INSERT INTO score_tokens (user_id, beatmap_id, ruleset_id, playlist_item_id, room_id, beatmap_hash, created_at) VALUES (@u, @b, @r, @p, @rm, @h, @t)
                """, ("@u", userId), ("@b", beatmapId), ("@r", rulesetId), ("@p", playlistItemId), ("@rm", roomId), ("@h", beatmapHash),
                ("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            tokenTransaction.Commit();
            return tokenId;
        }

        /// <summary>Finalises a score against its token. Returns the stored score, or null if the token is not valid for this user.</summary>
        public async Task<SoloScoreInfo?> Submit(int userId, int beatmapId, long token, long? playlistItemId, SoloScoreInfo score, CancellationToken ct)
        {
            if (accounts.Get(userId) is not { Restricted: false }) throw new SubmitException("Account unavailable", 403);
            if (score.Mods.Length > 32 || score.MaximumStatistics.Count > 64 || score.MaximumStatistics.Any(x => x.Value < 0 || x.Value > 1_000_000) || score.Statistics.Count > 64 || score.Statistics.Any(x => x.Value < 0 || x.Value > 1_000_000))
                throw new SubmitException("Invalid score statistics");
            string? tokenHash;
            long? roomId;
            using (var c = db.Open())
            {
                var t = c.Query("SELECT user_id, beatmap_id, ruleset_id, score_id, beatmap_hash, playlist_item_id, room_id, created_at FROM score_tokens WHERE id = @t",
                    r => (u: r.GetInt32(0), b: r.GetInt32(1), rs: r.GetInt32(2), s: r.IsDBNull(3) ? (long?)null : r.GetInt64(3), h: r.IsDBNull(4) ? null : r.GetString(4),
                        p: r.IsDBNull(5) ? (long?)null : r.GetInt64(5), room: r.IsDBNull(6) ? (long?)null : r.GetInt64(6), at: r.GetInt64(7)), ("@t", token)).FirstOrDefault();
                if (t == default || t.u != userId || t.b != beatmapId || t.p != playlistItemId) return null;
                if (t.s != null)
                    return Load(c, new[] { t.s.Value }, accounts, users, withBeatmap: false).FirstOrDefault();
                if (DateTimeOffset.FromUnixTimeSeconds(t.at) < DateTimeOffset.UtcNow.AddHours(-TOKEN_LIFETIME_HOURS)) return null;
                if (score.EndedAt != default && (score.EndedAt < DateTimeOffset.FromUnixTimeSeconds(t.at).AddMinutes(-5)
                    || score.EndedAt > DateTimeOffset.UtcNow.AddMinutes(5)
                    || (score.StartedAt is DateTimeOffset start && (start > score.EndedAt || start < DateTimeOffset.FromUnixTimeSeconds(t.at).AddMinutes(-5)))))
                {
                    // client clocks drift: never reject a play for it, just stop trusting its timestamps.
                    score.EndedAt = DateTimeOffset.UtcNow;
                    score.StartedAt = null;
                }
                score.RulesetID = t.rs;
                tokenHash = t.h;
                roomId = t.room;
            }

            score.UserID = userId;
            score.BeatmapID = beatmapId;
            score.EndedAt = score.EndedAt == default || score.EndedAt > DateTimeOffset.UtcNow.AddMinutes(5) ? DateTimeOffset.UtcNow : score.EndedAt;
            score.Accuracy = Math.Clamp(score.Accuracy, 0, 1);
            if (score.TotalScore < 0 || score.MaxCombo < 0) throw new SubmitException("invalid score");

            var map = await mirrors.GetBeatmap(beatmapId, null, null, ct);
            int status = BeatmapStatus(map);
            string? onlineHash = map?.Value<string>("checksum");
            int? mapMaxCombo = map?.Value<int?>("max_combo");

            var ruleset = Performance.Ruleset(score.RulesetID)!;
            bool modsRanked = score.Mods.All(m =>
            {
                try { return m.ToMod(ruleset).Ranked; }
                catch { return false; }
            });

            bool plausible = score.TotalScore <= max_plausible_score && (mapMaxCombo == null || score.MaxCombo <= mapMaxCombo + 1);
            bool hashMatches = !string.IsNullOrEmpty(tokenHash) && !string.IsNullOrEmpty(onlineHash) && string.Equals(tokenHash, onlineHash, StringComparison.OrdinalIgnoreCase);
            bool ranked = Eligible(score, map, tokenHash) && playlistItemId == null;
            if (!plausible) log.LogWarning("implausible score from user {User} on {Beatmap}: {Score} x{Combo}", userId, beatmapId, score.TotalScore, score.MaxCombo);

            Performance.ScoreCalculation? calc = null;
            if (score.Passed && map != null)
                calc = await performance.Calculate(beatmapId, score, ct);

            // pp is awarded on ranked/approved/qualified maps only; loved keeps a leaderboard without pp, like osu!.
            double? pp = ranked && status is 1 or 2 or 3 && score.Passed ? calc?.Pp : null;
            if (playlistItemId != null && score.Passed) pp = calc?.Pp; // shown on playlist results, never counted towards profile pp (ranked = 0)

            score.PP = pp;
            score.Ranked = ranked;

            int totalHits = score.Statistics.Where(s => s.Key.IsHit() && s.Key.IsBasic()).Sum(s => s.Value);
            int playSeconds = score.StartedAt is DateTimeOffset started ? (int)Math.Clamp((score.EndedAt - started).TotalSeconds, 0, calc?.Length + 30 ?? 3600) : (int)(calc?.Length ?? 0);

            long id;
            using (var c = db.Open())
            using (var tx = c.BeginTransaction())
            {
                long? alreadyStored = c.Scalar<long?>("SELECT score_id FROM score_tokens WHERE id = @t FOR UPDATE", ("@t", token));
                if (alreadyStored != null) return Load(c, new[] { alreadyStored.Value }, accounts, users, false).FirstOrDefault();
                if (c.Scalar<long>("SELECT COUNT(*) FROM users WHERE id = @u AND restricted = 0", ("@u", userId)) != 1)
                    throw new SubmitException("Account unavailable", 403);
                id = c.Insert("""
                    INSERT INTO scores (user_id, beatmap_id, ruleset_id, playlist_item_id, total_score, accuracy, max_combo, rank, passed, pp, ranked, mods, data, ended_at, started_at,
                                        has_replay, total_hits, play_seconds, legacy_total_score, replay_due)
                    VALUES (@u, @b, @r, @p, @ts, @a, @mc, @rk, @ps, @pp, @rd, @m, @d, @e, @st, 0, @th, @sec, @lts, @due)
                    """,
                    // a ranked pass has to back its numbers with a replay (see ReplayVerification).
                    ("@due", ranked && score.Passed && ReplayVerification.ReplayGraceSeconds is int grace ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + grace : null),
                    ("@u", userId), ("@b", beatmapId), ("@r", score.RulesetID), ("@p", playlistItemId), ("@ts", score.TotalScore), ("@a", score.Accuracy),
                    ("@mc", score.MaxCombo), ("@rk", Users.RankString(score.Rank)), ("@ps", score.Passed ? 1 : 0), ("@pp", pp), ("@rd", ranked ? 1 : 0),
                    ("@m", JsonConvert.SerializeObject(score.Mods)), ("@d", "{}"), ("@e", score.EndedAt.ToString("O")), ("@st", score.StartedAt?.ToString("O")),
                    ("@th", totalHits), ("@sec", playSeconds), ("@lts", score.LegacyTotalScore ?? 0));

                score.ID = (ulong)id;
                score.User = null;
                score.Beatmap = null;
                c.Exec("UPDATE scores SET data = @d WHERE id = @i", ("@d", JsonConvert.SerializeObject(score, Api.Json.SETTINGS)), ("@i", id));
                c.Exec("UPDATE score_tokens SET score_id = @s WHERE id = @t", ("@s", id), ("@t", token));
                c.Exec("""
                    INSERT INTO beatmap_playcounts (user_id, beatmap_id, count) VALUES (@u, @b, 1)
                    ON CONFLICT(user_id, beatmap_id) DO UPDATE SET count = beatmap_playcounts.count + 1
                    """, ("@u", userId), ("@b", beatmapId));
                tx.Commit();
            }

            users.InvalidateLeaderboard();
            users.InvalidateStatistics(userId);
            log.LogInformation("score {Id}: user {User} beatmap {Beatmap} {Score} pp={Pp} ranked={Ranked}", id, userId, beatmapId, score.TotalScore, pp, ranked);

            List<SoloScoreInfo> stored;
            using (var c = db.Open())
            {
                stored = Load(c, new[] { id }, accounts, users, withBeatmap: false);
                if (ranked && score.Passed && playlistItemId == null)
                    recordRankEvents(c, stored[0], map);
            }

            if (Submitted != null)
            {
                var submitted = new SubmittedScore(stored[0], playlistItemId, roomId, calc?.StarRating, calc?.MaxCombo ?? mapMaxCombo);
                foreach (var handler in Submitted.GetInvocationList().Cast<Func<SubmittedScore, Task>>())
                {
                    try { await handler(submitted); }
                    catch (Exception e) { log.LogWarning(e, "post-submission handler failed for score {Id}", id); }
                }
            }

            return stored[0];
        }

        /// <summary>osu-web style "rank" (top 50 on a beatmap) and "rank_lost" (lost #1) activity events.</summary>
        private void recordRankEvents(Npgsql.NpgsqlConnection c, SoloScoreInfo score, JObject? map)
        {
            var board = c.Query("""
                SELECT id, user_id FROM scores WHERE beatmap_id = @b AND ruleset_id = @r AND passed = 1 AND ranked = 1 AND playlist_item_id IS NULL
                ORDER BY total_score DESC, id ASC
                """, r => (id: r.GetInt64(0), user: r.GetInt32(1)), ("@b", score.BeatmapID), ("@r", score.RulesetID));

            var seen = new HashSet<int>();
            var bestPerUser = board.Where(r => seen.Add(r.user)).ToList();
            int position = bestPerUser.FindIndex(r => r.id == (long)score.ID!.Value) + 1;
            if (position == 0 || position > 50) return;

            string title = map?["beatmapset"] is JObject set
                ? $"{set.Value<string>("artist")} - {set.Value<string>("title")} [{map.Value<string>("version")}]"
                : $"beatmap #{score.BeatmapID}";
            var beatmap = new JObject { ["title"] = title, ["url"] = $"/b/{score.BeatmapID}?m={score.RulesetID}" };
            string mode = Users.RULESET_NAMES[Math.Clamp(score.RulesetID, 0, 3)];
            string now = DateTimeOffset.UtcNow.ToString("O");

            AddEvent(c, score.UserID, "rank", new JObject { ["scoreRank"] = Users.RankString(score.Rank), ["rank"] = position, ["mode"] = mode, ["beatmap"] = beatmap }, now);

            if (position == 1 && bestPerUser.Count > 1 && bestPerUser[1].user != score.UserID)
            {
                // only if the previous #1 was the previous holder before this play
                AddEvent(c, bestPerUser[1].user, "rank_lost", new JObject { ["mode"] = mode, ["beatmap"] = beatmap }, now);
            }
        }

        public void AddEvent(Npgsql.NpgsqlConnection c, int userId, string type, JObject payload, string? at = null)
        {
            var row = accounts.Get(userId, c);
            if (row != null) payload["user"] ??= new JObject { ["username"] = row.Username, ["url"] = $"/users/{userId}" };
            c.Exec("INSERT INTO user_events (user_id, type, json, created_at) VALUES (@u, @t, @j, @c)",
                ("@u", userId), ("@t", type), ("@j", payload.ToString(Formatting.None)), ("@c", at ?? DateTimeOffset.UtcNow.ToString("O")));
        }

        public void AttachReplay(long scoreToken, byte[] osr)
        {
            using var c = db.Open();
            long? scoreId = c.Scalar<long?>("SELECT score_id FROM score_tokens WHERE id = @t", ("@t", scoreToken));
            if (scoreId == null) return;
            File.WriteAllBytes(Path.Combine(ReplayDirectory, $"{scoreId}.osr"), osr);
            c.Exec("UPDATE scores SET has_replay = 1 WHERE id = @i", ("@i", scoreId));
            ReplayAttached?.Invoke(scoreId.Value);
        }

        /// <summary>Counts a replay download towards the score owner's "replays watched by others".</summary>
        public void CountReplayWatch(long scoreId, int? viewer)
        {
            using var c = db.Open();
            int? owner = (int?)c.Scalar<long?>("SELECT user_id FROM scores WHERE id = @i", ("@i", scoreId));
            if (owner == null || owner == viewer) return;
            c.Exec("""
                INSERT INTO replay_watches (user_id, month, count) VALUES (@u, @m, 1) ON CONFLICT(user_id, month) DO UPDATE SET count = replay_watches.count + 1
                """, ("@u", owner), ("@m", DateTime.UtcNow.ToString("yyyy-MM")));
        }

        private static bool Eligible(SoloScoreInfo score, JObject? map, string? tokenHash, bool legacy = false)
        {
            var ruleset = Performance.Ruleset(score.RulesetID);
            string? onlineHash = map?.Value<string>("checksum");
            int? maxCombo = map?.Value<int?>("max_combo");
            if (ruleset == null || !Users.PP_STATUSES.Contains(BeatmapStatus(map)) || score.TotalScore > max_plausible_score
                || score.TotalScore < 0 || score.MaxCombo < 0 || (maxCombo != null && score.MaxCombo > maxCombo + 1)
                // the client always sends its beatmap hash; scores stored before hashes were kept and maps
                // whose mirror entry has no checksum keep the previous lenient behaviour.
                || (string.IsNullOrEmpty(tokenHash) && !legacy)
                || (!string.IsNullOrEmpty(tokenHash) && !string.IsNullOrEmpty(onlineHash) && !string.Equals(tokenHash, onlineHash, StringComparison.OrdinalIgnoreCase))) return false;
            return score.Mods.All(m => { try { return m.ToMod(ruleset).Ranked; } catch { return false; } });
        }

        /// <summary>Recomputes pp / ranked flags for a beatmap whose status changed on the mirrors.</summary>
        public async Task ReprocessBeatmap(int beatmapId, CancellationToken ct)
        {
            var map = await mirrors.GetBeatmap(beatmapId, null, null, ct);
            int status = BeatmapStatus(map);
            List<(long id, string data, int ranked)> rows;
            using (var c = db.Open())
                // a score refused by replay verification stays refused whatever happens to the beatmap.
                rows = c.Query($"SELECT id, data, ranked FROM scores WHERE beatmap_id = @b AND playlist_item_id IS NULL AND verification NOT IN ({ReplayVerification.MISMATCH}, {ReplayVerification.MISSING_REPLAY})",
                    r => (r.GetInt64(0), r.GetString(1), r.GetInt32(2)), ("@b", beatmapId));

            foreach (var (id, data, wasRanked) in rows)
            {
                var s = JsonConvert.DeserializeObject<SoloScoreInfo>(data, Api.Json.SETTINGS)!;
                var ruleset = Performance.Ruleset(s.RulesetID);
                if (ruleset == null) continue;
                bool modsRanked = s.Mods.All(m => { try { return m.ToMod(ruleset).Ranked; } catch { return false; } });
                string? checksum;
                using (var source = db.Open()) checksum = source.Scalar<string>("SELECT beatmap_hash FROM score_tokens WHERE score_id = @s LIMIT 1", ("@s", id));
                bool ranked = Eligible(s, map, checksum, legacy: true);
                double? pp = ranked && status is 1 or 2 or 3 && s.Passed ? await performance.CalculatePp(beatmapId, s, ct) : null;
                using var c = db.Open();
                c.Exec("UPDATE scores SET ranked = @r, pp = @p WHERE id = @i", ("@r", ranked ? 1 : 0), ("@p", pp), ("@i", id));
            }

            users.InvalidateLeaderboard();
        }

        public static List<SoloScoreInfo> Load(Npgsql.NpgsqlConnection c, IEnumerable<long> ids, Accounts accounts, Users users, bool withBeatmap)
        {
            var idList = ids.ToList();
            if (idList.Count == 0) return new List<SoloScoreInfo>();

            var rows = c.Query($"SELECT id, data, pp, has_replay, ranked, beatmap_id FROM scores WHERE id IN ({string.Join(',', idList)})",
                r => (id: r.GetInt64(0), data: r.GetString(1), pp: r.IsDBNull(2) ? (double?)null : r.GetDouble(2), replay: r.GetInt32(3) != 0, ranked: r.GetInt32(4) != 0, beatmap: r.GetInt32(5)))
                .ToDictionary(r => r.id);

            var result = new List<SoloScoreInfo>();
            var userCache = new Dictionary<int, APIUser>();
            var beatmapCache = new Dictionary<int, (APIBeatmap? b, APIBeatmapSet? s)>();
            foreach (long id in idList)
            {
                if (!rows.TryGetValue(id, out var row)) continue;
                var s = JsonConvert.DeserializeObject<SoloScoreInfo>(row.data, Api.Json.SETTINGS)!;
                s.ID = (ulong)row.id;
                s.PP = row.pp;
                s.HasReplay = row.replay;
                s.Ranked = row.ranked;
                s.Preserve = true;
                s.Processed = true;
                if (!userCache.TryGetValue(s.UserID, out var u))
                {
                    var acc = accounts.Get(s.UserID, c);
                    userCache[s.UserID] = u = acc != null ? users.Compact(acc, c) : new APIUser { Id = s.UserID, Username = "?" };
                }

                s.User = u;

                if (withBeatmap)
                {
                    if (!beatmapCache.TryGetValue(row.beatmap, out var bm))
                    {
                        string? json = c.Scalar<string>("SELECT json FROM beatmaps WHERE id = @b", ("@b", row.beatmap));
                        if (json != null)
                        {
                            var jo = JObject.Parse(json);
                            bm = (jo.ToObject<APIBeatmap>(), jo["beatmapset"] is JObject set ? set.ToObject<APIBeatmapSet>() : null);
                        }

                        beatmapCache[row.beatmap] = bm;
                    }

                    s.Beatmap = bm.b;
                    if (bm.b != null) s.BeatmapSet = bm.s;
                }

                result.Add(s);
            }

            return result;
        }
    }
}
