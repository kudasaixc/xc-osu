// xc-osu! server: a player's own data. Export everything we keep about an account, or erase the account for good (GDPR articles 15, 17 and 20). xc softworks (kudasaixc)

using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;
using XcOsu.Server.Services;

namespace XcOsu.Server.Api
{
    public static class AccountData
    {
        // every table holding rows about a player, with the column naming them. Shared by the export and the deletion.
        private static readonly (string table, string column)[] owned =
        {
            ("scores", "user_id"), ("score_tokens", "user_id"), ("favourites", "user_id"), ("friends", "user_id"), ("blocks", "user_id"),
            ("rank_history", "user_id"), ("beatmap_playcounts", "user_id"), ("replay_watches", "user_id"), ("user_events", "user_id"),
            ("pinned_scores", "user_id"), ("comments", "user_id"), ("comment_votes", "user_id"), ("beatmap_tags", "user_id"),
            ("user_achievements", "user_id"), ("notifications", "user_id"), ("daily_challenge_stats", "user_id"),
            ("matchmaking_ratings", "user_id"), ("team_members", "user_id"), ("reports", "reporter_id"), ("screenshots", "user_id"),
            ("chat_messages", "sender_id"), ("chat_members", "user_id"),
        };

        public static void Map(WebApplication app)
        {
            app.MapGet("/api/xc/account/export", (HttpContext ctx, Db db) =>
            {
                if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
                using var c = db.Open();
                var account = c.Scalar<string>("SELECT (to_jsonb(t) - 'password_hash')::text FROM users t WHERE id = @u", ("@u", u));
                if (account == null) return Json.Error("unauthorized", 401);

                var export = new JObject
                {
                    ["exported_at"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["account"] = JObject.Parse(account),
                    ["sessions"] = JArray.Parse(c.Scalar<string>("SELECT COALESCE(json_agg(json_build_object('expires_at', expires_at)), '[]')::text FROM tokens WHERE user_id = @u", ("@u", u))!),
                };
                foreach (var (table, column) in owned)
                    export[table] = JArray.Parse(c.Scalar<string>($"SELECT COALESCE(json_agg(t), '[]')::text FROM {table} t WHERE {column} = @u", ("@u", u))!);

                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"xc-osu-account-{u}.json\"";
                return Results.Text(export.ToString(), "application/json; charset=utf-8");
            });

            app.MapPost("/api/xc/account/delete", async (HttpContext ctx, Db db, Accounts accounts, Users users, Scores scores) =>
            {
                if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
                var p = await Json.Params(ctx.Request);
                using var c = db.Open();
                if (accounts.Get(u, c) is not AccountRow row) return Json.Error("unauthorized", 401);
                if (!Accounts.VerifyPassword(p("password") ?? "", row.PasswordHash)) return Json.Error("Password is incorrect.", 403);
                if (!string.Equals(p("confirm"), row.Username, StringComparison.OrdinalIgnoreCase)) return Json.Error("Type your username to confirm.", 422);

                var replays = c.Query("SELECT id FROM scores WHERE user_id = @u AND has_replay = 1", r => r.GetInt64(0), ("@u", u));
                var shots = c.Query("SELECT hash FROM screenshots WHERE user_id = @u", r => r.GetString(0), ("@u", u));

                using (var tx = c.BeginTransaction())
                {
                    // a team keeps going under its longest-standing other member; a team left empty is disbanded.
                    foreach (long team in c.Query("SELECT id FROM teams WHERE leader_id = @u", r => r.GetInt64(0), ("@u", u)))
                    {
                        long? next = c.Scalar<long?>("SELECT user_id FROM team_members WHERE team_id = @t AND user_id <> @u ORDER BY seq LIMIT 1", ("@t", team), ("@u", u));
                        if (next == null) c.Exec("DELETE FROM teams WHERE id = @t", ("@t", team));
                        else c.Exec("UPDATE teams SET leader_id = @l WHERE id = @t", ("@l", next), ("@t", team));
                    }

                    // a comment with replies stays as an empty "deleted" entry so the thread holds together; the rest go.
                    const string has_replies = "EXISTS (SELECT 1 FROM comments r WHERE r.parent_id = comments.id)";
                    c.Exec($"UPDATE comments SET message = '', deleted_at = COALESCE(deleted_at, @n) WHERE user_id = @u AND {has_replies}", ("@u", u), ("@n", DateTimeOffset.UtcNow.ToString("O")));
                    c.Exec($"DELETE FROM comments WHERE user_id = @u AND NOT {has_replies}", ("@u", u));

                    // private conversations go entirely (their messages cascade).
                    c.Exec("DELETE FROM chat_members WHERE channel_id IN (SELECT id FROM chat_channels WHERE pm_user_a = @u OR pm_user_b = @u)", ("@u", u));
                    c.Exec("DELETE FROM chat_channels WHERE pm_user_a = @u OR pm_user_b = @u", ("@u", u));

                    foreach (var (table, column) in owned)
                        if (table != "comments")
                            c.Exec($"DELETE FROM {table} WHERE {column} = @u", ("@u", u));
                    c.Exec("DELETE FROM friends WHERE target_id = @u", ("@u", u));
                    c.Exec("DELETE FROM blocks WHERE target_id = @u", ("@u", u));
                    c.Exec("UPDATE notifications SET source_user_id = NULL WHERE source_user_id = @u", ("@u", u));

                    // tokens, password resets, challenges and remaining scores cascade from here.
                    c.Exec("DELETE FROM users WHERE id = @u", ("@u", u));
                    tx.Commit();
                }

                accounts.NotifyRevoked(u);
                users.InvalidateTeams();
                users.InvalidateStatistics(u);
                users.InvalidateLeaderboard();

                void remove(string path) { try { File.Delete(path); } catch { /* already gone */ } }
                remove(Path.Combine(db.DataDirectory, "avatars", u.ToString()));
                remove(Path.Combine(db.DataDirectory, "covers", u.ToString()));
                foreach (long id in replays) remove(Path.Combine(scores.ReplayDirectory, $"{id}.osr"));
                foreach (string hash in shots) remove(Path.Combine(db.DataDirectory, "screenshots", hash + ".jpg"));

                return Results.NoContent();
            });
        }
    }
}
