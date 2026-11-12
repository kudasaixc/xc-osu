using XcOsu.Server.Services;

namespace XcOsu.Server.Api;

public static class AccountEmail
{
    public static void Map(WebApplication app)
    {
        // Password resets go through PasswordReset (/api/xc/password-reset); this file only handles address confirmation.
        app.MapPost("/api/xc/account/email/request-verification", (HttpContext ctx, Accounts accounts, AccountMail mail) =>
        {
            if (ctx.User.UserId() is not int u || accounts.Get(u) is not { } row) return Results.Unauthorized();
            if (!AbuseGuard.Allow("mail-verify:" + u, 3, TimeSpan.FromHours(1))) return Results.StatusCode(429);
            return mail.Request(row.Email, "verify") ? Results.Accepted() : Results.StatusCode(503);
        });
        app.MapGet("/api/xc/account/email/{purpose}", (string purpose, string token, HttpContext ctx) =>
        {
            if (purpose is not "verify" || token.Length != 64 || !token.All(Uri.IsHexDigit)) return Results.BadRequest();
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; form-action 'self'; frame-ancestors 'none'";
            string password = purpose == "reset" ? "<label>Nouveau mot de passe <input type=password name=password minlength=8 maxlength=256 required autocomplete=new-password></label>" : "";
            return Results.Content("<!doctype html><html lang=fr><meta charset=utf-8><title>xc-osu! — Compte</title><h1>xc-osu!</h1><form method=post><input type=hidden name=token value=\"" + token + "\">" + password + "<button>Confirmer</button></form></html>", "text/html");
        });
        app.MapPost("/api/xc/account/email/{purpose}", async (string purpose, HttpContext ctx, AccountMail mail) =>
        {
            if (purpose is not "verify") return Results.NotFound();
            var p = await Json.Params(ctx.Request);
            return mail.Consume(p("token") ?? "", purpose, p("password"))
                ? Results.Content("Confirmation réussie. Tu peux te connecter à xc-osu!.", "text/plain; charset=utf-8")
                : Results.BadRequest("Lien expiré ou invalide.");
        });
    }
}
