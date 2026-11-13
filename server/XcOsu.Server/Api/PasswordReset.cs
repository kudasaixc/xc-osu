using XcOsu.Server.Services;

namespace XcOsu.Server.Api;

public static class PasswordReset
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/xc/password-reset", (PasswordRecovery recovery) => Json.Ok(new { available = recovery.IsConfigured }));
        app.MapPost("/api/xc/password-reset/request", async (HttpContext ctx, PasswordRecovery recovery) =>
        {
            if (!recovery.IsConfigured) return Json.Error("Password recovery email is not configured yet. Please contact xc softworks support.", 503);
            var p = await Json.Params(ctx.Request);
            string email = p("email") ?? "";
            if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out _))
                return Json.Error("Please enter a valid email address.", 422);
            await recovery.Request(email);
            return Json.Ok(new { message = "If this email belongs to an account, a reset link will be sent. Check your spam folder too." });
        }).RequireRateLimiting("password-reset").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096));
        app.MapPost("/api/xc/password-reset/complete", async (HttpContext ctx, PasswordRecovery recovery) =>
        {
            var p = await Json.Params(ctx.Request);
            string password = p("password") ?? "";
            if (password.Length is < Accounts.PASSWORD_MIN or > Accounts.PASSWORD_MAX) return Json.Error(Accounts.PASSWORD_RULE, 422);
            return recovery.Complete(p("token") ?? "", password)
                ? Json.Ok(new { message = "Password updated. Sign in again with your new password." })
                : Json.Error("This reset link is invalid or expired. Request a new one.", 422);
        }).RequireRateLimiting("password-reset").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096));
    }
}
