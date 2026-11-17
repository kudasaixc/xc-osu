// xc-osu! server: moderation. Reports, silences, restrictions, removing content, reviewing flagged scores. xc softworks (kudasaixc)

using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;
using XcOsu.Server.Services;

namespace XcOsu.Server.Api;

/// <summary>
/// Staff tools behind <c>/api/xc/admin</c>. A staff account must have two-factor authentication on, and the session must have
/// given its code: a stolen password alone never reaches these. Every action lands in the audit log.
/// </summary>
public static class Admin
{
    private static int? staff(HttpContext ctx, Db db)
    {
        if (ctx.User.UserId() is not int u || !ctx.User.SessionVerified()) return null;
        using var c = db.Open();
        return c.Scalar<long>("SELECT COUNT(*) FROM users WHERE id = @u AND is_admin = 1 AND restricted = 0 AND totp_enabled = 1", ("@u", u)) == 1 ? u : null;
    }

    private static IResult denied(HttpContext ctx, Db db) =>
        ctx.User.UserId() is int u && new Func<bool>(() => { using var c = db.Open(); return c.Scalar<long>("SELECT is_admin FROM users WHERE id = @u", ("@u", u)) == 1; })()
            ? Json.Error("Turn on two-factor authentication in your account settings to use the moderation tools.", 403)
            : Json.Error("Not found", 404);

    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/xc/admin");

        admin.MapGet("/reports", (HttpContext ctx, Db db, Accounts accounts, Users users, string? status) =>
        {
            if (staff(ctx, db) is null) return denied(ctx, db);
            using var c = db.Open();
            var rows = c.Query($"""
                SELECT id, reporter_id, reportable_type, reportable_id, reason, comments, created_at, resolved_at, resolved_by, resolution FROM reports
                WHERE {(status == "resolved" ? "resolved_at IS NOT NULL" : "resolved_at IS NULL")} ORDER BY id DESC LIMIT 200
                """, r => new JObject
            {
                ["id"] = r.GetInt64(0), ["reporter_id"] = r.GetInt32(1), ["type"] = r.GetString(2), ["target_id"] = r.GetInt64(3),
                ["reason"] = r.IsDBNull(4) ? null : r.GetString(4), ["comments"] = r.IsDBNull(5) ? null : r.GetString(5), ["created_at"] = r.GetString(6),
                ["resolved_at"] = r.IsDBNull(7) ? null : r.GetString(7), ["resolved_by"] = r.IsDBNull(8) ? null : r.GetInt32(8), ["resolution"] = r.IsDBNull(9) ? null : r.GetString(9),
            });
            foreach (var row in rows)
            {
                // what the report is about, so a moderator does not have to look it up.
                long target = row.Value<long>("target_id");
                row["target"] = row.Value<string>("type") switch
                {
                    "user" => c.Scalar<string>("SELECT username FROM users WHERE id = @i", ("@i", target)),
                    "comment" => c.Scalar<string>("SELECT message FROM comments WHERE id = @i", ("@i", target)),
                    "message" => c.Scalar<string>("SELECT content FROM chat_messages WHERE id = @i", ("@i", target)),
                    "score" => c.Scalar<string>("SELECT 'user ' || user_id || ', beatmap ' || beatmap_id || ', ' || total_score FROM scores WHERE id = @i", ("@i", target)),
                    _ => null,
                };
                row["target_user_id"] = row.Value<string>("type") switch
                {
                    "user" => target,
                    "comment" => c.Scalar<long?>("SELECT user_id FROM comments WHERE id = @i", ("@i", target)),
                    "message" => c.Scalar<long?>("SELECT sender_id FROM chat_messages WHERE id = @i", ("@i", target)),
                    "score" => c.Scalar<long?>("SELECT user_id FROM scores WHERE id = @i", ("@i", target)),
                    _ => null,
                };
                row["reporter"] = c.Scalar<string>("SELECT username FROM users WHERE id = @i", ("@i", row.Value<int>("reporter_id")));
            }
            return Json.Ok(rows);
        });

        admin.MapPost("/reports/{id:long}/resolve", async (long id, HttpContext ctx, Db db) =>
        {
            if (staff(ctx, db) is not int u) return denied(ctx, db);
            var p = await Json.Params(ctx.Request);
            string resolution = (p("resolution") ?? "").Trim();
            if (resolution.Length is 0 or > 500) return Json.Error("Write what was done (500 characters max).", 422);
            using var c = db.Open();
            if (c.Exec("UPDATE reports SET resolved_at = @n, resolved_by = @u, resolution = @r WHERE id = @i AND resolved_at IS NULL",
                    ("@n", DateTimeOffset.UtcNow.ToString("O")), ("@u", u), ("@r", resolution), ("@i", id)) != 1) return Json.Error("Report not found", 404);
            AuditLog.Write(c, "moderation.report_resolved", u, "report", id, ctx, new JObject { ["resolution"] = resolution });
            return Results.NoContent();
        });

        admin.MapGet("/users/{id:int}", (int id, HttpContext ctx, Db db) =>
        {
            if (staff(ctx, db) is null) return denied(ctx, db);
            using var c = db.Open();
            var user = c.Query("SELECT id, username, email, country, join_date, last_visit, silenced_until, restricted, email_verified, birth_year, is_admin, totp_enabled FROM users WHERE id = @u",
                r => new JObject
                {
                    ["id"] = r.GetInt32(0), ["username"] = r.GetString(1), ["email"] = r.GetString(2), ["country"] = r.GetString(3), ["join_date"] = r.GetString(4),
                    ["last_visit"] = r.IsDBNull(5) ? null : r.GetString(5), ["silenced_until"] = r.IsDBNull(6) ? null : r.GetString(6), ["restricted"] = r.GetInt64(7) == 1,
                    ["email_verified"] = r.GetInt64(8) == 1, ["birth_year"] = r.IsDBNull(9) ? null : r.GetInt64(9), ["is_admin"] = r.GetInt64(10) == 1, ["two_factor"] = r.GetInt64(11) == 1,
                }, ("@u", id)).FirstOrDefault();
            if (user == null) return Json.Error("User not found", 404);
            user["reports_against"] = c.Scalar<long>("SELECT COUNT(*) FROM reports WHERE reportable_type = 'user' AND reportable_id = @u", ("@u", id));
            user["history"] = JArray.Parse(c.Scalar<string>("SELECT COALESCE(json_agg(json_build_object('at', at, 'action', action, 'by', actor_id, 'details', details) ORDER BY id DESC), '[]')::text FROM (SELECT * FROM audit_log WHERE target_type = 'user' AND target_id = @u AND action LIKE 'moderation.%' ORDER BY id DESC LIMIT 50) a", ("@u", id))!);
            user["recent_messages"] = JArray.Parse(c.Scalar<string>("SELECT COALESCE(json_agg(json_build_object('id', id, 'channel_id', channel_id, 'content', content, 'timestamp', timestamp) ORDER BY id DESC), '[]')::text FROM (SELECT * FROM chat_messages WHERE sender_id = @u ORDER BY id DESC LIMIT 30) m", ("@u", id))!);
            return Json.Ok(user);
        });

        admin.MapPost("/users/{id:int}/silence", async (int id, HttpContext ctx, Db db, Accounts accounts) =>
        {
            if (staff(ctx, db) is not int u) return denied(ctx, db);
            var p = await Json.Params(ctx.Request);
            if (!int.TryParse(p("minutes"), out int minutes) || minutes is < 0 or > 525_600) return Json.Error("Duration: 0 (lift) to 525600 minutes.", 422);
            string reason = (p("reason") ?? "").Trim();
            if (minutes > 0 && reason.Length is 0 or > 300) return Json.Error("Give a reason (300 characters max).", 422);
            using var c = db.Open();
            if (c.Exec("UPDATE users SET silenced_until = @t WHERE id = @u AND is_admin = 0", ("@t", minutes == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(minutes).ToString("O")), ("@u", id)) != 1)
                return Json.Error("User not found (staff cannot be silenced here).", 404);
            AuditLog.Write(c, minutes == 0 ? "moderation.silence_lifted" : "moderation.silenced", u, "user", id, ctx, new JObject { ["minutes"] = minutes, ["reason"] = reason });
            return Results.NoContent();
        });

        admin.MapPost("/users/{id:int}/restrict", async (int id, HttpContext ctx, Db db, Accounts accounts, Users users) =>
        {
            if (staff(ctx, db) is not int u) return denied(ctx, db);
            var p = await Json.Params(ctx.Request);
            bool restrict = p("restricted") != "false";
            string reason = (p("reason") ?? "").Trim();
            if (reason.Length is 0 or > 300) return Json.Error("Give a reason (300 characters max).", 422);
            using (var c = db.Open())
            {
                if (c.Exec("UPDATE users SET restricted = @r WHERE id = @u AND is_admin = 0", ("@r", restrict ? 1 : 0), ("@u", id)) != 1)
                    return Json.Error("User not found (staff cannot be restricted here).", 404);
                AuditLog.Write(c, restrict ? "moderation.restricted" : "moderation.unrestricted", u, "user", id, ctx, new JObject { ["reason"] = reason });
            }
            if (restrict) accounts.RevokeAll(id);
            users.InvalidateLeaderboard();
            users.InvalidateStatistics(id);
            return Results.NoContent();
        });

        admin.MapDelete("/comments/{id:long}", (long id, HttpContext ctx, Db db) =>
        {
            if (staff(ctx, db) is not int u) return denied(ctx, db);
            using var c = db.Open();
            if (c.Exec("UPDATE comments SET deleted_at = @n WHERE id = @i AND deleted_at IS NULL", ("@n", DateTimeOffset.UtcNow.ToString("O")), ("@i", id)) != 1) return Json.Error("Comment not found", 404);
            AuditLog.Write(c, "moderation.comment_removed", u, "comment", id, ctx);
            return Results.NoContent();
        });

        admin.MapDelete("/chat/messages/{id:long}", (long id, HttpContext ctx, Db db) =>
        {
            if (staff(ctx, db) is not int u) return denied(ctx, db);
            using var c = db.Open();
            long? sender = c.Scalar<long?>("DELETE FROM chat_messages WHERE id = @i RETURNING sender_id", ("@i", id));
            if (sender == null) return Json.Error("Message not found", 404);
            AuditLog.Write(c, "moderation.message_removed", u, "user", sender, ctx, new JObject { ["message_id"] = id });
            return Results.NoContent();
        });

        // ---- anti-cheat review queue: scores the automatic checks want a person to look at
        admin.MapGet("/scores/flagged", (HttpContext ctx, Db db) =>
        {
            if (staff(ctx, db) is null) return denied(ctx, db);
            using var c = db.Open();
            return Json.Ok(c.Query("""
                SELECT s.id, s.user_id, u.username, s.beatmap_id, s.total_score, s.accuracy, s.max_combo, s.pp, s.pp_pending, s.ranked, s.verification, s.flag_reason, s.ended_at, s.has_replay
                FROM scores s JOIN users u ON u.id = s.user_id WHERE s.flag_reason IS NOT NULL ORDER BY s.id DESC LIMIT 200
                """, r => new JObject
            {
                ["id"] = r.GetInt64(0), ["user_id"] = r.GetInt32(1), ["username"] = r.GetString(2), ["beatmap_id"] = r.GetInt32(3), ["total_score"] = r.GetInt64(4),
                ["accuracy"] = r.GetDouble(5), ["max_combo"] = r.GetInt32(6), ["pp"] = r.IsDBNull(7) ? null : r.GetDouble(7), ["pp_pending"] = r.IsDBNull(8) ? null : r.GetDouble(8),
                ["ranked"] = r.GetInt64(9) == 1, ["verification"] = r.GetInt32(10), ["reason"] = r.GetString(11), ["ended_at"] = r.GetString(12), ["has_replay"] = r.GetInt64(13) == 1,
            }));
        });

        admin.MapPost("/scores/{id:long}/review", async (long id, HttpContext ctx, Db db, Scores scores) =>
        {
            if (staff(ctx, db) is not int u) return denied(ctx, db);
            var p = await Json.Params(ctx.Request);
            string decision = p("decision") ?? "";
            if (decision is not ("approve" or "unrank")) return Json.Error("decision: approve or unrank", 422);
            if (!scores.Review(id, decision == "approve")) return Json.Error("Score not found", 404);
            using (var c = db.Open()) AuditLog.Write(c, "moderation.score_" + decision, u, "score", id, ctx);
            return Results.NoContent();
        });

        admin.MapGet("/audit", (HttpContext ctx, Db db, long? before) =>
        {
            if (staff(ctx, db) is null) return denied(ctx, db);
            using var c = db.Open();
            return Json.Ok(c.Query("SELECT id, at, action, actor_id, target_type, target_id, details FROM audit_log WHERE (@b::bigint IS NULL OR id < @b) ORDER BY id DESC LIMIT 200",
                r => new JObject
                {
                    ["id"] = r.GetInt64(0), ["at"] = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)), ["action"] = r.GetString(2), ["actor_id"] = r.IsDBNull(3) ? null : r.GetInt64(3),
                    ["target_type"] = r.IsDBNull(4) ? null : r.GetString(4), ["target_id"] = r.IsDBNull(5) ? null : r.GetInt64(5), ["details"] = r.IsDBNull(6) ? null : JToken.Parse(r.GetString(6)),
                }, ("@b", before)));
        });
    }
}
