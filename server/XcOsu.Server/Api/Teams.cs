// xc-osu! server: teams (create / join / leave / kick / flag). The client only displays teams, so management lives under /api/xc/teams. xc softworks (kudasaixc)

using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;
using XcOsu.Server.Services;

namespace XcOsu.Server.Api
{
    public static class Teams
    {
        private const int max_members = 256;
        private const long max_flag_bytes = 2 << 20;

        private static JObject teamJson(Npgsql.NpgsqlConnection c, Users users, Accounts accounts, long id, bool withMembers)
        {
            var team = c.Query("SELECT id, name, short_name, flag_url, leader_id, created_at FROM teams WHERE id = @t", r => new JObject
            {
                ["id"] = r.GetInt64(0),
                ["name"] = r.GetString(1),
                ["short_name"] = r.GetString(2),
                ["flag_url"] = r.IsDBNull(3) ? null : r.GetString(3),
                ["leader_id"] = r.GetInt64(4),
                ["created_at"] = r.GetString(5),
            }, ("@t", id)).FirstOrDefault();
            if (team == null) return new JObject();

            var members = c.Query("SELECT user_id FROM team_members WHERE team_id = @t", r => r.GetInt32(0), ("@t", id));
            team["members_count"] = members.Count;
            if (withMembers)
                team["members"] = new JArray(accounts.GetMany(members, c).Where(r => !r.Restricted)
                                                    .Select(r => JObject.FromObject(users.Compact(r, c), Newtonsoft.Json.JsonSerializer.Create(Json.SETTINGS))));
            return team;
        }

        private static long? teamOf(Npgsql.NpgsqlConnection c, int user) => c.Scalar<long?>("SELECT team_id FROM team_members WHERE user_id = @u", ("@u", user));

        private static string? validate(string? name, string? shortName)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length is < 2 or > 30) return "Team name must be 2–30 characters.";
            if (string.IsNullOrWhiteSpace(shortName) || !System.Text.RegularExpressions.Regex.IsMatch(shortName.Trim(), "^[A-Za-z0-9]{2,4}$"))
                return "Short name must be 2–4 letters or digits.";
            return null;
        }

        public static void Map(WebApplication app)
        {
            app.MapGet("/api/xc/teams", (HttpRequest req, Db db, Users users, Accounts accounts) =>
            {
                using var c = db.Open();
                int page = Math.Clamp(int.TryParse(req.Query["page"], out var requested) ? requested : 1, 1, 100000);
                // ?q= narrows by name or tag so a team beyond the first page stays reachable; the response stays a plain array and
                // the paging facts travel in headers, so existing callers are unaffected.
                string q = (req.Query["q"].ToString() ?? "").Trim();
                if (q.Length > 30) q = q[..30];
                string like = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                const string filter = "(@q = '' OR name LIKE @like ESCAPE '\\' OR short_name LIKE @like ESCAPE '\\')";
                long total = c.Scalar<long>("SELECT COUNT(*) FROM teams WHERE " + filter, ("@q", q), ("@like", like));
                var ids = c.Query("SELECT id FROM teams WHERE " + filter + " ORDER BY id LIMIT 50 OFFSET @offset", r => r.GetInt64(0),
                    ("@q", q), ("@like", like), ("@offset", (page - 1) * 50));
                req.HttpContext.Response.Headers["X-Total-Count"] = total.ToString();
                req.HttpContext.Response.Headers["X-Has-More"] = ((long)page * 50 < total) ? "1" : "0";
                return Json.Ok(new JArray(ids.Select(id => teamJson(c, users, accounts, id, false))));
            });

            app.MapGet("/api/xc/teams/{id:long}", (long id, Db db, Users users, Accounts accounts) =>
            {
                using var c = db.Open();
                var team = teamJson(c, users, accounts, id, true);
                return team.HasValues ? Json.Ok(team) : Json.Error("Team not found", 404);
            });

            app.MapPost("/api/xc/teams", async (HttpContext ctx, Db db, Users users, Accounts accounts) =>
            {
                if (ctx.User.UserId() is not int u || accounts.Get(u) is not { Restricted: false }) return Json.Error("unauthorized", 401);
                if (accounts.NeedsEmailConfirmation(u)) return Json.Error(Accounts.CONFIRM_EMAIL_MESSAGE, 403);
                var p = await Json.Params(ctx.Request);
                string? name = p("name")?.Trim(), shortName = p("short_name")?.Trim().ToUpperInvariant();
                if (validate(name, shortName) is string error) return Json.Error(error, 422);

                using var c = db.Open();
                if (teamOf(c, u) != null) return Json.Error("Leave your current team first.", 422);
                if (c.Scalar<long>("SELECT COUNT(*) FROM teams WHERE name = @n OR short_name = @s", ("@n", name), ("@s", shortName)) > 0)
                    return Json.Error("That team name or short name is taken.", 422);

                long id;
                try
                {
                    using var tx = c.BeginTransaction();
                    id = c.Insert("INSERT INTO teams (name, short_name, flag_url, leader_id, created_at) VALUES (@n, @s, NULL, @u, @t)",
                        ("@n", name), ("@s", shortName), ("@u", u), ("@t", DateTimeOffset.UtcNow.ToString("O")));
                    c.Exec("INSERT INTO team_members (team_id, user_id) VALUES (@t, @u)", ("@t", id), ("@u", u));
                    tx.Commit();
                }
                catch (Exception e) when (e.IsUniqueViolation())
                {
                    return Json.Error("That team name or short name is taken.", 422);
                }
                users.InvalidateTeams();
                return Json.Ok(teamJson(c, users, accounts, id, true));
            });

            app.MapPut("/api/xc/teams/{id:long}", async (long id, HttpContext ctx, Db db, Users users, Accounts accounts) =>
            {
                if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
                using var c = db.Open();
                if (c.Scalar<long?>("SELECT leader_id FROM teams WHERE id = @t", ("@t", id)) != u) return Json.Error("Only the team leader can do that.", 403);
                var p = await Json.Params(ctx.Request);
                string? name = p("name")?.Trim(), shortName = p("short_name")?.Trim().ToUpperInvariant();
                if (validate(name, shortName) is string error) return Json.Error(error, 422);
                if (c.Scalar<long>("SELECT COUNT(*) FROM teams WHERE (name = @n OR short_name = @s) AND id != @t", ("@n", name), ("@s", shortName), ("@t", id)) > 0)
                    return Json.Error("That team name or short name is taken.", 422);
                try { c.Exec("UPDATE teams SET name = @n, short_name = @s WHERE id = @t", ("@n", name), ("@s", shortName), ("@t", id)); }
                catch (Exception e) when (e.IsUniqueViolation()) { return Json.Error("That team name or short name is taken.", 422); }
                users.InvalidateTeams();
                return Json.Ok(teamJson(c, users, accounts, id, true));
            });

            app.MapPost("/api/xc/teams/{id:long}/join", (long id, HttpContext ctx, Db db, Users users, Accounts accounts) =>
            {
                if (ctx.User.UserId() is not int u || accounts.Get(u) is not { Restricted: false }) return Json.Error("unauthorized", 401);
                if (accounts.NeedsEmailConfirmation(u)) return Json.Error(Accounts.CONFIRM_EMAIL_MESSAGE, 403);
                using var c = db.Open();
                if (c.Scalar<long>("SELECT COUNT(*) FROM teams WHERE id = @t", ("@t", id)) == 0) return Json.Error("Team not found", 404);
                if (teamOf(c, u) != null) return Json.Error("Leave your current team first.", 422);
                if (c.Scalar<long>("SELECT COUNT(*) FROM team_members WHERE team_id = @t", ("@t", id)) >= max_members) return Json.Error("This team is full.", 422);
                try
                {
                    // the team row is the lock that serialises joins, so the member cap holds under concurrency.
                    using var tx = c.BeginTransaction();
                    if (c.Scalar<long?>("SELECT id FROM teams WHERE id = @t FOR UPDATE", ("@t", id)) == null) return Json.Error("Team not found", 404);
                    if (c.Exec("INSERT INTO team_members (team_id, user_id) SELECT @t, @u WHERE (SELECT COUNT(*) FROM team_members WHERE team_id = @t) < 256", ("@t", id), ("@u", u)) != 1)
                        return Json.Error("This team is full.", 422);
                    tx.Commit();
                }
                catch (Exception e) when (e.IsUniqueViolation()) { return Json.Error("Leave your current team first.", 422); }
                users.InvalidateTeams();
                return Json.Ok(teamJson(c, users, accounts, id, true));
            });

            app.MapPost("/api/xc/teams/leave", (HttpContext ctx, Db db, Users users) =>
            {
                if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
                using var c = db.Open();
                if (teamOf(c, u) is not long team) return Results.NoContent();
                c.Exec("DELETE FROM team_members WHERE user_id = @u", ("@u", u));

                // leadership passes to the longest-standing member; an empty team is disbanded.
                if (c.Scalar<long?>("SELECT leader_id FROM teams WHERE id = @t", ("@t", team)) == u)
                {
                    long? next = c.Scalar<long?>("SELECT user_id FROM team_members WHERE team_id = @t ORDER BY seq LIMIT 1", ("@t", team));
                    if (next == null) c.Exec("DELETE FROM teams WHERE id = @t", ("@t", team));
                    else c.Exec("UPDATE teams SET leader_id = @l WHERE id = @t", ("@l", next), ("@t", team));
                }

                users.InvalidateTeams();
                return Results.NoContent();
            });

            app.MapDelete("/api/xc/teams/{id:long}/members/{user:int}", (long id, int user, HttpContext ctx, Db db, Users users) =>
            {
                if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
                using var c = db.Open();
                if (c.Scalar<long?>("SELECT leader_id FROM teams WHERE id = @t", ("@t", id)) != u) return Json.Error("Only the team leader can do that.", 403);
                if (user == u) return Json.Error("Use leave instead.", 422);
                c.Exec("DELETE FROM team_members WHERE team_id = @t AND user_id = @m", ("@t", id), ("@m", user));
                users.InvalidateTeams();
                return Results.NoContent();
            });

            app.MapPost("/api/xc/teams/{id:long}/flag", async (long id, HttpContext ctx, Db db, Users users) =>
            {
                if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
                using (var c = db.Open())
                    if (c.Scalar<long?>("SELECT leader_id FROM teams WHERE id = @t", ("@t", id)) != u) return Json.Error("Only the team leader can do that.", 403);
                if (!ctx.Request.HasFormContentType) return Json.Error("Send the image as multipart form field \"file\".", 422);
                var form = await ctx.Request.ReadFormAsync();
                var file = form.Files["file"] ?? form.Files.FirstOrDefault();
                if (file == null || file.Length == 0 || file.Length > max_flag_bytes) return Json.Error("Flag must be an image up to 2 MB.", 422);

                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                if (!Media.IsImage(ms.ToArray())) return Json.Error("Only PNG, JPEG, GIF or WebP images.", 415);

                string dir = Path.Combine(db.DataDirectory, "team-flags");
                Directory.CreateDirectory(dir);
                await SafeImages.WriteAtomic(Path.Combine(dir, id.ToString()), await SafeImages.Reencode(ms.ToArray(), false, ctx.RequestAborted), ctx.RequestAborted);

                using var c2 = db.Open();
                string url = $"{users.PublicBase}/api/xc/team-flag/{id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                c2.Exec("UPDATE teams SET flag_url = @f WHERE id = @t", ("@f", url), ("@t", id));
                users.InvalidateTeams();
                return Json.Ok(new { flag_url = url });
            }).DisableAntiforgery();

            app.MapGet("/api/xc/team-flag/{id:long}", (long id, Db db) => Media.Serve(Path.Combine(db.DataDirectory, "team-flags", id.ToString()), "badge-xc.png"));
        }
    }
}
