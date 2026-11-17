// xc-osu! server: the player's own sessions and two-factor authentication. xc softworks (kudasaixc)

using XcOsu.Server.Data;
using XcOsu.Server.Services;

namespace XcOsu.Server.Api;

public static class AccountSecurity
{
    public static void Map(WebApplication app)
    {
        // ---- sessions: every signed-in device or browser, and a way to end any of them
        app.MapGet("/api/xc/account/sessions", (HttpContext ctx, Accounts accounts) =>
        {
            if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
            string? current = ctx.User.SessionId();
            return Json.Ok(accounts.Sessions(u).Select(s => new
            {
                // the id is a handle for revocation only; it is not a credential.
                id = s.Id, created_at = s.CreatedAt == 0 ? null : (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(s.CreatedAt),
                expires_at = DateTimeOffset.FromUnixTimeSeconds(s.ExpiresAt), current = s.Id == current, verified = s.Verified,
            }));
        });

        app.MapDelete("/api/xc/account/sessions/{id}", (string id, HttpContext ctx, Accounts accounts, AuditLog audit) =>
        {
            if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
            if (!accounts.RevokeSession(u, id)) return Json.Error("Session not found", 404);
            audit.Write("session.revoked", u, "user", u, ctx);
            return Results.NoContent();
        });

        app.MapPost("/api/xc/account/sessions/revoke-others", (HttpContext ctx, Accounts accounts, AuditLog audit) =>
        {
            if (ctx.User.UserId() is not int u || ctx.User.SessionId() is not string current) return Json.Error("unauthorized", 401);
            foreach (var s in accounts.Sessions(u).Where(s => s.Id != current)) accounts.RevokeSession(u, s.Id);
            audit.Write("session.revoked_others", u, "user", u, ctx);
            return Results.NoContent();
        });

        // ---- two-factor authentication (an authenticator app; the game already has the screen that asks for its code)
        app.MapPost("/api/xc/account/2fa/setup", async (HttpContext ctx, Accounts accounts, Db db) =>
        {
            if (ctx.User.UserId() is not int u || accounts.Get(u) is not AccountRow row) return Json.Error("unauthorized", 401);
            var p = await Json.Params(ctx.Request);
            if (!Accounts.VerifyPassword(p("password") ?? "", row.PasswordHash)) return Json.Error("Password is incorrect.", 403);
            if (accounts.TwoFactorEnabled(u)) return Json.Error("Two-factor authentication is already on.", 422);
            string secret = Totp.NewSecret();
            using (var c = db.Open()) c.Exec("UPDATE users SET totp_secret = @s, totp_enabled = 0 WHERE id = @u", ("@s", secret), ("@u", u));
            ctx.Response.Headers.CacheControl = "no-store";
            return Json.Ok(new { secret, uri = Totp.Uri(secret, row.Username) });
        });

        app.MapPost("/api/xc/account/2fa/enable", async (HttpContext ctx, Accounts accounts, Db db, AuditLog audit) =>
        {
            if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
            var p = await Json.Params(ctx.Request);
            using var c = db.Open();
            var row = c.Query("SELECT totp_secret, totp_last_step FROM users WHERE id = @u AND totp_enabled = 0 AND totp_secret IS NOT NULL",
                r => (secret: r.GetString(0), last: r.GetInt64(1)), ("@u", u)).FirstOrDefault();
            if (row.secret == null) return Json.Error("Start the setup first.", 422);
            if (Totp.Verify(row.secret, p("code"), row.last) is not long step) return Json.Error("Incorrect code. Check the time on your phone and try again.", 422);
            c.Exec("UPDATE users SET totp_enabled = 1, totp_last_step = @s WHERE id = @u", ("@s", step), ("@u", u));
            // the session that turned it on has just proven the factor; every other session must sign in again.
            if (ctx.User.SessionId() is string current)
                c.Exec("DELETE FROM tokens WHERE user_id = @u AND session_id <> @s", ("@u", u), ("@s", current));
            AuditLog.Write(c, "account.two_factor_enabled", u, "user", u, ctx);
            c.Dispose();
            accounts.NotifyRevoked(u);
            return Results.NoContent();
        });

        app.MapPost("/api/xc/account/2fa/disable", async (HttpContext ctx, Accounts accounts, Db db) =>
        {
            if (ctx.User.UserId() is not int u || accounts.Get(u) is not AccountRow row) return Json.Error("unauthorized", 401);
            var p = await Json.Params(ctx.Request);
            if (!Accounts.VerifyPassword(p("password") ?? "", row.PasswordHash)) return Json.Error("Password is incorrect.", 403);
            using var c = db.Open();
            var totp = c.Query("SELECT totp_secret, totp_last_step FROM users WHERE id = @u AND totp_enabled = 1", r => (secret: r.GetString(0), last: r.GetInt64(1)), ("@u", u)).FirstOrDefault();
            if (totp.secret == null) return Results.NoContent();
            if (Totp.Verify(totp.secret, p("code"), totp.last) == null) return Json.Error("Incorrect code.", 422);
            c.Exec("UPDATE users SET totp_enabled = 0, totp_secret = NULL WHERE id = @u", ("@u", u));
            AuditLog.Write(c, "account.two_factor_disabled", u, "user", u, ctx);
            return Results.NoContent();
        });
    }
}
