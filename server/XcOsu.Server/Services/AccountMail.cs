using System.Security.Cryptography;
using System.Threading.Channels;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services;

/// <summary>
/// Account emails, sent from a background queue so a request never waits on (or reveals anything through) mail delivery.
/// "verify": confirm the address. "exists": someone tried to create an account with an address that already has one (the sign-up
/// form answers the same either way, so this is how the real owner learns of it). Password resets are <see cref="PasswordRecovery"/>.
/// </summary>
public sealed class AccountMail(Db db, IConfiguration config, ILogger<AccountMail> log) : BackgroundService
{
    private readonly Channel<(string Email, string Purpose)> queue = Channel.CreateBounded<(string, string)>(1000);
    public bool Request(string email, string purpose)
    {
        if (config.GetValue<bool?>("AccountMail:Enabled") == false) return true;
        if (purpose is not ("verify" or "exists")) return false;
        if (email.Length > 254 || email.Contains('\r') || email.Contains('\n') || !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)
            return true; // Identical public response for unknown/invalid addresses.
        return queue.Writer.TryWrite((email, purpose));
    }

    /// <summary>Confirms an address with the token of its email. Only "verify" exists.</summary>
    public bool Consume(string token, string purpose)
    {
        if (purpose != "verify" || token.Length != 64 || !token.All(Uri.IsHexDigit)) return false;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        long? user = c.Scalar<long?>("DELETE FROM account_challenges WHERE token_hash = @t AND purpose = 'verify' AND expires_at > @now AND EXISTS (SELECT 1 FROM users WHERE id = account_challenges.user_id AND restricted = 0) RETURNING user_id",
            ("@t", Accounts.TokenHash(token)), ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (user == null) return false;
        c.Exec("UPDATE users SET email_verified = 1 WHERE id = @u", ("@u", user));
        c.Exec("DELETE FROM account_challenges WHERE user_id = @u AND purpose = 'verify'", ("@u", user));
        AuditLog.Write(c, "account.email_verified", (int)user.Value, "user", user);
        tx.Commit();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                string origin = (config["PublicUrl"] ?? "https://osu.xcsoftworks.com").TrimEnd('/');
                string subject, body;
                using (var c = db.Open())
                {
                    long? user = c.Scalar<long?>("SELECT id FROM users WHERE email = @e AND restricted = 0", ("@e", request.Email));
                    if (user == null) continue;
                    if (!AbuseGuard.Allow("account-mail:" + user, 3, TimeSpan.FromHours(1))) continue;
                    if (request.Purpose == "exists")
                    {
                        subject = "xc-osu! - Account sign-up attempt / Tentative d'inscription";
                        body = "xc-osu!\n\nSomeone tried to create a new xc-osu! account with this email address, which already has one."
                             + "\nIf it was you, sign in instead: " + origin + "/web/login"
                             + "\nForgot your password? " + origin + "/web/home/password-reset"
                             + "\n\nQuelqu'un a essayé de créer un compte xc-osu! avec cette adresse, qui en a déjà un."
                             + "\nSi c'était toi, connecte-toi plutôt ; mot de passe oublié : " + origin + "/web/home/password-reset"
                             + "\n\nIf it was not you, you can ignore this message. / Sinon, ignore ce message.";
                    }
                    else
                    {
                        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                        c.Exec("DELETE FROM account_challenges WHERE expires_at < @now", ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                        c.Exec("INSERT INTO account_challenges (token_hash, user_id, purpose, expires_at) VALUES (@t, @u, 'verify', @e)",
                            ("@t", Accounts.TokenHash(token)), ("@u", user), ("@e", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()));
                        // a fragment keeps the token out of server and proxy logs, and out of Referer headers.
                        string link = origin + "/web/home/verify-email#token=" + token;
                        subject = "xc-osu! - Confirm your email / Confirme ton adresse";
                        body = "xc-osu!\n\nConfirm your email address / Confirme ton adresse e-mail :\n" + link
                             + "\n\nThis link expires in 30 minutes. / Ce lien expire dans 30 minutes."
                             + "\nIf you did not create an xc-osu! account, ignore this message. / Ignore ce message si tu n'es pas à l'origine de la demande.";
                    }
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await MailTransport.Send(config, request.Email, subject, body, timeout.Token);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning("Account email delivery failed ({ErrorType})", e.GetType().Name); }
        }
    }
}
