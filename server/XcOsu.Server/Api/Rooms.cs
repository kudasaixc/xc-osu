// xc-osu! server — rooms API (lounge listing for multiplayer, full playlists support). xc softworks (kudasaixc)

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using XcOsu.Server.Data;
using XcOsu.Server.Services;
using UsersService = XcOsu.Server.Services.Users;

namespace XcOsu.Server.Api
{
    public static class Rooms
    {
        private static int? uid(HttpContext ctx) => ctx.User.UserId();

        public static void Map(RouteGroupBuilder api)
        {
            api.MapGet("/rooms", async (HttpContext ctx, RoomStore store, UsersService users, Accounts accounts, Db db) =>
            {
                string category = ctx.Request.Query["category"].ToString();
                string mode = ctx.Request.Query["mode"].ToString();
                string status = ctx.Request.Query["status"].ToString();
                string type = ctx.Request.Query["type_group"].ToString();
                int? me = uid(ctx);

                var list = new JArray();
                if (status == "ended")
                {
                    // history of finished rooms (playlists "ended" filter / daily challenge history)
                    using var c = db.Open();
                    var ended = c.Query($"""
                        SELECT id, json FROM rooms WHERE active = 0 AND length(json) > 2 {(category == "daily_challenge" ? "AND category = 'daily_challenge'" : "")}
                        {(mode == "participated" && me != null ? $"AND id IN (SELECT DISTINCT room_id FROM score_tokens WHERE user_id = {me} AND room_id IS NOT NULL)" : "")}
                        {(mode == "owned" && me != null ? $"AND host_id = {me}" : "")}
                        ORDER BY id DESC LIMIT 50
                        """, r => JObject.Parse(r.GetString(1)));
                    foreach (var j in ended)
                    {
                        if (type == "realtime" && j.Value<string>("type") == "playlists") continue;
                        if (type == "playlists" && j.Value<string>("type") != "playlists") continue;
                        j.Remove("password");
                        list.Add(j);
                    }

                    return Json.Ok(list);
                }

                foreach (var room in store.Rooms.Values.OrderByDescending(r => r.Id))
                {
                    if (room.Realtime) await store.SyncJson(room, users, accounts);
                    JObject j;
                    lock (room.Lock) j = (JObject)room.Json.DeepClone();

                    bool realtime = room.Realtime;
                    if (type == "realtime" && !realtime) continue;
                    if (type == "playlists" && realtime) continue;
                    if (string.IsNullOrEmpty(type) && category == "realtime" != realtime && category is "realtime" or "normal") continue;
                    if (mode == "owned" && j["host"]?.Value<int?>("id") != me) continue;
                    if (mode == "participated" && (me == null || !room.Participants.Contains(me.Value))) continue;
                    string roomCategory = j.Value<string>("category") ?? "normal";
                    if (category == "daily_challenge" ? roomCategory != "daily_challenge" : roomCategory == "daily_challenge") continue;
                    if (roomCategory == "matchmaking" || j.Value<string>("type") is "matchmaking" or "ranked_play") continue;
                    if (RoomStore.EndsAt(j) is DateTimeOffset end && end < DateTimeOffset.UtcNow && !realtime) continue;

                    j.Remove("password");
                    list.Add(j);
                }

                return Json.Ok(list);
            });

            api.MapPost("/rooms", async (HttpContext ctx, RoomStore store, UsersService users, Accounts accounts, Mirrors mirrors, Chat chat) =>
            {
                if (uid(ctx) is not int u) return Json.Error("unauthorized", 401);
                if (accounts.NeedsEmailConfirmation(u)) return Json.Error(Accounts.CONFIRM_EMAIL_MESSAGE, 403);
                var body = await Json.Body<JObject>(ctx.Request);
                if (body == null) return Json.Error("invalid room");

                if (body["playlist"] is not JArray input || input.Count is < 1 or > 100
                    || (body.Value<string>("description")?.Length ?? 0) > 4000)
                    return Json.Error("Invalid playlist or description size.", 422);
                int duration = body.Value<int?>("duration") ?? 60 * 24;
                if (duration is < 1 or > 43200) return Json.Error("Duration must be 1–43200 minutes.", 422);
                foreach (var entry in input)
                    if (entry is not JObject || entry["required_mods"] is JArray required && required.Count > 32
                        || entry["allowed_mods"] is JArray allowed && allowed.Count > 64
                        || Performance.Ruleset(entry.Value<int?>("ruleset_id") ?? 0) == null)
                        return Json.Error("Invalid playlist item", 422);
                var host = accounts.Get(u)!;

                var playlist = new JArray();
                ushort order = 0;
                foreach (var item in body["playlist"] as JArray ?? new JArray())
                {
                    int beatmapId = item.Value<int?>("beatmap_id") ?? item["beatmap"]?.Value<int?>("id") ?? 0;
                    playlist.Add(await store.PlaylistItemJson(0, u, beatmapId, item.Value<int?>("ruleset_id") ?? 0,
                        item["required_mods"]?.ToObject<APIMod[]>() ?? Array.Empty<APIMod>(), item["allowed_mods"]?.ToObject<APIMod[]>() ?? Array.Empty<APIMod>(),
                        false, order++, null, item.Value<bool?>("freestyle") ?? false));
                }

                if (playlist.Count == 0) return Json.Error("A playlist needs at least one item.", 422);
                if (playlist.Any(p => p["beatmap"]?["checksum"] == null)) return Json.Error("One of the beatmaps couldn't be found on the mirrors.", 422);
                string name = body.Value<string>("name") is { Length: > 0 } n ? (n.Length > 100 ? n[..100] : n) : $"{host.Username}'s playlist";
                var json = new JObject
                {
                    ["id"] = 0,
                    ["name"] = name,
                    ["description"] = body.Value<string>("description"),
                    ["category"] = "normal",
                    ["type"] = "playlists",
                    ["host"] = JObject.FromObject(users.Compact(host), JsonSerializer.Create(Json.SETTINGS)),
                    ["starts_at"] = DateTimeOffset.UtcNow,
                    ["ends_at"] = DateTimeOffset.UtcNow.AddMinutes(duration),
                    ["duration"] = duration,
                    ["max_attempts"] = body.Value<int?>("max_attempts"),
                    ["participant_count"] = 0,
                    ["recent_participants"] = new JArray(),
                    ["playlist"] = playlist,
                    ["current_playlist_item"] = playlist[0],
                    ["channel_id"] = 0,
                    ["status"] = "idle",
                    ["has_password"] = false,
                    ["queue_mode"] = "host_only",
                    ["playlist_item_stats"] = new JObject { ["count_active"] = playlist.Count, ["count_total"] = playlist.Count, ["ruleset_ids"] = new JArray(0) },
                    ["difficulty_range"] = new JObject
                    {
                        ["min"] = playlist.Min(p => p["beatmap"]?.Value<double?>("difficulty_rating") ?? 0),
                        ["max"] = playlist.Max(p => p["beatmap"]?.Value<double?>("difficulty_rating") ?? 0),
                    },
                };

                var room = store.CreatePlaylistRoom(u, json);
                await chat.JoinAndNotify(room.ChannelId, u);
                return Json.Ok(json);
            });

            api.MapGet("/rooms/{id:long}", async (long id, RoomStore store, UsersService users, Accounts accounts, Db db) =>
            {
                if (!store.Rooms.TryGetValue(id, out var room))
                {
                    using var c = db.Open();
                    string? stored = c.Scalar<string>("SELECT json FROM rooms WHERE id = $i", ("$i", id));
                    return stored == null || stored == "{}" ? Json.Error("Room not found", 404) : Json.Ok(JObject.Parse(stored));
                }

                if (room.Realtime) await store.SyncJson(room, users, accounts);
                lock (room.Lock)
                {
                    var j = (JObject)room.Json.DeepClone();
                    j.Remove("password");
                    return Json.Ok(j);
                }
            });

            api.MapDelete("/rooms/{id:long}", async (long id, HttpContext ctx, RoomStore store, Chat chat) =>
            {
                if (!store.Rooms.TryGetValue(id, out var room) || room.Realtime) return Json.Error("Room not found", 404);
                if (room.Json["host"]?.Value<int?>("id") != uid(ctx)) return Json.Error("forbidden", 403);
                store.Close(room);
                if (room.Json.Value<int?>("channel_id") is int ch and > 0) await chat.CloseRoomChannel(ch);
                return Results.NoContent();
            });

            api.MapPut("/rooms/{id:long}/users/{user:int}", async (long id, int user, HttpContext ctx, RoomStore store, UsersService users, Accounts accounts, Chat chat) =>
            {
                if (uid(ctx) != user) return Json.Error("forbidden", 403);
                if (!store.Rooms.TryGetValue(id, out var room)) return Json.Error("Room not found", 404);
                if (room.Realtime) await store.SyncJson(room, users, accounts);
                else if (room.Json.Value<int?>("channel_id") is int ch and > 0) await chat.JoinAndNotify(ch, user);
                lock (room.Lock)
                {
                    if (!room.Realtime && room.Participants.Add(user))
                    {
                        room.Json["participant_count"] = room.Participants.Count;
                        var recent = accounts.GetMany(room.Participants.TakeLast(10)).Select(users.Compact);
                        room.Json["recent_participants"] = JArray.FromObject(recent, JsonSerializer.Create(Json.SETTINGS));
                        store.Persist(room);
                    }

                    var j = (JObject)room.Json.DeepClone();
                    j.Remove("password");
                    return Json.Ok(j);
                }
            });

            api.MapDelete("/rooms/{id:long}/users/{user:int}", async (long id, int user, HttpContext ctx, RoomStore store, Chat chat) =>
            {
                if (uid(ctx) == user && store.Rooms.TryGetValue(id, out var room) && !room.Realtime && room.Json.Value<int?>("channel_id") is int ch and > 0)
                    await chat.PartAndNotify(ch, user);
                return Results.NoContent();
            });

            // ---------------------------------------------------------------- scores

            api.MapPost("/rooms/{id:long}/playlist/{item:long}/scores", async (long id, long item, HttpContext ctx, Db db, RoomStore store, Scores scores) =>
            {
                if (uid(ctx) is not int u) return Json.Error("unauthorized", 401);
                var p = await Json.Params(ctx.Request);
                int beatmapId = findBeatmap(store, db, id, item) ?? 0;

                if (beatmapId == 0) return Json.Error("Playlist item not found", 404);
                if (!store.Rooms.TryGetValue(id, out var room)) return Json.Error("This room has ended.", 422);
                if (RoomStore.EndsAt(room.Json) is DateTimeOffset end && end < DateTimeOffset.UtcNow && !room.Realtime) return Json.Error("This room has ended.", 422);
                if (room.Json.Value<int?>("max_attempts") is int max)
                {
                    using var c = db.Open();
                    long attempts = c.Scalar<long>("SELECT COUNT(*) FROM score_tokens WHERE user_id = $u AND playlist_item_id = $p", ("$u", u), ("$p", item));
                    if (attempts >= max) return Json.Error("You've run out of attempts.", 422);
                }

                try
                {
                    long token = scores.CreateToken(u, beatmapId, int.TryParse(p("ruleset_id"), out int r) ? r : 0, p("beatmap_hash"), item, id);
                    return Json.Ok(new { id = token, created_at = DateTimeOffset.UtcNow });
                }
                catch (Scores.SubmitException e)
                {
                    return Json.Error(e.Message, e.Status);
                }
            });

            api.MapPut("/rooms/{id:long}/playlist/{item:long}/scores/{token:long}", async (long id, long item, long token, HttpContext ctx, Db db, Scores scores, RoomStore store, CancellationToken ct) =>
            {
                if (uid(ctx) is not int u) return Json.Error("unauthorized", 401);
                var score = await Json.Body<SoloScoreInfo>(ctx.Request);
                if (score == null) return Json.Error("invalid score");
                int beatmapId;
                using (var c = db.Open())
                    beatmapId = (int)(c.Scalar<long?>("SELECT beatmap_id FROM score_tokens WHERE id = $t AND room_id = $r AND playlist_item_id = $p", ("$t", token), ("$r", id), ("$p", item)) ?? 0);
                SoloScoreInfo? stored;
                try
                {
                    stored = await scores.Submit(u, beatmapId, token, item, score, ct);
                }
                catch (Scores.SubmitException e)
                {
                    return Json.Error(e.Message, e.Status);
                }

                if (stored == null) return Json.Error("invalid score token", 404);

                var j = JObject.FromObject(stored, JsonSerializer.Create(Json.SETTINGS));
                j["playlist_item_id"] = item;
                j["room_id"] = id;
                return Json.Ok(j);
            });

            api.MapGet("/rooms/{id:long}/playlist/{item:long}/scores", (long id, long item, HttpContext ctx, Db db, Accounts accounts, UsersService users) =>
            {
                int limit = Math.Clamp(int.TryParse(ctx.Request.Query["limit"], out int l) ? l : 50, 1, 100);
                using (var check = db.Open())
                    if (check.Scalar<long>("SELECT COUNT(*) FROM playlist_items WHERE id = $i AND room_id = $r", ("$i", item), ("$r", id)) != 1)
                        return Json.Error("Playlist item not found", 404);
                var all = playlistBest(db, item, accounts, users);
                int? me = uid(ctx);
                int mine = all.FindIndex(s => s.UserID == me);
                return Json.Ok(new
                {
                    scores = all.Take(limit).Select((s, i) => withPosition(s, i + 1, id, item)),
                    total = all.Count,
                    user_score = mine >= 0 ? withPosition(all[mine], mine + 1, id, item) : null,
                    @params = new { limit, sort = "score_desc" },
                    cursor = (object?)null,
                });
            });

            api.MapGet("/rooms/{id:long}/playlist/{item:long}/scores/{scoreId:long}", (long id, long item, long scoreId, Db db, Accounts accounts, UsersService users) =>
            {
                using (var check = db.Open())
                    if (check.Scalar<long>("SELECT COUNT(*) FROM playlist_items WHERE id = $i AND room_id = $r", ("$i", item), ("$r", id)) != 1)
                        return Json.Error("Playlist item not found", 404);
                var all = playlistBest(db, item, accounts, users);
                int idx = all.FindIndex(s => (long)(s.ID ?? 0) == scoreId);
                if (idx < 0)
                {
                    using var c = db.Open();
                    if (c.Scalar<long>("SELECT COUNT(*) FROM scores WHERE id = $s AND playlist_item_id = $p", ("$s", scoreId), ("$p", item)) != 1)
                        return Json.Error("Score not found", 404);
                    var s = Scores.Load(c, new[] { scoreId }, accounts, users, withBeatmap: false).FirstOrDefault();
                    return s == null ? Json.Error("Score not found", 404) : Json.Ok(withPosition(s, null, id, item));
                }

                return Json.Ok(withPosition(all[idx], idx + 1, id, item, around: all));
            });

            api.MapGet("/rooms/{id:long}/playlist/{item:long}/scores/users/{user:int}", (long id, long item, int user, Db db, Accounts accounts, UsersService users) =>
            {
                using (var check = db.Open())
                    if (check.Scalar<long>("SELECT COUNT(*) FROM playlist_items WHERE id = $i AND room_id = $r", ("$i", item), ("$r", id)) != 1)
                        return Json.Error("Playlist item not found", 404);
                var all = playlistBest(db, item, accounts, users);
                int idx = all.FindIndex(s => s.UserID == user);
                return idx < 0 ? Json.Error("Score not found", 404) : Json.Ok(withPosition(all[idx], idx + 1, id, item, around: all));
            });

            api.MapGet("/rooms/{id:long}/leaderboard", (long id, HttpContext ctx, Db db, RoomStore store, Accounts accounts, UsersService users) =>
            {
                var items = playlistItemIds(store, db, id);
                if (items.Count == 0) return Json.Ok(new { leaderboard = Array.Empty<object>(), user_score = (object?)null });

                using var c = db.Open();
                var rows = c.Query($"""
                    SELECT user_id, SUM(best), AVG(acc), COUNT(*) FROM (
                        SELECT user_id, playlist_item_id, MAX(total_score) AS best, MAX(accuracy) AS acc FROM scores
                        WHERE playlist_item_id IN ({string.Join(',', items)}) AND passed = 1 GROUP BY user_id, playlist_item_id)
                    GROUP BY user_id ORDER BY SUM(best) DESC
                    """, r => (user: r.GetInt32(0), total: r.GetInt64(1), acc: r.GetDouble(2), count: r.GetInt32(3)));

                var accountsById = accounts.GetMany(rows.Select(r => r.user)).ToDictionary(a => a.Id);
                var board = rows.Select((r, i) => new
                {
                    user_id = r.user,
                    user = accountsById.TryGetValue(r.user, out var a) ? users.Compact(a) : null,
                    total_score = r.total,
                    accuracy = r.acc,
                    completed = r.count,
                    attempts = r.count,
                    playlist_item_attempts = Array.Empty<object>(),
                    position = i + 1,
                    pp = 0,
                }).ToList();

                int? me = uid(ctx);
                return Json.Ok(new { leaderboard = board, user_score = board.FirstOrDefault(b => b.user_id == me) });
            });
        }

        private static void persistItem(HttpContext ctx, JToken item, long roomId)
        {
            var db = ctx.RequestServices.GetRequiredService<Db>();
            using var c = db.Open();
            c.Exec("INSERT OR REPLACE INTO playlist_items (id, room_id, json) VALUES ($i, $r, $j)",
                ("$i", item.Value<long>("id")), ("$r", roomId), ("$j", item.ToString(Formatting.None)));
        }

        private static List<long> playlistItemIds(RoomStore store, Db db, long roomId)
        {
            if (store.Rooms.TryGetValue(roomId, out var room))
            {
                lock (room.Lock)
                {
                    if (room.Live != null) return room.Live.Playlist.Select(p => p.ID).ToList();
                    return (room.Json["playlist"] as JArray ?? new JArray()).Select(p => p.Value<long>("id")).ToList();
                }
            }

            using var c = db.Open();
            return c.Query("SELECT id FROM playlist_items WHERE room_id = $r", r => r.GetInt64(0), ("$r", roomId));
        }

        private static int? findBeatmap(RoomStore store, Db db, long roomId, long itemId)
        {
            if (store.Rooms.TryGetValue(roomId, out var room))
            {
                lock (room.Lock)
                {
                    if (room.Live?.Playlist.FirstOrDefault(p => p.ID == itemId) is { } live) return live.BeatmapID;
                    if ((room.Json["playlist"] as JArray)?.FirstOrDefault(p => p.Value<long>("id") == itemId) is JToken j) return j.Value<int>("beatmap_id");
                }
            }

            using var c = db.Open();
            string? json = c.Scalar<string>("SELECT json FROM playlist_items WHERE id = $i AND room_id = $r", ("$i", itemId), ("$r", roomId));
            return json == null ? null : JObject.Parse(json).Value<int?>("beatmap_id");
        }

        private static List<SoloScoreInfo> playlistBest(Db db, long item, Accounts accounts, UsersService users)
        {
            using var c = db.Open();
            var rows = c.Query("SELECT id, user_id FROM scores WHERE playlist_item_id = $p AND passed = 1 ORDER BY total_score DESC, id ASC",
                r => (id: r.GetInt64(0), user: r.GetInt32(1)), ("$p", item));
            var seen = new HashSet<int>();
            return Scores.Load(c, rows.Where(r => seen.Add(r.user)).Select(r => r.id), accounts, users, withBeatmap: false);
        }

        private static JObject withPosition(SoloScoreInfo s, int? position, long roomId, long item, List<SoloScoreInfo>? around = null)
        {
            var j = JObject.FromObject(s, JsonSerializer.Create(Json.SETTINGS));
            j["position"] = position;
            j["playlist_item_id"] = item;
            j["room_id"] = roomId;

            if (around != null && position is int pos)
            {
                JObject block(IEnumerable<SoloScoreInfo> list, int start) => new JObject
                {
                    ["scores"] = new JArray(list.Select((x, i) => withPosition(x, start + i, roomId, item))),
                    ["params"] = new JObject { ["limit"] = 10, ["sort"] = "score_desc" },
                    ["cursor"] = null,
                };

                int from = Math.Max(0, pos - 1 - 10);
                j["scores_around"] = new JObject
                {
                    ["higher"] = block(around.Skip(from).Take(pos - 1 - from), from + 1),
                    ["lower"] = block(around.Skip(pos).Take(10), pos + 1),
                };
            }

            return j;
        }
    }
}
