using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services;

// Account emails use the same local sendmail transport as sportsbook.
public sealed class AccountMail(Db db, IConfiguration config, ILogger<AccountMail> log) : BackgroundService
{
    private readonly Channel<(string Email, string Purpose)> queue = Channel.CreateBounded<(string, string)>(100);
    public bool Request(string email, string purpose)
    {
        if (config.GetValue<bool?>("AccountMail:Enabled") == false) return true;
        if (email.Length > 254 || email.Contains('\r') || email.Contains('\n') || !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)
            return true; // Identical public response for unknown/invalid addresses.
        return queue.Writer.TryWrite((email, purpose));
    }

    public bool Consume(string token, string purpose, string? password = null)
    {
        if (purpose is not ("verify" or "reset") || token.Length != 64 || !token.All(Uri.IsHexDigit) || (purpose == "reset" && (password == null || password.Length is < 8 or > 256))) return false;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        long? user = c.Scalar<long?>("DELETE FROM account_challenges WHERE token_hash = $t AND purpose = $p AND expires_at > $now AND EXISTS (SELECT 1 FROM users WHERE id = account_challenges.user_id AND restricted = 0 AND ($p = 'verify' OR email_verified = 1)) RETURNING user_id",
            ("$t", Accounts.TokenHash(token)), ("$p", purpose), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (user == null) return false;
        if (purpose == "verify")
            c.Exec("UPDATE users SET email_verified = 1 WHERE id = $u", ("$u", user));
        else
        {
            string hash = Accounts.HashPassword(password!);
            c.Exec("UPDATE users SET password_hash = $h WHERE id = $u AND email_verified = 1", ("$h", hash), ("$u", user));
            c.Exec("DELETE FROM tokens WHERE user_id = $u", ("$u", user));
        }
        c.Exec("DELETE FROM account_challenges WHERE user_id = $u AND purpose = $p", ("$u", user), ("$p", purpose));
        tx.Commit();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var c = db.Open();
                long? user = c.Scalar<long?>("SELECT id FROM users WHERE email = $e AND restricted = 0 AND ($p = 'verify' OR email_verified = 1)",
                    ("$e", request.Email), ("$p", request.Purpose));
                if (user == null) continue;
                if (!AbuseGuard.Allow("account-mail:" + user, 3, TimeSpan.FromHours(1))) continue;
                string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                c.Exec("DELETE FROM account_challenges WHERE expires_at < $now", ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                c.Exec("INSERT INTO account_challenges (token_hash, user_id, purpose, expires_at) VALUES ($t, $u, $p, $e)",
                    ("$t", Accounts.TokenHash(token)), ("$u", user), ("$p", request.Purpose), ("$e", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()));
                string origin = (config["PublicUrl"] ?? "https://osu.xcsoftworks.com").TrimEnd('/');
                string link = origin + "/api/xc/account/email/" + request.Purpose + "?token=" + token;
                string body = "xc-osu!\n\n" + (request.Purpose == "verify" ? "Confirme ton adresse email :" : "Reinitialise ton mot de passe :") + "\n" + link
                    + "\n\nCe lien expire dans 30 minutes. Ignore ce message si tu n'es pas a l'origine de la demande.";
                var start = new ProcessStartInfo("/usr/sbin/sendmail") { RedirectStandardInput = true, UseShellExecute = false };
                start.ArgumentList.Add("-t"); start.ArgumentList.Add("-i"); start.ArgumentList.Add("-fnoreply@xcsoftworks.com");
                using var process = Process.Start(start) ?? throw new IOException("Mail transport unavailable");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    await process.StandardInput.WriteAsync(("From: xc-osu! <noreply@xcsoftworks.com>\nTo: " + request.Email
                        + "\nSubject: xc-osu! - Compte\nMIME-Version: 1.0\nContent-Type: text/plain; charset=utf-8\nContent-Transfer-Encoding: base64\n\n"
                        + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(body))).AsMemory(), timeout.Token);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(timeout.Token);
                    if (process.ExitCode != 0) log.LogWarning("Account email delivery failed");
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { log.LogWarning("Account email processing failed"); }
        }
    }
}
