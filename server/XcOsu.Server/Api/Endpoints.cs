// xc-osu! server — the osu! API v2 surface used by the lazer client. xc softworks (kudasaixc)

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;
using osu.Game.Users;
using XcOsu.Server.Data;
using XcOsu.Server.Services;
using UsersService = XcOsu.Server.Services.Users;

namespace XcOsu.Server.Api
{
    public static class Endpoints
    {
        public static void Map(WebApplication app)
        {
            mapAuth(app);
            PasswordReset.Map(app);

            var api = app.MapGroup("/api/v2");
            mapUsers(api);
            mapBeatmaps(api);
            mapScores(api);
            mapRankings(api);
            mapChat(api);
            mapSocial(api);
            mapComments(api);
            mapMisc(api);
            Rooms.Map(api);
            Media.Map(app);
            Teams.Map(app);
        }

        private static int? uid(HttpContext ctx) => ctx.User.UserId();

        private static IResult unauthorized() => Json.Error("unauthorized", 401);

        private static readonly JsonSerializer serializer = JsonSerializer.Create(Json.SETTINGS);

        // A search page is the heaviest GET in the app (full mirror beatmapsets decorated with our counts, ~230 KB), and
        // popular terms are requested by many clients at once. Sharing the serialized page for a few seconds turns a
        // burst into one page build instead of one per client.
        private static readonly Dictionary<string, (DateTime at, string json)> searchResponses = new();
        private static readonly object searchResponsesLock = new();
        private static readonly TimeSpan searchResponseTtl = TimeSpan.FromSeconds(10);

        private static JObject jo(object o) => JObject.FromObject(o, serializer);

        // ------------------------------------------------------------------ auth

        private static void mapAuth(WebApplication app)
        {
            app.MapPost("/oauth/token", async (HttpRequest req, Accounts accounts, ILoggerFactory loggerFactory) =>
            {
                var p = await Json.Params(req);
                bool browser = p("browser_session") == "1";
                if (browser && !BrowserSession.SameOrigin(req)) return Results.StatusCode(403);
                int? userId = null;
                (string access, string refresh)? pair = null;
                string ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                IResult limited()
                {
                    req.HttpContext.Response.Headers.RetryAfter = "60";
                    return Json.Ok(new { error = "too_many_attempts", error_description = "Too many attempts. Please wait a minute.", message = "Too many attempts. Please wait a minute." }, 429);
                }

                switch (p("grant_type"))
                {
                    case "password":
                        // password guessing is what needs a tight per-address budget; the per-account budget lives in Accounts.Verify.
                        if (!AbuseGuard.Allow("ip:login-password:" + ip, AbuseGuard.PASSWORD_SIGNINS_PER_IP, TimeSpan.FromMinutes(1))) return limited();
                        var row = accounts.Verify(p("username") ?? "", p("password") ?? "");
                        userId = row?.Id;
                        if (userId == null)
                        {
                            loggerFactory.CreateLogger("Security.Authentication").LogWarning("Password sign-in rejected from {Address}", req.HttpContext.Connection.RemoteIpAddress);
                            return Json.Ok(new { error = "invalid_grant", error_description = "Incorrect sign in", hint = "Incorrect sign in", message = "Incorrect sign in" }, 400);
                        }
                        break;

                    case "refresh_token":
                        // a refresh carries a 256-bit single-use secret: it cannot be guessed, so a whole campus or CGNAT behind one
                        // address may refresh freely. Only failures are budgeted tightly.
                        if (!AbuseGuard.Allow("ip:login-refresh:" + ip, AbuseGuard.REFRESHES_PER_IP, TimeSpan.FromMinutes(1))
                            || AbuseGuard.Count("ip:login-refresh-failed:" + ip) >= AbuseGuard.FAILED_REFRESHES_PER_IP) return limited();
                        var rotated = accounts.Rotate(p("refresh_token") is { Length: > 0 } supplied ? supplied : browser ? req.Cookies[BrowserSession.RefreshCookie] ?? "" : "");
                        userId = rotated?.UserId;
                        if (rotated != null) pair = (rotated.Value.access, rotated.Value.refresh);
                        else AbuseGuard.Allow("ip:login-refresh-failed:" + ip, int.MaxValue, TimeSpan.FromMinutes(1));
                        if (userId == null)
                            return Json.Ok(new { error = "invalid_grant", error_description = "The refresh token is invalid.", message = "The refresh token is invalid." }, 401);
                        break;

                    case "client_credentials":
                        return Json.Ok(new { token_type = "Bearer", expires_in = Accounts.TOKEN_LIFETIME_SECONDS, access_token = "guest" });

                    default:
                        return Json.Ok(new { error = "unsupported_grant_type" }, 400);
                }

                var (access, refresh) = pair ?? accounts.IssueToken(userId.Value);
                req.HttpContext.Response.Headers.CacheControl = "no-store";
                if (browser)
                {
                    BrowserSession.Set(req.HttpContext.Response, access, refresh);
                    return Json.Ok(new { browser_session = true, expires_in = Accounts.TOKEN_LIFETIME_SECONDS });
                }
                return Json.Ok(new { token_type = "Bearer", expires_in = Accounts.TOKEN_LIFETIME_SECONDS, access_token = access, refresh_token = refresh });
            });

            app.MapPost("/users", async (HttpContext ctx, Accounts accounts, AccountMail mail) =>
            {
                var p = await Json.Params(ctx.Request);
                string country = ctx.Request.Headers["CF-IPCountry"].ToString() is { Length: 2 } cc && cc != "XX" ? cc.ToUpperInvariant() : "XX";
                var errors = accounts.Register(p("user[username]") ?? "", p("user[user_email]") ?? "", p("user[password]") ?? "", country);
                if (errors != null)
                    return Json.Ok(new { form_error = new { user = errors } }, 422);
                mail.Request(p("user[user_email]") ?? "", "verify");
                return Json.Ok(new { });
            });

            // xc-osu! has no second factor. Sessions are reported as verified (session_verified: true), so a client never needs
            // these; if one calls them anyway it gets an explicit "unavailable" instead of a success that verified nothing.
            IResult noSecondFactor() => Json.Ok(new
            {
                error = "Two-factor verification is not available on xc-osu!.",
                message = "Two-factor verification is not available on xc-osu!.",
                available = false,
            }, 501);
            app.MapPost("/api/v2/session/verify", noSecondFactor);
            app.MapPost("/api/v2/session/verify/reissue", noSecondFactor);
            app.MapPost("/api/v2/session/verify/mail-fallback", noSecondFactor);
            app.MapDelete("/api/v2/oauth/tokens/current", (HttpRequest req, Accounts accounts) =>
            {
                string auth = req.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer ")) accounts.Revoke(auth[7..]);
                else if (BrowserSession.SameOrigin(req) && req.Cookies[BrowserSession.AccessCookie] is string cookie) accounts.Revoke(cookie);
                else if (req.Cookies.ContainsKey(BrowserSession.AccessCookie)) return Results.StatusCode(403);
                BrowserSession.Clear(req.HttpContext.Response);
                return Results.NoContent();
            });
        }

        // ------------------------------------------------------------------ users

        private static int rulesetFrom(string? mode, HttpRequest req) =>
            !string.IsNullOrEmpty(mode) ? UsersService.RulesetId(mode)
            : req.Query["mode"].ToString() is { Length: > 0 } m ? UsersService.RulesetId(m)
            : int.TryParse(req.Query["ruleset_id"], out int r) ? r : 0;

        /// <summary>APIUser exposes rank_history / playstyle as setter-only properties, so they must be written by hand.</summary>
        private static JObject fullJson(APIUser user)
        {
            var obj = jo(user);
            if (user.Statistics?.RankHistory is APIRankHistory h)
                obj["rank_history"] = obj["rankHistory"] = new JObject { ["mode"] = h.Mode, ["data"] = new JArray(h.Data ?? Array.Empty<int>()) };
            obj["playstyle"] = new JArray((user.PlayStyles ?? Array.Empty<APIPlayStyle>()).Select(p => p.ToString().ToLowerInvariant()));
            return obj;
        }

        private static void mapUsers(RouteGroupBuilder api)
        {
            IResult me(HttpContext ctx, UsersService users, Accounts accounts, string? mode)
            {
                if (uid(ctx) is not int id) return unauthorized();
                var row = accounts.Get(id);
                if (row == null || row.Restricted) return unauthorized();
                int ruleset = string.IsNullOrEmpty(mode) ? UsersService.RulesetId(row.Playmode) : UsersService.RulesetId(mode);
                var obj = fullJson(users.Full(row, ruleset));
                obj["session_verified"] = true;
                obj["session_verification_method"] = null;
                obj["score_processing_notice_url"] = "";
                obj["is_restricted"] = false;
                obj["is_silenced"] = row.SilencedUntil > DateTimeOffset.UtcNow;
                return Json.Ok(obj);
            }

            api.MapGet("/me", (HttpContext ctx, UsersService users, Accounts accounts) => me(ctx, users, accounts, null));
            api.MapGet("/me/{mode}", (HttpContext ctx, UsersService users, Accounts accounts, string mode) => mode == "beatmapset-favourites" ? Results.NotFound() : me(ctx, users, accounts, mode));

            api.MapPut("/me/options", async (HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var body = await Json.Body<JObject>(ctx.Request);
                if (body?["user"] is JObject settings && accounts.Get(u) is AccountRow row)
                {
                    var profile = JObject.Parse(row.ProfileJson);
                    if (settings["pm_friends_only"] is JValue v) profile["pm_friends_only"] = v.Value<bool>();
                    using var c = db.Open();
                    c.Exec("UPDATE users SET profile_json = @p WHERE id = @u", ("@p", profile.ToString(Formatting.None)), ("@u", u));
                }

                return me(ctx, users, accounts, null);
            });

            api.MapGet("/users/{lookup}/{mode?}", (string lookup, string? mode, HttpRequest req, UsersService users, Accounts accounts) =>
            {
                if (lookup == "lookup") return lookupMany(req, users, accounts);
                string key = req.Query["key"].ToString();
                if (lookup.StartsWith('@')) { key = "username"; lookup = lookup[1..]; }
                AccountRow? row = key != "username" && int.TryParse(lookup, out int id) ? accounts.Get(id) : accounts.GetByName(lookup);
                if (row == null || row.Restricted) return Json.Error("User not found", 404);
                return Json.Ok(fullJson(users.Full(row, string.IsNullOrEmpty(mode) ? UsersService.RulesetId(row.Playmode) : UsersService.RulesetId(mode))));
            });

            api.MapGet("/users", (HttpRequest req, UsersService users, Accounts accounts) => lookupMany(req, users, accounts));

            static IResult lookupMany(HttpRequest req, UsersService users, Accounts accounts)
            {
                var ids = req.Query["ids[]"].Select(s => int.TryParse(s, out int i) ? i : -1).Where(i => i > 0).Take(50);
                int ruleset = int.TryParse(req.Query["ruleset_id"], out int r) ? r : 0;
                return Json.Ok(new
                {
                    users = accounts.GetMany(ids).Where(a => !a.Restricted).Select(a =>
                    {
                        var u = users.Compact(a);
                        u.Statistics = users.Statistics(a.Id, ruleset);
                        return u;
                    }),
                });
            }

            api.MapGet("/users/{id:int}/scores/{type}", (int id, string type, HttpRequest req, Db db, Accounts accounts, UsersService users) =>
            {
                int limit = Math.Clamp(int.TryParse(req.Query["limit"], out int l) ? l : 5, 1, 100);
                int offset = Math.Max(0, int.TryParse(req.Query["offset"], out int o) ? o : 0);
                int ruleset = rulesetFrom(null, req);
                bool includeFails = req.Query["include_fails"] == "1";
                using var c = db.Open();
                List<long> ids = type switch
                {
                    "best" => users.Bests(c, id, ruleset).Skip(offset).Take(limit).Select(b => b.ScoreId).ToList(),
                    "recent" => c.Query($"""
                        SELECT id FROM scores WHERE user_id = @u AND ruleset_id = @r AND ended_at > @t {(includeFails ? "" : "AND passed = 1")}
                        ORDER BY id DESC LIMIT @l OFFSET @o
                        """, r => r.GetInt64(0), ("@u", id), ("@r", ruleset), ("@t", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")), ("@l", limit), ("@o", offset)),
                    "firsts" => UsersService.FirstPlaceBeatmaps(c, id, ruleset).Skip(offset).Take(limit).ToList(),
                    "pinned" => c.Query("""
                        SELECT p.score_id FROM pinned_scores p JOIN scores s ON s.id = p.score_id WHERE p.user_id = @u AND s.ruleset_id = @r
                        ORDER BY p.sort, p.score_id DESC LIMIT @l OFFSET @o
                        """, r => r.GetInt64(0), ("@u", id), ("@r", ruleset), ("@l", limit), ("@o", offset)),
                    _ => new List<long>(),
                };

                var scores = Scores.Load(c, ids, accounts, users, withBeatmap: true);
                if (type == "best")
                {
                    // weight shown on the profile: 95% decay per position
                    var arr = new JArray();
                    for (int i = 0; i < scores.Count; i++)
                    {
                        var j = jo(scores[i]);
                        double w = Math.Pow(0.95, offset + i);
                        j["weight"] = new JObject { ["percentage"] = w * 100, ["pp"] = (scores[i].PP ?? 0) * w };
                        arr.Add(j);
                    }

                    return Json.Ok(arr);
                }

                return Json.Ok(scores);
            });

            api.MapGet("/users/{id:int}/beatmapsets/{type}", async (int id, string type, HttpRequest req, Db db, Mirrors mirrors, CancellationToken ct) =>
            {
                int limit = Math.Clamp(int.TryParse(req.Query["limit"], out int l) ? l : 5, 1, 100);
                int offset = Math.Max(0, int.TryParse(req.Query["offset"], out int o) ? o : 0);
                using var c = db.Open();

                switch (type)
                {
                    case "most_played":
                    {
                        var rows = c.Query("SELECT beatmap_id, count FROM beatmap_playcounts WHERE user_id = @u ORDER BY count DESC, beatmap_id LIMIT @l OFFSET @o",
                            r => (beatmap: r.GetInt32(0), count: r.GetInt32(1)), ("@u", id), ("@l", limit), ("@o", offset));
                        var list = new JArray();
                        foreach (var (beatmap, count) in rows)
                        {
                            var map = await mirrors.GetBeatmap(beatmap, null, null, ct);
                            if (map == null) continue;
                            var set = map["beatmapset"] as JObject;
                            var b = (JObject)map.DeepClone();
                            b.Remove("beatmapset");
                            list.Add(new JObject { ["beatmap_id"] = beatmap, ["count"] = count, ["beatmap"] = b, ["beatmapset"] = set });
                        }

                        return Json.Ok(list);
                    }

                    case "favourite":
                    {
                        var ids = c.Query("SELECT beatmapset_id FROM favourites WHERE user_id = @u ORDER BY seq DESC LIMIT @l OFFSET @o",
                            r => r.GetInt32(0), ("@u", id), ("@l", limit), ("@o", offset));
                        var sets = await Task.WhenAll(ids.Select(s => mirrors.GetBeatmapset(s, ct)));
                        return Json.Ok(sets.Where(s => s != null));
                    }

                    default:
                        // ranked / loved / pending / graveyard / guest / nominated: xc-osu! has no beatmap submission (maps come from the mirrors).
                        return Json.Ok(Array.Empty<object>());
                }
            });

            api.MapGet("/users/{id:int}/kudosu", () => Json.Ok(Array.Empty<object>()));

            api.MapGet("/users/{id:int}/recent_activity", (int id, HttpRequest req, Db db) =>
            {
                int limit = Math.Clamp(int.TryParse(req.Query["limit"], out int l) ? l : 5, 1, 100);
                int offset = Math.Max(0, int.TryParse(req.Query["offset"], out int o) ? o : 0);
                using var c = db.Open();
                var events = c.Query("SELECT id, type, json, created_at FROM user_events WHERE user_id = @u ORDER BY id DESC LIMIT @l OFFSET @o", r =>
                {
                    var j = JObject.Parse(r.GetString(2));
                    j["id"] = r.GetInt64(0);
                    j["type"] = r.GetString(1);
                    j["createdAt"] = r.GetString(3);
                    return j;
                }, ("@u", id), ("@l", limit), ("@o", offset));
                return Json.Ok(events);
            });

            // score pins (profile "pinned scores")
            api.MapPut("/score-pins/{scoreId:long}", (long scoreId, HttpContext ctx, Db db) => pin(ctx, db, scoreId, true));
            api.MapPost("/score-pins/{scoreId:long}", (long scoreId, HttpContext ctx, Db db) => pin(ctx, db, scoreId, true));
            api.MapDelete("/score-pins/{scoreId:long}", (long scoreId, HttpContext ctx, Db db) => pin(ctx, db, scoreId, false));

            static IResult pin(HttpContext ctx, Db db, long scoreId, bool on)
            {
                if (uid(ctx) is not int u) return unauthorized();
                using var c = db.Open();
                if (c.Scalar<long>("SELECT COUNT(*) FROM scores WHERE id = @i AND user_id = @u AND passed = 1", ("@i", scoreId), ("@u", u)) == 0)
                    return Json.Error("Score not found", 404);
                if (on)
                    c.Exec("INSERT INTO pinned_scores (user_id, score_id, sort) VALUES (@u, @s, (SELECT COALESCE(MAX(sort), 0) + 1 FROM pinned_scores WHERE user_id = @u)) ON CONFLICT DO NOTHING",
                        ("@u", u), ("@s", scoreId));
                else
                    c.Exec("DELETE FROM pinned_scores WHERE user_id = @u AND score_id = @s", ("@u", u), ("@s", scoreId));
                return Results.NoContent();
            }
        }

        // ------------------------------------------------------------------ beatmaps (mirrors + xc-osu! data)

        /// <summary>Replaces the mirrors' (official) community numbers with xc-osu!'s own: play/pass counts, favourites, user tags.</summary>
        private static JObject decorateBeatmap(JObject map, Npgsql.NpgsqlConnection c, int? viewer)
        {
            map = (JObject)map.DeepClone();
            int id = map.Value<int>("id");
            map["playcount"] = c.Scalar<long>("SELECT COUNT(*) FROM scores WHERE beatmap_id = @b", ("@b", id));
            map["passcount"] = c.Scalar<long>("SELECT COUNT(*) FROM scores WHERE beatmap_id = @b AND passed = 1", ("@b", id));
            map["top_tag_ids"] = new JArray(c.Query("SELECT tag_id, COUNT(*) FROM beatmap_tags WHERE beatmap_id = @b GROUP BY tag_id ORDER BY 2 DESC LIMIT 10",
                r => new JObject { ["tag_id"] = r.GetInt64(0), ["count"] = r.GetInt32(1) }, ("@b", id)));
            map["current_user_tag_ids"] = viewer == null
                ? new JArray()
                : new JArray(c.Query("SELECT tag_id FROM beatmap_tags WHERE beatmap_id = @b AND user_id = @u", r => r.GetInt64(0), ("@b", id), ("@u", viewer)));
            if (map["beatmapset"] is JObject set) map["beatmapset"] = decorateSet(set, c, viewer, nested: true);
            return map;
        }

        /// <summary>
        /// Listing variant of <see cref="decorateSet"/>: same xc-osu! numbers, but batched queries and none of the per-set
        /// extras (related tags, user tags) the cards never show — they made a 50-set page weigh ~1 MB.
        /// </summary>
        private static IEnumerable<JObject> decorateListing(List<JObject> sets, Npgsql.NpgsqlConnection c, int? viewer)
        {
            if (sets.Count == 0) return Array.Empty<JObject>();

            string setIds = string.Join(',', sets.Select(s => s.Value<int>("id")));
            var favCounts = c.Query($"SELECT beatmapset_id, COUNT(*) FROM favourites WHERE beatmapset_id IN ({setIds}) GROUP BY beatmapset_id",
                r => (r.GetInt32(0), r.GetInt64(1))).ToDictionary(r => r.Item1, r => r.Item2);
            var mine = viewer == null
                ? new HashSet<int>()
                : c.Query($"SELECT beatmapset_id FROM favourites WHERE user_id = @u AND beatmapset_id IN ({setIds})", r => r.GetInt32(0), ("@u", viewer)).ToHashSet();

            var mapIds = sets.SelectMany(s => (s["beatmaps"] as JArray)?.Select(m => m.Value<int>("id")) ?? Enumerable.Empty<int>()).ToList();
            var plays = mapIds.Count == 0
                ? new Dictionary<int, (long, long)>()
                : c.Query($"SELECT beatmap_id, COUNT(*), SUM(passed) FROM scores WHERE beatmap_id IN ({string.Join(',', mapIds)}) GROUP BY beatmap_id",
                    r => (r.GetInt32(0), r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetInt64(2))).ToDictionary(r => r.Item1, r => (r.Item2, r.Item3));

            return sets.Select(src =>
            {
                var set = (JObject)src.DeepClone();
                int id = set.Value<int>("id");
                set["favourite_count"] = favCounts.GetValueOrDefault(id);
                set["has_favourited"] = mine.Contains(id);

                long total = 0;
                if (set["beatmaps"] is JArray maps)
                {
                    foreach (var m in maps.OfType<JObject>())
                    {
                        var (pc, ps) = plays.GetValueOrDefault(m.Value<int>("id"));
                        m["playcount"] = pc;
                        m["passcount"] = ps;
                        total += pc;
                    }
                }

                set["play_count"] = total;
                set.Remove("converts");
                return set;
            });
        }

        private static JObject decorateSet(JObject set, Npgsql.NpgsqlConnection c, int? viewer, bool nested = false)
        {
            set = (JObject)set.DeepClone();
            int id = set.Value<int>("id");
            set["favourite_count"] = c.Scalar<long>("SELECT COUNT(*) FROM favourites WHERE beatmapset_id = @s", ("@s", id));
            set["has_favourited"] = viewer != null && c.Scalar<long>("SELECT COUNT(*) FROM favourites WHERE beatmapset_id = @s AND user_id = @u", ("@s", id), ("@u", viewer)) > 0;
            set["related_tags"] = JArray.FromObject(Tags.ALL, serializer);

            if (!nested && set["beatmaps"] is JArray maps)
            {
                var ids = maps.Select(m => m.Value<int>("id")).ToList();
                var plays = ids.Count == 0
                    ? new Dictionary<int, (long, long)>()
                    : c.Query($"SELECT beatmap_id, COUNT(*), SUM(passed) FROM scores WHERE beatmap_id IN ({string.Join(',', ids)}) GROUP BY beatmap_id",
                        r => (r.GetInt32(0), r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetInt64(2))).ToDictionary(r => r.Item1, r => (r.Item2, r.Item3));
                long total = 0;
                foreach (var m in maps.OfType<JObject>())
                {
                    var (pc, ps) = plays.GetValueOrDefault(m.Value<int>("id"));
                    m["playcount"] = pc;
                    m["passcount"] = ps;
                    total += pc;
                    m["top_tag_ids"] = new JArray(c.Query("SELECT tag_id, COUNT(*) FROM beatmap_tags WHERE beatmap_id = @b GROUP BY tag_id ORDER BY 2 DESC LIMIT 10",
                        r => new JObject { ["tag_id"] = r.GetInt64(0), ["count"] = r.GetInt32(1) }, ("@b", m.Value<int>("id"))));
                    m["current_user_tag_ids"] = viewer == null
                        ? new JArray()
                        : new JArray(c.Query("SELECT tag_id FROM beatmap_tags WHERE beatmap_id = @b AND user_id = @u", r => r.GetInt64(0), ("@b", m.Value<int>("id")), ("@u", viewer)));
                }

                set["play_count"] = total;
            }

            return set;
        }

        private static void mapBeatmaps(RouteGroupBuilder api)
        {
            api.MapGet("/beatmapsets/search", async (HttpContext ctx, BeatmapSearch search, Db db, CancellationToken ct) =>
            {
                var req = ctx.Request;
                int? me = uid(ctx);
                string cacheKey = me + "|" + req.QueryString.Value;
                lock (searchResponsesLock)
                    if (searchResponses.TryGetValue(cacheKey, out var cached) && cached.at > DateTime.UtcNow - searchResponseTtl)
                        return Results.Content(cached.json, "application/json", System.Text.Encoding.UTF8);

                var result = await search.Search(req.Query, me, ct);

                string json;
                using (var c = db.Open())
                    json = JsonConvert.SerializeObject(new JObject
                    {
                        ["beatmapsets"] = new JArray(decorateListing(result.Sets, c, me)),
                        ["total"] = result.Total,
                        ["cursor"] = result.Cursor,
                        ["cursor_string"] = result.Cursor?.Value<int>("page").ToString(),
                        ["search"] = new JObject { ["sort"] = req.Query["sort"].ToString() is { Length: > 0 } sort ? sort : "ranked_desc" },
                    }, Json.SETTINGS);

                lock (searchResponsesLock)
                {
                    if (searchResponses.Count >= 256) searchResponses.Clear();
                    searchResponses[cacheKey] = (DateTime.UtcNow, json);
                }
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8);
            });

            api.MapGet("/beatmapsets/lookup", async (int beatmap_id, HttpContext ctx, Mirrors mirrors, Db db, CancellationToken ct) =>
            {
                var map = await mirrors.GetBeatmap(beatmap_id, null, null, ct);
                int? setId = map?.Value<int?>("beatmapset_id");
                var set = setId == null ? null : await mirrors.GetBeatmapset(setId.Value, ct);
                if (set == null) return Json.Error("Beatmap not found", 404);
                using var c = db.Open();
                return Json.Ok(decorateSet(set, c, uid(ctx)));
            });

            api.MapGet("/beatmapsets/{id:int}", async (int id, HttpContext ctx, Mirrors mirrors, Db db, CancellationToken ct) =>
            {
                if (await mirrors.GetBeatmapset(id, ct) is not JObject set) return Json.Error("Beatmapset not found", 404);
                using var c = db.Open();
                return Json.Ok(decorateSet(set, c, uid(ctx)));
            });

            // like osu!: "{id} {artist} - {title}.osz", with an ASCII fallback for old clients and filename* for the real name
            static async Task<string> oszDisposition(int id, Mirrors mirrors, CancellationToken ct)
            {
                string name = id.ToString();
                try
                {
                    if (await mirrors.GetBeatmapset(id, ct) is JObject set && set.Value<string>("artist") is { Length: > 0 } artist && set.Value<string>("title") is { Length: > 0 } title)
                        name = $"{id} {artist} - {title}";
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                var invalid = new HashSet<char>("\\/:*?\"<>|");
                string clean = new string(name.Select(ch => char.IsControl(ch) || invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
                if (clean.Length > 200) clean = clean[..200].TrimEnd();
                string ascii = new string(clean.Select(ch => ch is >= ' ' and <= '~' && ch != '%' ? ch : '_').ToArray());
                return $"attachment; filename=\"{ascii}.osz\"; filename*=UTF-8''{Uri.EscapeDataString(clean + ".osz")}";
            }

            api.MapGet("/beatmapsets/{id:int}/download", async (int id, HttpContext ctx, Mirrors mirrors, CancellationToken ct) =>
            {
                bool noVideo = ctx.Request.Query["noVideo"] == "1";
                using var res = await mirrors.Download(id, noVideo, ct);
                if (res == null) return Json.Error("No mirror has this beatmapset right now.", 404);

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-osu-beatmap-archive";
                if (res.Content.Headers.ContentLength is long len) ctx.Response.ContentLength = len;
                ctx.Response.Headers.ContentDisposition = await oszDisposition(id, mirrors, ct);
                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                await stream.CopyToAsync(ctx.Response.Body, 1 << 16, ct);
                return Results.Empty;
            });

            api.MapPost("/beatmapsets/{id:int}/favourites", async (int id, HttpContext ctx, Db db) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var p = await Json.Params(ctx.Request);
                using var c = db.Open();
                if (p("action") == "favourite")
                    c.Exec("INSERT INTO favourites (user_id, beatmapset_id) VALUES (@u, @s) ON CONFLICT DO NOTHING", ("@u", u), ("@s", id));
                else
                    c.Exec("DELETE FROM favourites WHERE user_id = @u AND beatmapset_id = @s", ("@u", u), ("@s", id));
                return Json.Ok(new { favourite_count = c.Scalar<long>("SELECT COUNT(*) FROM favourites WHERE beatmapset_id = @s", ("@s", id)) });
            });

            api.MapGet("/me/beatmapset-favourites", (HttpContext ctx, Db db) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                using var c = db.Open();
                return Json.Ok(new { beatmapset_ids = c.Query("SELECT beatmapset_id FROM favourites WHERE user_id = @u", r => r.GetInt32(0), ("@u", u)) });
            });

            api.MapGet("/beatmaps/lookup", async (HttpContext ctx, Mirrors mirrors, Db db, CancellationToken ct) =>
            {
                var req = ctx.Request;
                int? id = int.TryParse(req.Query["id"], out int i) ? i : null;
                string? checksum = req.Query["checksum"].ToString() is { Length: > 0 } cs ? cs : null;
                string? filename = req.Query["filename"].ToString() is { Length: > 0 } fn ? fn : null;
                if (id == null && checksum == null && filename == null) return Json.Error("Beatmap not found", 404);
                var map = await mirrors.GetBeatmap(id, checksum, filename, ct);
                if (map == null) return Json.Error("Beatmap not found", 404);
                using var c = db.Open();
                return Json.Ok(decorateBeatmap(map, c, uid(ctx)));
            });

            api.MapGet("/beatmaps", async (HttpContext ctx, Mirrors mirrors, Db db, CancellationToken ct) =>
            {
                var ids = ctx.Request.Query["ids[]"].Select(s => int.TryParse(s, out int i) ? i : -1).Where(i => i > 0).Distinct().Take(50);
                var maps = await Task.WhenAll(ids.Select(i => mirrors.GetBeatmap(i, null, null, ct)));
                using var c = db.Open();
                return Json.Ok(new { beatmaps = maps.OfType<JObject>().Select(m => decorateBeatmap(m, c, uid(ctx))) });
            });

            api.MapGet("/beatmaps/{id:int}", async (int id, HttpContext ctx, Mirrors mirrors, Db db, CancellationToken ct) =>
            {
                if (await mirrors.GetBeatmap(id, null, null, ct) is not JObject m) return Json.Error("Beatmap not found", 404);
                using var c = db.Open();
                return Json.Ok(decorateBeatmap(m, c, uid(ctx)));
            });

            api.MapPost("/beatmaps/{id:int}/attributes", async (int id, HttpRequest req, Performance perf, CancellationToken ct) =>
            {
                var p = await Json.Params(req);
                int ruleset = int.TryParse(p("ruleset_id"), out int r) ? r : UsersService.RulesetId(p("ruleset"));
                var mods = new List<APIMod>();
                var body = req.ContentType?.Contains("json") == true ? await Json.Body<JObject>(req) : null;
                if (body?["mods"] is JArray arr)
                    mods.AddRange(arr.Select(m => m.Type == JTokenType.String ? new APIMod { Acronym = m.Value<string>()! } : m.ToObject<APIMod>()!));
                if (mods.Count > 32) return Json.Error("Too many mods", 422);
                try
                {
                    var d = await perf.CalculateDifficulty(id, ruleset, mods, ct);
                    return Json.Ok(new { attributes = new { star_rating = d?.starRating ?? 0, max_combo = d?.maxCombo ?? 0 } });
                }
                catch (Scores.SubmitException e) { return Json.Error(e.Message, e.Status); }
            });

            // user tags
            api.MapGet("/tags", () => Json.Ok(new { tags = Tags.ALL }));
            api.MapGet("/beatmaps/{id:int}/tags", (int id, Db db) =>
            {
                using var c = db.Open();
                return Json.Ok(c.Query("SELECT tag_id, COUNT(*) FROM beatmap_tags WHERE beatmap_id = @b GROUP BY tag_id", r => new { tag_id = r.GetInt64(0), count = r.GetInt32(1) }, ("@b", id)));
            });
            api.MapPut("/beatmaps/{id:int}/tags/{tag:long}", (int id, long tag, HttpContext ctx, Db db) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                if (Tags.ALL.All(t => t.Id != tag)) return Json.Error("Unknown tag", 404);
                using var c = db.Open();
                if (c.Scalar<long>("SELECT COUNT(*) FROM scores WHERE user_id = @u AND beatmap_id = @b AND passed = 1", ("@u", u), ("@b", id)) == 0)
                    return Json.Error("You must pass the beatmap before tagging it.", 403);
                c.Exec("INSERT INTO beatmap_tags (beatmap_id, tag_id, user_id) VALUES (@b, @t, @u) ON CONFLICT DO NOTHING", ("@b", id), ("@t", tag), ("@u", u));
                return Results.NoContent();
            });
            api.MapDelete("/beatmaps/{id:int}/tags/{tag:long}", (int id, long tag, HttpContext ctx, Db db) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                using var c = db.Open();
                c.Exec("DELETE FROM beatmap_tags WHERE beatmap_id = @b AND tag_id = @t AND user_id = @u", ("@b", id), ("@t", tag), ("@u", u));
                return Results.NoContent();
            });
        }

        // ------------------------------------------------------------------ solo scores

        private static void mapScores(RouteGroupBuilder api)
        {
            api.MapPost("/beatmaps/{id:int}/solo/scores", async (int id, HttpContext ctx, Scores scores) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var p = await Json.Params(ctx.Request);
                try
                {
                    long token = scores.CreateToken(u, id, int.TryParse(p("ruleset_id"), out int r) ? r : 0, p("beatmap_hash"), null, null);
                    return Json.Ok(new { id = token, created_at = DateTimeOffset.UtcNow });
                }
                catch (Scores.SubmitException e)
                {
                    return Json.Error(e.Message, e.Status);
                }
            });

            api.MapPut("/beatmaps/{id:int}/solo/scores/{token:long}", async (int id, long token, HttpContext ctx, Scores scores, CancellationToken ct) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var score = await Json.Body<SoloScoreInfo>(ctx.Request);
                if (score == null) return Json.Error("invalid score");
                try
                {
                    var result = await scores.Submit(u, id, token, null, score, ct);
                    return result == null ? Json.Error("invalid score token", 404) : Json.Ok(result);
                }
                catch (Scores.SubmitException e)
                {
                    return Json.Error(e.Message, e.Status);
                }
            });

            api.MapGet("/beatmaps/{id:int}/scores", (int id, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                var q = ctx.Request.Query;
                int ruleset = UsersService.RulesetId(q["mode"].ToString());
                int limit = Math.Clamp(int.TryParse(q["limit"], out int l) ? l : 50, 1, 100);
                string type = q["type"].ToString();
                var mods = q["mods[]"].Where(m => !string.IsNullOrEmpty(m)).Select(m => m!).ToHashSet();
                int? me = uid(ctx);

                var all = beatmapLeaderboard(db, accounts, users, id, ruleset, type, mods, me);
                APIScoreWithPosition? userScore = null;
                int myIndex = me == null ? -1 : all.FindIndex(s => s.UserID == me);
                if (myIndex >= 0)
                    userScore = new APIScoreWithPosition { Score = all[myIndex], Position = myIndex + 1 };

                return Json.Ok(new APIScoresCollection { Scores = all.Take(limit).ToList(), UserScore = userScore, ScoresCount = all.Count });
            });

            api.MapGet("/beatmaps/{id:int}/scores/users/{user:int}", (int id, int user, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                int ruleset = UsersService.RulesetId(ctx.Request.Query["mode"].ToString());
                var mods = ctx.Request.Query["mods[]"].Where(m => !string.IsNullOrEmpty(m)).Select(m => m!).ToHashSet();
                var all = beatmapLeaderboard(db, accounts, users, id, ruleset, "global", mods, uid(ctx));
                int idx = all.FindIndex(s => s.UserID == user);
                return idx < 0 ? Json.Error("Score not found", 404) : Json.Ok(new APIScoreWithPosition { Score = all[idx], Position = idx + 1 });
            });

            api.MapGet("/beatmaps/{id:int}/scores/users/{user:int}/all", (int id, int user, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                int ruleset = UsersService.RulesetId(ctx.Request.Query["mode"].ToString());
                using var c = db.Open();
                var ids = c.Query("SELECT id FROM scores WHERE beatmap_id = @b AND user_id = @u AND ruleset_id = @r AND passed = 1 AND playlist_item_id IS NULL ORDER BY total_score DESC",
                    r => r.GetInt64(0), ("@b", id), ("@u", user), ("@r", ruleset));
                return Json.Ok(new { scores = Scores.Load(c, ids, accounts, users, withBeatmap: false) });
            });

            IResult showScore(long id, Db db, Accounts accounts, UsersService users)
            {
                using var c = db.Open();
                return Scores.Load(c, new[] { id }, accounts, users, withBeatmap: true).FirstOrDefault() is SoloScoreInfo s ? Json.Ok(s) : Json.Error("Score not found", 404);
            }

            api.MapGet("/scores/{id:long}", (long id, Db db, Accounts accounts, UsersService users) => showScore(id, db, accounts, users));
            api.MapGet("/scores/{mode}/{id:long}", (string mode, long id, Db db, Accounts accounts, UsersService users) => showScore(id, db, accounts, users));

            IResult download(long id, HttpContext ctx, Scores scores)
            {
                if (scores.ReplayPath(id) is not string path) return Json.Error("Replay not available", 404);
                scores.CountReplayWatch(id, uid(ctx));
                return Results.File(path, "application/x-osu-replay", $"xc-osu-replay-{id}.osr");
            }

            api.MapGet("/scores/{id:long}/download", (long id, HttpContext ctx, Scores scores) => download(id, ctx, scores));
            api.MapGet("/scores/{mode}/{id:long}/download", (string mode, long id, HttpContext ctx, Scores scores) => download(id, ctx, scores));
        }

        /// <summary>Best score per user on a beatmap (ranked/loved leaderboards only, like osu!), highest first.</summary>
        private static List<SoloScoreInfo> beatmapLeaderboard(Db db, Accounts accounts, UsersService users, int beatmapId, int ruleset, string type, HashSet<string> mods, int? me)
        {
            using var c = db.Open();
            string filter = "";
            switch (type)
            {
                case "friend" when me != null:
                    filter = $" AND (s.user_id = {me} OR s.user_id IN (SELECT target_id FROM friends WHERE user_id = {me}))";
                    break;

                case "country" when me != null:
                    filter = $" AND u.country = (SELECT country FROM users WHERE id = {me})";
                    break;

                case "team" when me != null:
                    filter = $" AND s.user_id IN (SELECT user_id FROM team_members WHERE team_id = (SELECT team_id FROM team_members WHERE user_id = {me}))";
                    break;

                case "friend" or "country" or "team":
                    return new List<SoloScoreInfo>();
            }

            var rows = c.Query($"""
                SELECT s.id, s.user_id, s.mods FROM scores s JOIN users u ON u.id = s.user_id
                WHERE s.beatmap_id = @b AND s.ruleset_id = @r AND s.passed = 1 AND s.ranked = 1 AND s.playlist_item_id IS NULL AND u.restricted = 0 {filter}
                ORDER BY s.total_score DESC, s.id ASC
                """, r => (id: r.GetInt64(0), user: r.GetInt32(1), mods: r.GetString(2)), ("@b", beatmapId), ("@r", ruleset));

            var seen = new HashSet<int>();
            var best = new List<long>();
            foreach (var row in rows)
            {
                if (mods.Count > 0)
                {
                    var acronyms = JArray.Parse(row.mods).Select(m => m.Value<string>("acronym")).Where(a => a != null && a != "CL").ToHashSet();
                    if (mods.Contains("NM") ? acronyms.Count > 0 : !mods.SetEquals(acronyms!)) continue;
                }

                if (seen.Add(row.user)) best.Add(row.id);
            }

            return Scores.Load(c, best, accounts, users, withBeatmap: false);
        }

        // ------------------------------------------------------------------ rankings

        private static void mapRankings(RouteGroupBuilder api)
        {
            api.MapGet("/rankings/kudosu", () => Json.Ok(new { ranking = Array.Empty<object>() }));

            api.MapGet("/rankings/{mode}/{type}", (string mode, string type, HttpRequest req, UsersService users, Accounts accounts, Db db) =>
            {
                int ruleset = UsersService.RulesetId(mode);
                int page = Math.Max(1, int.TryParse(req.Query["cursor[page]"], out int p) ? p : int.TryParse(req.Query["page"], out p) ? p : 1);
                string? country = req.Query["country"].ToString() is { Length: 2 } cc && cc != "XX" && cc != "Unknown" ? cc.ToUpperInvariant() : null;

                switch (type)
                {
                    case "performance":
                    case "score":
                    {
                        var countryOf = accounts.GetMany(users.Leaderboard(ruleset).Select(e => e.userId)).ToDictionary(a => a.Id, a => a.Country);
                        IEnumerable<int> ids;
                        int total;
                        if (type == "performance")
                        {
                            var board = users.Leaderboard(ruleset).Where(e => country == null || countryOf.GetValueOrDefault(e.userId) == country).OrderByDescending(e => e.pp).ToList();
                            total = board.Count;
                            ids = board.Skip((page - 1) * 50).Take(50).Select(e => e.userId);
                        }
                        else
                        {
                            using var c = db.Open();
                            var board = c.Query($"""
                                SELECT s.user_id, SUM(best) FROM (
                                    SELECT user_id, MAX(total_score) AS best FROM scores WHERE ruleset_id = @r AND passed = 1 AND ranked = 1 AND playlist_item_id IS NULL GROUP BY user_id, beatmap_id) s
                                JOIN users u ON u.id = s.user_id WHERE u.restricted = 0 {(country != null ? "AND u.country = @c" : "")}
                                GROUP BY s.user_id ORDER BY 2 DESC
                                """, r => r.GetInt32(0), ("@r", ruleset), ("@c", country));
                            total = board.Count;
                            ids = board.Skip((page - 1) * 50).Take(50);
                        }

                        var rows = accounts.GetMany(ids).ToDictionary(r => r.Id);
                        var ranking = new List<UserStatistics>();
                        foreach (int id in ids)
                        {
                            if (!rows.TryGetValue(id, out var row)) continue;
                            var stats = users.Statistics(id, ruleset);
                            stats.User = users.Compact(row);
                            ranking.Add(stats);
                        }

                        return Json.Ok(new { ranking, total, cursor = total > page * 50 ? new { page = page + 1 } : null });
                    }

                    case "country":
                    {
                        var board = users.Leaderboard(ruleset);
                        var byCountry = accounts.GetMany(board.Select(e => e.userId)).ToDictionary(a => a.Id, a => a.Country);
                        using var c = db.Open();
                        var playcounts = c.Query("SELECT u.country, COUNT(*), COALESCE(SUM(CASE WHEN s.ranked = 1 AND s.passed = 1 THEN s.total_score END), 0) FROM scores s JOIN users u ON u.id = s.user_id WHERE s.ruleset_id = @r GROUP BY u.country",
                            r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("@r", ruleset)).ToDictionary(r => r.Item1, r => (plays: r.Item2, score: r.Item3));
                        var countries = board.GroupBy(e => byCountry.GetValueOrDefault(e.userId, "XX"))
                                             .Select(g => new CountryStatistics
                                             {
                                                 Code = Enum.TryParse(g.Key, out CountryCode code) ? code : CountryCode.Unknown,
                                                 ActiveUsers = g.Count(),
                                                 PlayCount = playcounts.GetValueOrDefault(g.Key).plays,
                                                 RankedScore = playcounts.GetValueOrDefault(g.Key).score,
                                                 Performance = (long)g.Sum(e => e.pp),
                                             })
                                             .OrderByDescending(x => x.Performance).ToList();
                        return Json.Ok(new { ranking = countries.Skip((page - 1) * 50).Take(50), total = countries.Count, cursor = countries.Count > page * 50 ? new { page = page + 1 } : null });
                    }

                    case "charts":
                        return Json.Ok(new { ranking = Array.Empty<object>(), spotlight = (object?)null, beatmapsets = Array.Empty<object>() });

                    case "team":
                    {
                        using var c = db.Open();
                        var teams = c.Query("SELECT t.id, t.name, t.short_name, t.flag_url, COUNT(m.user_id) FROM teams t LEFT JOIN team_members m ON m.team_id = t.id GROUP BY t.id",
                            r => (id: r.GetInt32(0), name: r.GetString(1), shortName: r.GetString(2), flag: r.IsDBNull(3) ? null : r.GetString(3), members: r.GetInt32(4)));
                        var board = users.Leaderboard(ruleset).ToDictionary(e => e.userId, e => e.pp);
                        var ranking = teams.Select(t =>
                        {
                            var members = c.Query("SELECT user_id FROM team_members WHERE team_id = @t", r => r.GetInt32(0), ("@t", t.id));
                            return new
                            {
                                team = new { t.id, t.name, short_name = t.shortName, flag_url = t.flag },
                                members_count = t.members,
                                performance = (long)members.Sum(m => board.GetValueOrDefault(m)),
                                play_count = c.Scalar<long>($"SELECT COUNT(*) FROM scores WHERE ruleset_id = {ruleset} AND user_id IN (SELECT user_id FROM team_members WHERE team_id = {t.id})"),
                                ranked_score = 0L,
                                ruleset_id = ruleset,
                            };
                        }).OrderByDescending(t => t.performance).ToList();
                        return Json.Ok(new { ranking, total = ranking.Count, cursor = (object?)null });
                    }

                    default:
                        return Json.Error("Unknown ranking type", 404);
                }
            });

            api.MapGet("/spotlights", () => Json.Ok(new { spotlights = Array.Empty<object>() }));
        }

        // ------------------------------------------------------------------ chat

        private static void mapChat(RouteGroupBuilder api)
        {
            api.MapGet("/chat/channels", (HttpContext ctx, Chat chat) => uid(ctx) is int u ? Json.Ok(Chat.ToJson(chat.PublicChannels(u))) : unauthorized());

            // CreateChannelRequest: opening a PM conversation with someone.
            api.MapPost("/chat/channels", async (HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var p = await Json.Params(ctx.Request);
                if (!string.Equals(p("type"), "PM", StringComparison.OrdinalIgnoreCase) || !int.TryParse(p("target_id"), out int target))
                    return Json.Error("Only private message channels can be created.", 422);
                if (chat.PmRestriction(u, target) is string error) return Json.Error(error, 403);
                int channel = chat.OpenPm(u, target);
                var ch = chat.Join(channel, u)!;
                return Json.Ok(Chat.ToJson(ch));
            });

            api.MapGet("/chat/channels/{id:int}", (int id, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                if (!chat.CanRead(id, u)) return Json.Error("not found", 404);
                var ch = chat.Get(id, u);
                return ch == null ? Json.Error("not found", 404) : Json.Ok(new { channel = Chat.ToJson(ch), users = ch.Users });
            });

            api.MapPut("/chat/channels/{id:int}/users/{user:int}", (int id, int user, HttpContext ctx, Chat chat) =>
                uid(ctx) is int u && u == user ? chat.Join(id, u) is Channel ch ? Json.Ok(Chat.ToJson(ch)) : Json.Error("not found", 404) : unauthorized());

            api.MapDelete("/chat/channels/{id:int}/users/{user:int}", (int id, int user, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u || u != user) return unauthorized();
                chat.Leave(id, u);
                return Results.NoContent();
            });

            api.MapGet("/chat/channels/{id:int}/messages", (int id, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                if (!chat.CanRead(id, u)) return Json.Error("forbidden", 403);
                int limit = Math.Clamp(int.TryParse(ctx.Request.Query["limit"], out int l) ? l : 50, 1, 50);
                long? since = long.TryParse(ctx.Request.Query["since"], out long s) ? s : null;
                return Json.Ok(Chat.ToJson(chat.Messages(id, since, limit)));
            });

            api.MapPost("/chat/channels/{id:int}/messages", async (int id, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                if (!chat.CanRead(id, u)) return Json.Error("forbidden", 403);
                if (chat.PostRestriction(id, u) is string error) return Json.Error(error, 403);
                var p = await Json.Params(ctx.Request);
                string content = p("message") ?? "";
                if (string.IsNullOrWhiteSpace(content)) return Json.Error("empty message", 422);
                return Json.Ok(Chat.ToJson(chat.Post(id, u, content, p("is_action") == "true", p("uuid"))));
            });

            api.MapPut("/chat/channels/{id:int}/mark-as-read/{message:long}", (int id, long message, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                chat.MarkRead(id, u, message);
                return Results.NoContent();
            });

            api.MapPost("/chat/new", async (HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var p = await Json.Params(ctx.Request);
                if (!int.TryParse(p("target_id"), out int target)) return Json.Error("invalid target", 422);
                if (chat.PmRestriction(u, target) is string error) return Json.Error(error, 403);
                int channel = chat.OpenPm(u, target);
                if (chat.PostRestriction(channel, u) is string error2) return Json.Error(error2, 403);
                var msg = chat.Post(channel, u, p("message") ?? "", p("is_action") == "true", p("uuid"));
                return Json.Ok(new { new_channel_id = channel, channel = Chat.ToJson(chat.Get(channel, u)!), message = Chat.ToJson(msg) });
            });

            api.MapPost("/chat/ack", (HttpContext ctx) => uid(ctx) != null ? Json.Ok(new { silences = Array.Empty<object>() }) : unauthorized());

            api.MapGet("/chat/updates", (HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                long since = long.TryParse(ctx.Request.Query["since"], out long s) ? s : 0;
                var channels = chat.JoinedChannels(u);
                var messages = channels.SelectMany(ch => chat.Messages((int)ch.Id, since)).OrderBy(m => m.Id).ToList();
                return Json.Ok(new { presence = Chat.ToJson(channels), messages = Chat.ToJson(messages), silences = Array.Empty<object>() });
            });

            api.MapGet("/notifications", (HttpContext ctx, Notifications notifications) =>
            {
                string scheme = ctx.Request.Headers["X-Forwarded-Proto"] == "https" || ctx.Request.IsHttps ? "wss" : "ws";
                string endpoint = $"{scheme}://{ctx.Request.Host}/notifications";
                if (uid(ctx) is not int u)
                    return Json.Ok(new { has_more = false, notifications = Array.Empty<object>(), notification_endpoint = endpoint });
                return Json.Ok(notifications.List(u, endpoint));
            });

            api.MapPost("/notifications/mark-read", async (HttpContext ctx, Notifications notifications) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var body = ctx.Request.ContentType?.Contains("json") == true ? await Json.Body<JObject>(ctx.Request) : null;
                var ids = (body?["identities"] as JArray ?? body?["notifications"] as JArray)?.Select(t => t.Type == JTokenType.Object ? t.Value<long?>("id") : t.Value<long?>())
                                                                                              .OfType<long>().ToList();
                if (ids == null || ids.Count == 0) notifications.MarkAllRead(u);
                else notifications.MarkRead(u, ids);
                return Results.NoContent();
            });
        }

        // ------------------------------------------------------------------ friends / blocks

        private static void mapSocial(RouteGroupBuilder api)
        {
            void relations(string table, string kind)
            {
                api.MapGet($"/{table}", (HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
                {
                    if (uid(ctx) is not int u) return unauthorized();
                    using var c = db.Open();
                    var targets = c.Query($"SELECT target_id FROM {table} WHERE user_id = @u", r => r.GetInt32(0), ("@u", u));
                    var mutual = c.Query($"SELECT user_id FROM {table} WHERE target_id = @u", r => r.GetInt32(0), ("@u", u)).ToHashSet();
                    return Json.Ok(accounts.GetMany(targets).Select(r =>
                    {
                        var t = users.Compact(r);
                        t.Statistics = users.Statistics(r.Id, 0);
                        return new { target_id = r.Id, relation_type = kind, mutual = mutual.Contains(r.Id), target = t };
                    }));
                });

                api.MapPost($"/{table}", async (HttpContext ctx, Db db, Accounts accounts, UsersService users, Notifications notifications) =>
                {
                    if (uid(ctx) is not int u) return unauthorized();
                    var p = await Json.Params(ctx.Request);
                    if (!int.TryParse(p("target"), out int t) || accounts.Get(t) is not AccountRow row || t == u) return Json.Error("user not found", 404);
                    bool added;
                    bool mutual;
                    using (var c = db.Open())
                    {
                        added = c.Exec($"INSERT INTO {table} (user_id, target_id) VALUES (@u, @t) ON CONFLICT DO NOTHING", ("@u", u), ("@t", t)) > 0;
                        // blocking someone removes the friendship both ways, like osu-web.
                        if (table == "blocks") c.Exec("DELETE FROM friends WHERE (user_id = @u AND target_id = @t)", ("@u", u), ("@t", t));
                        if (table == "friends") c.Exec("DELETE FROM blocks WHERE user_id = @u AND target_id = @t", ("@u", u), ("@t", t));
                        mutual = c.Scalar<long>($"SELECT COUNT(*) FROM {table} WHERE user_id = @t AND target_id = @u", ("@u", u), ("@t", t)) > 0;
                    }

                    var target = users.Compact(row);
                    target.Statistics = users.Statistics(t, 0);
                    var rel = new { target_id = t, relation_type = kind, mutual, target };
                    return Json.Ok(new { user_relation = rel });
                });

                api.MapDelete($"/{table}/{{target:int}}", (int target, HttpContext ctx, Db db) =>
                {
                    if (uid(ctx) is not int u) return unauthorized();
                    using var c = db.Open();
                    c.Exec($"DELETE FROM {table} WHERE user_id = @u AND target_id = @t", ("@u", u), ("@t", target));
                    return Results.NoContent();
                });
            }

            relations("friends", "friend");
            relations("blocks", "block");
        }

        // ------------------------------------------------------------------ comments (news posts, beatmapsets, changelog)

        private static void mapComments(RouteGroupBuilder api)
        {
            JObject bundle(Npgsql.NpgsqlConnection c, List<long> ids, string? type, long? commentableId, int? viewer, Accounts accounts, UsersService users,
                           bool hasMore = false, long? parentId = null)
            {
                var comments = ids.Count == 0
                    ? new List<JObject>()
                    : c.Query($"""
                        SELECT id, parent_id, user_id, message, commentable_type, commentable_id, created_at, updated_at, deleted_at, edited_at, edited_by_id, pinned,
                               (SELECT COUNT(*) FROM comments r WHERE r.parent_id = comments.id AND r.deleted_at IS NULL),
                               (SELECT COUNT(*) FROM comment_votes v WHERE v.comment_id = comments.id)
                        FROM comments WHERE id IN ({string.Join(',', ids)})
                        """, r => new JObject
                    {
                        ["id"] = r.GetInt64(0),
                        ["parent_id"] = r.IsDBNull(1) ? null : r.GetInt64(1),
                        ["user_id"] = r.GetInt32(2),
                        ["message"] = r.IsDBNull(8) ? r.GetString(3) : null,
                        ["message_html"] = r.IsDBNull(8) ? $"<div class='osu-md-default'><p class=\"osu-md-default__paragraph\">{System.Net.WebUtility.HtmlEncode(r.GetString(3))}</p></div>" : null,
                        ["commentable_type"] = r.GetString(4),
                        ["commentable_id"] = r.GetInt64(5),
                        ["created_at"] = r.GetString(6),
                        ["updated_at"] = r.GetString(7),
                        ["deleted_at"] = r.IsDBNull(8) ? null : r.GetString(8),
                        ["edited_at"] = r.IsDBNull(9) ? null : r.GetString(9),
                        ["edited_by_id"] = r.IsDBNull(10) ? null : r.GetInt32(10),
                        ["pinned"] = r.GetInt32(11) != 0,
                        ["replies_count"] = r.GetInt32(12),
                        ["votes_count"] = r.GetInt32(13),
                        ["legacy_name"] = null,
                    });
                var ordered = ids.Select(id => comments.FirstOrDefault(x => x.Value<long>("id") == id)).OfType<JObject>().ToList();

                var userIds = ordered.Select(x => x.Value<int>("user_id")).Concat(ordered.Select(x => x.Value<int?>("edited_by_id")).OfType<int>()).Distinct();
                var votes = viewer == null || ids.Count == 0
                    ? new List<long>()
                    : c.Query($"SELECT comment_id FROM comment_votes WHERE user_id = @u AND comment_id IN ({string.Join(',', ids)})", r => r.GetInt64(0), ("@u", viewer));

                var meta = new JArray();
                if (type != null && commentableId != null)
                    meta.Add(new JObject
                    {
                        ["id"] = commentableId, ["type"] = type, ["title"] = $"{type} #{commentableId}", ["url"] = "", ["owner_id"] = null, ["owner_title"] = null,
                        ["current_user_attributes"] = new JObject { ["can_new_comment_reason"] = viewer == null ? "Please sign in to proceed." : null },
                    });

                long total = type == null ? ordered.Count : c.Scalar<long>("SELECT COUNT(*) FROM comments WHERE commentable_type = @t AND commentable_id = @i AND deleted_at IS NULL", ("@t", type), ("@i", commentableId));
                long topLevel = type == null ? ordered.Count : c.Scalar<long>("SELECT COUNT(*) FROM comments WHERE commentable_type = @t AND commentable_id = @i AND parent_id IS NULL", ("@t", type), ("@i", commentableId));

                var top = ordered.Where(x => x["parent_id"]?.Type == JTokenType.Null || x.Value<long?>("parent_id") == parentId).ToList();
                var included = ordered.Except(top).ToList();
                return new JObject
                {
                    ["comments"] = new JArray(top),
                    ["included_comments"] = new JArray(included),
                    ["pinned_comments"] = new JArray(ordered.Where(x => x.Value<bool>("pinned"))),
                    ["has_more"] = hasMore,
                    ["has_more_id"] = parentId,
                    ["user_votes"] = new JArray(votes),
                    ["user_follow"] = false,
                    ["users"] = JArray.FromObject(accounts.GetMany(userIds).Select(users.Compact), serializer),
                    ["sort"] = "new",
                    ["top_level_count"] = topLevel,
                    ["total"] = total,
                    ["commentable_meta"] = meta,
                    ["cursor"] = null,
                };
            }

            api.MapGet("/comments", (HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                var q = ctx.Request.Query;
                string? type = q["commentable_type"].ToString() is { Length: > 0 } t ? t : null;
                long? commentable = long.TryParse(q["commentable_id"], out long ci) ? ci : null;
                long? parent = long.TryParse(q["parent_id"], out long pi) && pi > 0 ? pi : null;
                int page = Math.Max(1, int.TryParse(q["page"], out int pg) ? pg : 1);
                string order = q["sort"].ToString() switch { "old" => "created_at ASC", "top" => "(SELECT COUNT(*) FROM comment_votes v WHERE v.comment_id = comments.id) DESC, created_at DESC", _ => "created_at DESC" };
                using var c = db.Open();
                var top = c.Query($"""
                    SELECT id FROM comments WHERE (@t::text IS NULL OR commentable_type = @t) AND (@i::bigint IS NULL OR commentable_id = @i)
                      AND {(parent == null ? "parent_id IS NULL" : "parent_id = @p")} ORDER BY pinned DESC, {order} LIMIT 51 OFFSET @o
                    """, r => r.GetInt64(0), ("@t", type), ("@i", commentable), ("@p", parent), ("@o", (page - 1) * 50));
                bool hasMore = top.Count > 50;
                top = top.Take(50).ToList();
                var replies = top.Count == 0 ? new List<long>() : c.Query($"SELECT id FROM comments WHERE parent_id IN ({string.Join(',', top)}) ORDER BY created_at LIMIT 100", r => r.GetInt64(0));
                return Json.Ok(bundle(c, top.Concat(replies).ToList(), type, commentable, uid(ctx), accounts, users, hasMore, parent));
            });

            api.MapPost("/comments", async (HttpContext ctx, Db db, Accounts accounts, UsersService users, Notifications notifications) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                if (accounts.Get(u) is not { } me || me.SilencedUntil > DateTimeOffset.UtcNow) return Json.Error("You can't comment right now.", 403);
                if (accounts.NeedsEmailConfirmation(u)) return Json.Error(Accounts.CONFIRM_EMAIL_MESSAGE, 403);
                var p = await Json.Params(ctx.Request);
                string message = (p("comment[message]") ?? "").Trim();
                string type = p("comment[commentable_type]") ?? "";
                if (message.Length == 0 || !long.TryParse(p("comment[commentable_id]"), out long commentable)) return Json.Error("invalid comment", 422);
                long? parent = long.TryParse(p("comment[parent_id]"), out long pid) && pid > 0 ? pid : null;
                string now = DateTimeOffset.UtcNow.ToString("O");
                using var c = db.Open();
                if (type is not ("news_post" or "beatmapset" or "build") || commentable <= 0
                    || (parent != null && c.Scalar<long>("SELECT COUNT(*) FROM comments WHERE id = @p AND commentable_type = @t AND commentable_id = @i AND deleted_at IS NULL", ("@p", parent), ("@t", type), ("@i", commentable)) != 1))
                    return Json.Error("Invalid comment target", 422);
                long id = c.Insert("INSERT INTO comments (commentable_type, commentable_id, parent_id, user_id, message, created_at, updated_at) VALUES (@t, @i, @p, @u, @m, @n, @n)",
                    ("@t", type), ("@i", commentable), ("@p", parent), ("@u", u), ("@m", message.Length > 4000 ? message[..4000] : message), ("@n", now));

                if (parent != null && (int?)c.Scalar<long?>("SELECT user_id FROM comments WHERE id = @p", ("@p", parent)) is int parentAuthor && parentAuthor != u)
                    _ = notifications.Push(parentAuthor, "comment_new", type, commentable, "comment", u,
                        new JObject { ["comment_id"] = id, ["title"] = $"{type} #{commentable}", ["content"] = message, ["username"] = me.Username, ["reply_to"] = parent });

                return Json.Ok(bundle(c, new List<long> { id }, type, commentable, u, accounts, users));
            });

            api.MapPut("/comments/{id:long}", async (long id, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var p = await Json.Params(ctx.Request);
                using var c = db.Open();
                c.Exec("UPDATE comments SET message = @m, edited_at = @n, edited_by_id = @u, updated_at = @n WHERE id = @i AND (user_id = @u OR @a = 1)",
                    ("@m", (p("comment[message]") ?? "") is var edited && edited.Length > 4000 ? edited[..4000] : edited), ("@n", DateTimeOffset.UtcNow.ToString("O")), ("@u", u), ("@i", id), ("@a", accounts.Get(u)?.IsAdmin == true ? 1 : 0));
                return Json.Ok(bundle(c, new List<long> { id }, null, null, u, accounts, users));
            });

            api.MapDelete("/comments/{id:long}", (long id, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                using var c = db.Open();
                c.Exec("UPDATE comments SET deleted_at = @n WHERE id = @i AND (user_id = @u OR @a = 1)",
                    ("@n", DateTimeOffset.UtcNow.ToString("O")), ("@i", id), ("@u", u), ("@a", accounts.Get(u)?.IsAdmin == true ? 1 : 0));
                return Json.Ok(bundle(c, new List<long> { id }, null, null, u, accounts, users));
            });

            api.MapPost("/comments/{id:long}/vote", (long id, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                using var c = db.Open();
                if (c.Scalar<long>("SELECT user_id FROM comments WHERE id = @i", ("@i", id)) == u) return Json.Error("Can't vote on your own comment.", 422);
                c.Exec("INSERT INTO comment_votes (comment_id, user_id) VALUES (@i, @u) ON CONFLICT DO NOTHING", ("@i", id), ("@u", u));
                return Json.Ok(bundle(c, new List<long> { id }, null, null, u, accounts, users));
            });

            api.MapDelete("/comments/{id:long}/vote", (long id, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                using var c = db.Open();
                c.Exec("DELETE FROM comment_votes WHERE comment_id = @i AND user_id = @u", ("@i", id), ("@u", u));
                return Json.Ok(bundle(c, new List<long> { id }, null, null, u, accounts, users));
            });
        }

        // ------------------------------------------------------------------ misc content

        private static void mapMisc(RouteGroupBuilder api)
        {
            api.MapGet("/news", (HttpContext ctx, IConfiguration config) =>
                Json.Ok(News.Index(config, News.Locale(ctx), int.TryParse(ctx.Request.Query["year"], out int year) ? year : null)));
            api.MapGet("/news/{slug}", (string slug, HttpContext ctx, IConfiguration config) =>
                News.Show(config, slug, News.Locale(ctx)) is JObject post ? Json.Ok(post) : Json.Error("Post not found", 404));

            api.MapGet("/changelog", (HttpContext ctx, Presence presence) =>
                Json.Ok(News.Changelog(News.Locale(ctx), presence.Online.Count(e => e.Value.Connections > 0), ctx.Request.Query["stream"].FirstOrDefault())));
            api.MapGet("/changelog/{stream}/{version}", (string stream, string version, HttpContext ctx, Presence presence) =>
                News.ChangelogBuild(News.Locale(ctx), stream, version, presence.Online.Count(e => e.Value.Connections > 0)) is JObject build ? Json.Ok(build) : Json.Error("Build not found", 404));

            // default menu backgrounds for everyone: every image dropped into the site's backgrounds/ folder (no rebuild needed).
            api.MapGet("/seasonal-backgrounds", (IConfiguration config) =>
            {
                string dir = config["BackgroundsDirectory"] ?? "/var/www/osu/site/backgrounds";
                string publicBase = (config["PublicUrl"] ?? "https://osu.xcsoftworks.com").TrimEnd('/');
                var files = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal)
                    : Enumerable.Empty<string>();
                return Json.Ok(new
                {
                    ends_at = DateTimeOffset.UtcNow.AddYears(1),
                    backgrounds = files.Select(f => new { url = $"{publicBase}/backgrounds/{Uri.EscapeDataString(Path.GetFileName(f))}", user = (object?)null }).ToArray(),
                });
            });

            // /wiki/{locale}/{path} is what the game asks for; the site also lists every page with /wiki-index
            api.MapGet("/wiki-index", (HttpContext ctx) => Json.Ok(News.WikiIndex(News.Locale(ctx))));
            api.MapGet("/wiki/{locale}/{**path}", (string locale, string? path) =>
                News.Wiki(locale.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en", path) is JObject page ? Json.Ok(page) : Json.Error("Page not found", 404));

            api.MapGet("/search", (HttpRequest req, Accounts accounts, UsersService users, Db db) =>
            {
                string query = req.Query["query"].ToString().Trim();
                string mode = req.Query["mode"].ToString();
                int page = Math.Max(1, int.TryParse(req.Query["page"], out int p) ? p : 1);
                var found = new List<APIUser>();
                long total = 0;
                if (query.Length > 0 && mode is "" or "all" or "user")
                {
                    using var c = db.Open();
                    string like = "%" + query.Replace("%", "").Replace("_", "\\_") + "%";
                    total = c.Scalar<long>("SELECT COUNT(*) FROM users WHERE username LIKE @q ESCAPE '\\' AND restricted = 0", ("@q", like));
                    var ids = c.Query("""
                        SELECT id FROM users WHERE username LIKE @q ESCAPE '\' AND restricted = 0
                        ORDER BY CASE WHEN username = @exact THEN 0 ELSE 1 END, length(username), username LIMIT 20 OFFSET @o
                        """, r => r.GetInt32(0), ("@q", like), ("@exact", query), ("@o", (page - 1) * 20));
                    found = accounts.GetMany(ids).Select(users.Compact).ToList();
                }

                return Json.Ok(new { user = new { data = found, total }, wiki_page = new { data = Array.Empty<object>(), total = 0 } });
            });

            api.MapPost("/reports", async (HttpContext ctx, Db db, Accounts accounts) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                if (accounts.NeedsEmailConfirmation(u)) return Json.Error(Accounts.CONFIRM_EMAIL_MESSAGE, 403);
                var p = await Json.Params(ctx.Request);
                using var c = db.Open();
                if ((p("reason")?.Length ?? 0) > 100 || (p("comments")?.Length ?? 0) > 4000 || (p("reportable_type")?.Length ?? 0) > 40)
                    return Json.Error("Report fields too long", 422);
                string reportType = p("reportable_type") ?? "user";
                long reportId = long.TryParse(p("reportable_id"), out long parsedReportId) ? parsedReportId : 0;
                if (reportId <= 0 || reportType is not ("user" or "score" or "comment" or "message" or "beatmapset"))
                    return Json.Error("Invalid report target", 422);
                if (c.Scalar<long>("SELECT COUNT(*) FROM reports WHERE reporter_id = @u AND reportable_type = @t AND reportable_id = @i AND created_at > @at",
                    ("@u", u), ("@t", reportType), ("@i", reportId), ("@at", DateTimeOffset.UtcNow.AddHours(-24).ToString("O"))) > 0)
                    return Results.NoContent();
                c.Exec("INSERT INTO reports (reporter_id, reportable_type, reportable_id, reason, comments, created_at) VALUES (@u, @t, @i, @r, @c, @n)",
                    ("@u", u), ("@t", p("reportable_type") ?? "user"), ("@i", long.TryParse(p("reportable_id"), out long rid) ? rid : 0), ("@r", p("reason")),
                    ("@c", p("comments")), ("@n", DateTimeOffset.UtcNow.ToString("O")));
                return Results.NoContent();
            });

            api.MapPost("/screenshots", async (HttpContext ctx, Db db, UsersService users) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                if (!ctx.Request.HasFormContentType) return Json.Error("no screenshot", 422);
                var form = await ctx.Request.ReadFormAsync();
                var file = form.Files["screenshot"];
                if (file == null || file.Length == 0 || file.Length > 16 << 20) return Json.Error("invalid screenshot", 422);
                string hash = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                string dir = Path.Combine(db.DataDirectory, "screenshots");
                Directory.CreateDirectory(dir);
                using var input = new MemoryStream();
                await file.CopyToAsync(input, ctx.RequestAborted);
                var jpeg = await SafeImages.Reencode(input.ToArray(), true, ctx.RequestAborted);
                await SafeImages.WriteAtomic(Path.Combine(dir, hash + ".jpg"), jpeg, ctx.RequestAborted);
                using var c = db.Open();
                c.Exec("INSERT INTO screenshots (user_id, hash, created_at) VALUES (@u, @h, @n)", ("@u", u), ("@h", hash), ("@n", DateTimeOffset.UtcNow.ToString("O")));
                return Json.Ok(new { url = $"{users.PublicBase}/api/xc/ss/{hash}" });
            });
        }
    }
}
