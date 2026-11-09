// xc-osu! server — the osu! API v2 surface used by the lazer client. xc softworks (kudasaixc)

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;
using osu.Game.Scoring;
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

            var api = app.MapGroup("/api/v2");
            mapUsers(api);
            mapBeatmaps(api);
            mapScores(api);
            mapRankings(api);
            mapChat(api);
            mapSocial(api);
            mapMisc(api);
            Rooms.Map(api);
        }

        private static int? uid(HttpContext ctx) => ctx.User.UserId();

        private static IResult unauthorized() => Json.Error("unauthorized", 401);

        // ------------------------------------------------------------------ auth

        private static void mapAuth(WebApplication app)
        {
            app.MapPost("/oauth/token", async (HttpRequest req, Accounts accounts) =>
            {
                var p = await Json.Params(req);
                int? userId = null;

                switch (p("grant_type"))
                {
                    case "password":
                        userId = accounts.Verify(p("username") ?? "", p("password") ?? "")?.Id;
                        if (userId == null)
                            return Json.Ok(new { error = "invalid_grant", error_description = "Incorrect sign in", hint = "Incorrect sign in", message = "Incorrect sign in" }, 400);
                        break;

                    case "refresh_token":
                        userId = accounts.Refresh(p("refresh_token") ?? "");
                        if (userId == null)
                            return Json.Ok(new { error = "invalid_grant", error_description = "The refresh token is invalid.", message = "The refresh token is invalid." }, 401);
                        break;

                    case "client_credentials":
                        return Json.Ok(new { token_type = "Bearer", expires_in = Accounts.TOKEN_LIFETIME_SECONDS, access_token = "guest" });

                    default:
                        return Json.Ok(new { error = "unsupported_grant_type" }, 400);
                }

                var (access, refresh) = accounts.IssueToken(userId.Value);
                return Json.Ok(new { token_type = "Bearer", expires_in = Accounts.TOKEN_LIFETIME_SECONDS, access_token = access, refresh_token = refresh });
            });

            app.MapPost("/users", async (HttpContext ctx, Accounts accounts) =>
            {
                var p = await Json.Params(ctx.Request);
                string country = ctx.Request.Headers["CF-IPCountry"].ToString() is { Length: 2 } cc && cc != "XX" ? cc.ToUpperInvariant() : "XX";
                var errors = accounts.Register(p("user[username]") ?? "", p("user[user_email]") ?? "", p("user[password]") ?? "", country);
                if (errors != null)
                    return Json.Ok(new { form_error = new { user = errors } }, 422);
                return Json.Ok(new { });
            });

            // second factor isn't used on xc-osu!, but keep the endpoints answering.
            app.MapPost("/api/v2/session/verify", () => Results.NoContent());
            app.MapPost("/api/v2/session/verify/reissue", () => Json.Ok(new { success = "ok" }));
            app.MapPost("/api/v2/session/verify/mail-fallback", () => Json.Ok(new { success = "ok" }));
            app.MapDelete("/api/v2/oauth/tokens/current", (HttpRequest req, Accounts accounts) =>
            {
                string auth = req.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer ")) accounts.Revoke(auth[7..]);
                return Results.NoContent();
            });
        }

        // ------------------------------------------------------------------ users

        private static void mapUsers(RouteGroupBuilder api)
        {
            IResult me(HttpContext ctx, UsersService users, Accounts accounts)
            {
                if (uid(ctx) is not int id) return unauthorized();
                var row = accounts.Get(id);
                if (row == null) return unauthorized();
                var full = users.Full(row, 0);
                var obj = JObject.FromObject(full, JsonSerializer.Create(Json.SETTINGS));
                obj["session_verified"] = true;
                obj["session_verification_method"] = null;
                return Json.Ok(obj);
            }

            api.MapGet("/me", me);
            api.MapGet("/me/{mode}", (HttpContext ctx, UsersService users, Accounts accounts, string mode) => me(ctx, users, accounts));

            api.MapGet("/users/{lookup}/{mode?}", (string lookup, string? mode, HttpRequest req, UsersService users, Accounts accounts) =>
            {
                if (lookup == "lookup") return lookupMany(req, users, accounts);
                string key = req.Query["key"].ToString();
                if (lookup.StartsWith('@')) { key = "username"; lookup = lookup[1..]; }
                AccountRow? row = key != "username" && int.TryParse(lookup, out int id) ? accounts.Get(id) : accounts.GetByName(lookup);
                return row == null ? Json.Error("User not found", 404) : Json.Ok(users.Full(row, 0));
            });

            api.MapGet("/users", (HttpRequest req, UsersService users, Accounts accounts) => lookupMany(req, users, accounts));

            static IResult lookupMany(HttpRequest req, UsersService users, Accounts accounts)
            {
                var ids = req.Query["ids[]"].Select(s => int.TryParse(s, out int i) ? i : -1).Where(i => i > 0);
                return Json.Ok(new { users = accounts.GetMany(ids).Select(r => users.Full(r, 0)) });
            }

            api.MapGet("/users/{id:int}/scores/{type}", (int id, string type, HttpRequest req, Db db, Accounts accounts, UsersService users, Mirrors mirrors) =>
            {
                int limit = Math.Clamp(int.TryParse(req.Query["limit"], out int l) ? l : 5, 1, 100);
                int offset = int.TryParse(req.Query["offset"], out int o) ? o : 0;
                using var c = db.Open();
                List<long> ids = type switch
                {
                    "best" => users.Bests(c, id, 0).Skip(offset).Take(limit).Select(b => b.ScoreId).ToList(),
                    "recent" => c.Query("SELECT id FROM scores WHERE user_id = $u AND ended_at > $t ORDER BY id DESC LIMIT $l OFFSET $o", r => r.GetInt64(0),
                        ("$u", id), ("$t", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")), ("$l", limit), ("$o", offset)),
                    "firsts" => c.Query("""
                        SELECT id FROM (SELECT id, beatmap_id, user_id, MAX(total_score) FROM scores WHERE passed = 1 GROUP BY beatmap_id)
                        WHERE user_id = $u ORDER BY id DESC LIMIT $l OFFSET $o
                        """, r => r.GetInt64(0), ("$u", id), ("$l", limit), ("$o", offset)),
                    _ => new List<long>(),
                };
                return Json.Ok(Scores.Load(c, ids, accounts, users, withBeatmap: true));
            });

            api.MapGet("/users/{id:int}/beatmapsets/{type}", () => Json.Ok(Array.Empty<object>()));
            api.MapGet("/users/{id:int}/kudosu", () => Json.Ok(Array.Empty<object>()));
            api.MapGet("/users/{id:int}/recent_activity", () => Json.Ok(Array.Empty<object>()));
        }

        // ------------------------------------------------------------------ beatmaps (mirrors)

        private static void mapBeatmaps(RouteGroupBuilder api)
        {
            api.MapGet("/beatmapsets/search", async (HttpRequest req, Mirrors mirrors, CancellationToken ct) =>
            {
                int page = int.TryParse(req.Query["cursor[page]"], out int p) ? p : 0;
                var sets = await mirrors.Search(req.Query, page, ct);
                return Json.Ok(new JObject
                {
                    ["beatmapsets"] = sets,
                    ["total"] = sets.Count < 50 ? page * 50 + sets.Count : (page + 2) * 50,
                    ["cursor"] = sets.Count < 50 ? null : new JObject { ["page"] = page + 1 },
                    ["cursor_string"] = sets.Count < 50 ? null : (page + 1).ToString(),
                });
            });

            api.MapGet("/beatmapsets/lookup", async (int beatmap_id, Mirrors mirrors, CancellationToken ct) =>
            {
                var map = await mirrors.GetBeatmap(beatmap_id, null, null, ct);
                int? setId = map?.Value<int?>("beatmapset_id");
                var set = setId == null ? null : await mirrors.GetBeatmapset(setId.Value, ct);
                return set == null ? Json.Error("Beatmap not found", 404) : Json.Ok(set);
            });

            api.MapGet("/beatmapsets/{id:int}", async (int id, Mirrors mirrors, CancellationToken ct) =>
                await mirrors.GetBeatmapset(id, ct) is JObject set ? Json.Ok(set) : Json.Error("Beatmapset not found", 404));

            api.MapGet("/beatmapsets/{id:int}/download", async (int id, HttpContext ctx, Mirrors mirrors, CancellationToken ct) =>
            {
                bool noVideo = ctx.Request.Query["noVideo"] == "1";
                using var res = await mirrors.Download(id, noVideo, ct);
                if (res == null) return Json.Error("No mirror has this beatmapset right now.", 404);

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-osu-beatmap-archive";
                if (res.Content.Headers.ContentLength is long len) ctx.Response.ContentLength = len;
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{id}.osz\"";
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
                    c.Exec("INSERT OR IGNORE INTO favourites (user_id, beatmapset_id) VALUES ($u, $s)", ("$u", u), ("$s", id));
                else
                    c.Exec("DELETE FROM favourites WHERE user_id = $u AND beatmapset_id = $s", ("$u", u), ("$s", id));
                return Json.Ok(new { favourite_count = c.Scalar<long>("SELECT COUNT(*) FROM favourites WHERE beatmapset_id = $s", ("$s", id)) });
            });

            api.MapGet("/me/beatmapset-favourites", (HttpContext ctx, Db db) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                using var c = db.Open();
                return Json.Ok(new { beatmapset_ids = c.Query("SELECT beatmapset_id FROM favourites WHERE user_id = $u", r => r.GetInt32(0), ("$u", u)) });
            });

            api.MapGet("/beatmaps/lookup", async (HttpRequest req, Mirrors mirrors, CancellationToken ct) =>
            {
                int? id = int.TryParse(req.Query["id"], out int i) ? i : null;
                string? checksum = req.Query["checksum"].ToString() is { Length: > 0 } cs ? cs : null;
                var map = await mirrors.GetBeatmap(id, checksum, req.Query["filename"], ct);
                return map == null ? Json.Error("Beatmap not found", 404) : Json.Ok(map);
            });

            api.MapGet("/beatmaps", async (HttpRequest req, Mirrors mirrors, CancellationToken ct) =>
            {
                var ids = req.Query["ids[]"].Select(s => int.TryParse(s, out int i) ? i : -1).Where(i => i > 0).Distinct().Take(50);
                var maps = await Task.WhenAll(ids.Select(i => mirrors.GetBeatmap(i, null, null, ct)));
                return Json.Ok(new { beatmaps = maps.Where(m => m != null) });
            });

            api.MapGet("/beatmaps/{id:int}", async (int id, Mirrors mirrors, CancellationToken ct) =>
                await mirrors.GetBeatmap(id, null, null, ct) is JObject m ? Json.Ok(m) : Json.Error("Beatmap not found", 404));

            api.MapPost("/beatmaps/{id:int}/attributes", async (int id, HttpRequest req, Performance perf, CancellationToken ct) =>
            {
                var p = await Json.Params(req);
                int ruleset = int.TryParse(p("ruleset_id"), out int r) ? r : 0;
                var d = await perf.CalculateDifficulty(id, ruleset, Array.Empty<APIMod>(), ct);
                return Json.Ok(new { attributes = new { star_rating = d?.starRating ?? 0, max_combo = d?.maxCombo ?? 0 } });
            });

            api.MapGet("/beatmaps/{id:int}/tags", () => Json.Ok(Array.Empty<object>()));
            api.MapGet("/tags", () => Json.Ok(new { tags = Array.Empty<object>() }));
        }

        // ------------------------------------------------------------------ solo scores

        private static void mapScores(RouteGroupBuilder api)
        {
            api.MapPost("/beatmaps/{id:int}/solo/scores", async (int id, HttpContext ctx, Db db) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var p = await Json.Params(ctx.Request);
                using var c = db.Open();
                long token = c.Insert("INSERT INTO score_tokens (user_id, beatmap_id, ruleset_id, created_at) VALUES ($u, $b, $r, $t)",
                    ("$u", u), ("$b", id), ("$r", int.TryParse(p("ruleset_id"), out int r) ? r : 0), ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                return Json.Ok(new { id = token, created_at = DateTimeOffset.UtcNow });
            });

            api.MapPut("/beatmaps/{id:int}/solo/scores/{token:long}", async (int id, long token, HttpContext ctx, Scores scores, CancellationToken ct) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var score = await Json.Body<SoloScoreInfo>(ctx.Request);
                if (score == null) return Json.Error("invalid score");
                var result = await scores.Submit(u, id, token, null, score, ct);
                return result == null ? Json.Error("invalid score token", 404) : Json.Ok(result);
            });

            api.MapGet("/beatmaps/{id:int}/scores", (int id, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                var q = ctx.Request.Query;
                int ruleset = q["mode"].ToString() switch { "taiko" => 1, "fruits" => 2, "mania" => 3, _ => 0 };
                int limit = Math.Clamp(int.TryParse(q["limit"], out int l) ? l : 50, 1, 100);
                string type = q["type"].ToString();
                var mods = q["mods[]"].Where(m => !string.IsNullOrEmpty(m)).Select(m => m!).ToHashSet();
                int? me = uid(ctx);

                using var c = db.Open();
                string filter = type == "friend" && me != null
                    ? $" AND (s.user_id = {me} OR s.user_id IN (SELECT target_id FROM friends WHERE user_id = {me}))"
                    : "";

                // best score per user
                var rows = c.Query($"""
                    SELECT s.id, s.user_id, s.mods FROM scores s
                    WHERE s.beatmap_id = $b AND s.ruleset_id = $r AND s.passed = 1 {filter}
                    ORDER BY s.total_score DESC, s.id ASC
                    """, r => (id: r.GetInt64(0), user: r.GetInt32(1), mods: r.GetString(2)), ("$b", id), ("$r", ruleset));

                var seen = new HashSet<int>();
                var best = new List<long>();
                foreach (var row in rows)
                {
                    if (mods.Count > 0)
                    {
                        var acronyms = JArray.Parse(row.mods).Select(m => m.Value<string>("acronym")).Where(a => a != null).ToHashSet();
                        if (mods.Contains("NM") ? acronyms.Count > 0 : !mods.SetEquals(acronyms!)) continue;
                    }

                    if (seen.Add(row.user)) best.Add(row.id);
                }

                var all = Scores.Load(c, best, accounts, users, withBeatmap: false);
                APIScoreWithPosition? userScore = null;
                int myIndex = me == null ? -1 : all.FindIndex(s => s.UserID == me);
                if (myIndex >= 0)
                    userScore = new APIScoreWithPosition { Score = all[myIndex], Position = myIndex + 1 };

                return Json.Ok(new APIScoresCollection { Scores = all.Take(limit).ToList(), UserScore = userScore, ScoresCount = all.Count });
            });

            api.MapGet("/scores/{id:long}", (long id, Db db, Accounts accounts, UsersService users) =>
            {
                using var c = db.Open();
                return Scores.Load(c, new[] { id }, accounts, users, withBeatmap: true).FirstOrDefault() is SoloScoreInfo s ? Json.Ok(s) : Json.Error("Score not found", 404);
            });

            api.MapGet("/scores/{id:long}/download", (long id, Scores scores) =>
                scores.ReplayPath(id) is string path ? Results.File(path, "application/x-osu-replay", $"xc-osu-replay-{id}.osr") : Json.Error("Replay not available", 404));
        }

        // ------------------------------------------------------------------ rankings

        private static void mapRankings(RouteGroupBuilder api)
        {
            api.MapGet("/rankings/{mode}/{type}", (string mode, string type, HttpRequest req, UsersService users, Accounts accounts) =>
            {
                int page = int.TryParse(req.Query["cursor[page]"], out int p) ? p : int.TryParse(req.Query["page"], out p) ? p : 1;
                var board = users.Leaderboard(0).OrderByDescending(e => e.pp).ToList();
                var slice = board.Skip((page - 1) * 50).Take(50).ToList();
                var rows = accounts.GetMany(slice.Select(e => e.userId)).ToDictionary(r => r.Id);
                var ranking = new List<osu.Game.Users.UserStatistics>();
                foreach (var e in slice)
                {
                    if (!rows.TryGetValue(e.userId, out var row)) continue;
                    var stats = users.Statistics(e.userId, 0);
                    stats.User = users.Compact(row);
                    ranking.Add(stats);
                }

                return Json.Ok(new { ranking, total = board.Count, cursor = board.Count > page * 50 ? new { page = page + 1 } : null });
            });

            api.MapGet("/spotlights", () => Json.Ok(new { spotlights = Array.Empty<object>() }));
        }

        // ------------------------------------------------------------------ chat

        private static void mapChat(RouteGroupBuilder api)
        {
            api.MapGet("/chat/channels", (HttpContext ctx, Chat chat) => uid(ctx) is int u ? Json.Ok(chat.PublicChannels(u)) : unauthorized());

            api.MapGet("/chat/channels/{id:int}", (int id, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var ch = chat.Get(id, u);
                return ch == null ? Json.Error("not found", 404) : Json.Ok(new { channel = ch, users = ch.Users });
            });

            api.MapPut("/chat/channels/{id:int}/users/{user:int}", (int id, int user, HttpContext ctx, Chat chat) =>
                uid(ctx) is int u && u == user ? chat.Join(id, u) is Channel ch ? Json.Ok(ch) : Json.Error("not found", 404) : unauthorized());

            api.MapDelete("/chat/channels/{id:int}/users/{user:int}", (int id, int user, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u || u != user) return unauthorized();
                chat.Leave(id, u);
                return Results.NoContent();
            });

            api.MapGet("/chat/channels/{id:int}/messages", (int id, HttpContext ctx, Chat chat) =>
                uid(ctx) is int u ? chat.CanRead(id, u) ? Json.Ok(chat.Messages(id)) : Json.Error("forbidden", 403) : unauthorized());

            api.MapPost("/chat/channels/{id:int}/messages", async (int id, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                if (!chat.CanRead(id, u)) return Json.Error("forbidden", 403);
                var p = await Json.Params(ctx.Request);
                string content = p("message") ?? "";
                if (string.IsNullOrWhiteSpace(content)) return Json.Error("empty message", 422);
                return Json.Ok(chat.Post(id, u, content, p("is_action") == "true", p("uuid")));
            });

            api.MapPut("/chat/channels/{id:int}/mark-as-read/{message:long}", (int id, long message, HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                chat.MarkRead(id, u, message);
                return Results.NoContent();
            });

            api.MapPost("/chat/new", async (HttpContext ctx, Chat chat, Db db) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                var p = await Json.Params(ctx.Request);
                if (!int.TryParse(p("target_id"), out int target)) return Json.Error("invalid target", 422);
                using (var c = db.Open())
                    if (c.Scalar<long>("SELECT COUNT(*) FROM blocks WHERE user_id = $t AND target_id = $u", ("$t", target), ("$u", u)) > 0)
                        return Json.Error("You cannot message this user.", 403);
                int channel = chat.OpenPm(u, target);
                var msg = chat.Post(channel, u, p("message") ?? "", p("is_action") == "true", p("uuid"));
                return Json.Ok(new { new_channel_id = channel, channel = chat.Get(channel, u), message = msg });
            });

            api.MapPost("/chat/ack", (HttpContext ctx) => uid(ctx) != null ? Json.Ok(new { silences = Array.Empty<object>() }) : unauthorized());

            api.MapGet("/chat/updates", (HttpContext ctx, Chat chat) =>
            {
                if (uid(ctx) is not int u) return unauthorized();
                long since = long.TryParse(ctx.Request.Query["since"], out long s) ? s : 0;
                var channels = chat.JoinedChannels(u);
                var messages = channels.SelectMany(ch => chat.Messages((int)ch.Id, since)).OrderBy(m => m.Id).ToList();
                return Json.Ok(new { presence = channels, messages, silences = Array.Empty<object>() });
            });

            api.MapGet("/notifications", (HttpContext ctx) =>
            {
                string scheme = ctx.Request.Headers["X-Forwarded-Proto"] == "https" || ctx.Request.IsHttps ? "wss" : "ws";
                return Json.Ok(new { has_more = false, notifications = Array.Empty<object>(), notification_endpoint = $"{scheme}://{ctx.Request.Host}/notifications" });
            });
            api.MapPost("/notifications/mark-read", () => Results.NoContent());
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
                    var targets = c.Query($"SELECT target_id FROM {table} WHERE user_id = $u", r => r.GetInt32(0), ("$u", u));
                    var mutual = c.Query($"SELECT user_id FROM {table} WHERE target_id = $u", r => r.GetInt32(0), ("$u", u)).ToHashSet();
                    return Json.Ok(accounts.GetMany(targets).Select(r => new
                    {
                        target_id = r.Id, relation_type = kind, mutual = mutual.Contains(r.Id), target = users.Full(r, 0),
                    }));
                });

                api.MapPost($"/{table}", (HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
                {
                    if (uid(ctx) is not int u) return unauthorized();
                    if (!int.TryParse(ctx.Request.Query["target"], out int t) || accounts.Get(t) is not AccountRow row) return Json.Error("user not found", 404);
                    using var c = db.Open();
                    c.Exec($"INSERT OR IGNORE INTO {table} (user_id, target_id) VALUES ($u, $t)", ("$u", u), ("$t", t));
                    bool mutual = c.Scalar<long>($"SELECT COUNT(*) FROM {table} WHERE user_id = $t AND target_id = $u", ("$u", u), ("$t", t)) > 0;
                    var rel = new { target_id = t, relation_type = kind, mutual, target = users.Full(row, 0) };
                    return Json.Ok(new { user_relation = rel });
                });

                api.MapDelete($"/{table}/{{target:int}}", (int target, HttpContext ctx, Db db) =>
                {
                    if (uid(ctx) is not int u) return unauthorized();
                    using var c = db.Open();
                    c.Exec($"DELETE FROM {table} WHERE user_id = $u AND target_id = $t", ("$u", u), ("$t", target));
                    return Results.NoContent();
                });
            }

            relations("friends", "friend");
            relations("blocks", "block");
        }

        // ------------------------------------------------------------------ misc content

        private static void mapMisc(RouteGroupBuilder api)
        {
            api.MapGet("/news", () => Json.Ok(new
            {
                news_posts = new[]
                {
                    new
                    {
                        id = 1, author = "kudasaixc", slug = "welcome-to-xc-osu", title = "Welcome to xc-osu!",
                        preview = "xc-osu! runs the full lazer client in your browser. Brought to you by xc softworks.",
                        published_at = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), updated_at = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero),
                        first_image = (string?)null, edit_url = (string?)null,
                    },
                },
                news_sidebar = new { current_year = 2026, years = new[] { 2026 }, news_posts = Array.Empty<object>() },
                cursor = (object?)null,
                search = new { limit = 12, sort = "published_desc" },
            }));

            api.MapGet("/changelog", () => Json.Ok(new { builds = Array.Empty<object>(), streams = Array.Empty<object>() }));
            api.MapGet("/changelog/{stream}/{version}", () => Json.Error("Build not found", 404));
            api.MapGet("/seasonal-backgrounds", () => Json.Ok(new { ends_at = DateTimeOffset.UtcNow.AddDays(30), backgrounds = Array.Empty<object>() }));
            api.MapGet("/comments", () => Json.Ok(new
            {
                comments = Array.Empty<object>(), has_more = false, has_more_id = (int?)null, included_comments = Array.Empty<object>(),
                pinned_comments = Array.Empty<object>(), user_votes = Array.Empty<long>(), user_follow = false, users = Array.Empty<object>(),
                sort = "new", top_level_count = 0, total = 0, commentable_meta = Array.Empty<object>(), cursor = (object?)null,
            }));
            api.MapGet("/wiki/{**path}", () => Json.Error("The xc-osu! wiki lives at osu.xcsoftworks.com.", 404));
            api.MapGet("/search", () => Json.Ok(new { user = new { data = Array.Empty<object>(), total = 0 }, wiki_page = new { data = Array.Empty<object>(), total = 0 } }));
            api.MapPost("/reports", () => Results.NoContent());
            api.MapPut("/me/options", () => Results.NoContent());
            api.MapPost("/screenshots", () => Json.Ok(new { url = (string?)null }));
        }
    }
}
