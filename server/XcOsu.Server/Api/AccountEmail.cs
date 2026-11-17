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
        // the email links to /web/home/verify-email#token=…; that page posts the token here (it never travels in an address).
        app.MapPost("/api/xc/account/email/verify", async (HttpContext ctx, AccountMail mail) =>
        {
            var p = await Json.Params(ctx.Request);
            ctx.Response.Headers.CacheControl = "no-store";
            return mail.Consume(p("token") ?? "", "verify")
                ? Json.Ok(new { message = "Email address confirmed." })
                : Json.Error("This link is invalid or expired. Request a new one from your account settings.", 422);
        });
    }
}
