using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services;

public sealed class PasswordRecovery(Db db, IConfiguration config, ILogger<PasswordRecovery> log)
{
    private string from => config["Mail:From"] ?? "noreply@xcsoftworks.com";
    private string publicUrl => config["PublicUrl"] ?? "https://osu.xcsoftworks.com";
    private string sendmail => config["Mail:SendmailPath"] ?? "/usr/sbin/sendmail";
    public bool IsConfigured => (File.Exists(sendmail) || !string.IsNullOrWhiteSpace(config["Mail:Host"]))
                                && MailAddress.TryCreate(from, out _)
                                && Uri.TryCreate(publicUrl, UriKind.Absolute, out var url)
                                && url.Scheme == "https" && string.IsNullOrEmpty(url.UserInfo);

    private static string digest(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task Request(string email)
    {
        using var c = db.Open();
        var user = c.Query("SELECT id, email FROM users WHERE email = $e AND restricted = 0",
            r => (Id: r.GetInt32(0), Email: r.GetString(1)), ("$e", email.Trim())).FirstOrDefault();
        if (user.Id == 0) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string hash = digest(token);
        using (var tx = c.BeginTransaction())
        {
            c.Exec("DELETE FROM password_resets WHERE expires_at <= $n", ("$n", now));
            // One email per account per minute, in addition to the HTTP rate limit.
            if (c.Scalar<long>("SELECT COUNT(*) FROM password_resets WHERE user_id = $u AND expires_at > $n",
                    ("$u", user.Id), ("$n", now + 24 * 60)) > 0) return;
            c.Exec("DELETE FROM password_resets WHERE user_id = $u", ("$u", user.Id));
            c.Exec("INSERT INTO password_resets(token_hash, user_id, expires_at) VALUES ($h, $u, $e)",
                ("$h", hash), ("$u", user.Id), ("$e", now + 25 * 60));
            tx.Commit();
        }
        try
        {
            // A fragment keeps the secret out of server access logs and Referer headers.
            string link = publicUrl.TrimEnd('/') + "/web/home/password-reset#token=" + token;
            using var message = new MailMessage(from, user.Email)
            {
                Subject = "Reset your xc-osu! password / Réinitialiser ton mot de passe",
                Body = "Reset your xc-osu! password using this link (valid for 25 minutes, once only):\n\n" + link
                       + "\n\nRéinitialise ton mot de passe xc-osu! avec ce lien (valable 25 minutes, une seule fois)."
                       + "\n\nIf you did not request this, ignore this email. Your password has not changed."
                       + "\nSi tu n’as pas fait cette demande, ignore cet e-mail. Ton mot de passe n’a pas changé."
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (string.IsNullOrWhiteSpace(config["Mail:Host"]))
            {
                // Same local transport as LCW Sportsbook. Arguments never pass through a shell.
                var start = new ProcessStartInfo(sendmail) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string arg in new[] { "-i", "-t", "-f", new MailAddress(from).Address }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Mail transport unavailable.");
                try
                {
                    string mime = "From: xc-osu! <" + new MailAddress(from).Address + ">\r\nTo: " + new MailAddress(user.Email).Address
                        + "\r\nSubject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Subject)) + "?=\r\n"
                        + "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\nAuto-Submitted: auto-generated\r\n\r\n"
                        + Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Body), Base64FormattingOptions.InsertLineBreaks) + "\r\n";
                    var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.StandardInput.WriteAsync(mime.AsMemory(), timeout.Token);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(timeout.Token);
                    await stderr; // Drain, but never log mail transport output containing recipient information.
                    if (process.ExitCode != 0) throw new InvalidOperationException("Mail transport rejected the message.");
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            else
            {
                using var smtp = new SmtpClient(config["Mail:Host"], config.GetValue("Mail:Port", 587))
                {
                    EnableSsl = true,
                    UseDefaultCredentials = false,
                    Credentials = string.IsNullOrEmpty(config["Mail:Username"]) ? null
                        : new NetworkCredential(config["Mail:Username"], config["Mail:Password"]),
                };
                await smtp.SendMailAsync(message, timeout.Token);
            }
        }
        catch (Exception e) when (e is SmtpException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or FormatException)
        {
            c.Exec("DELETE FROM password_resets WHERE token_hash = $h", ("$h", hash));
            // Do not log the recipient or reset token, or disclose account existence to the caller.
            log.LogError("Password reset email delivery failed ({ErrorType}). Check mail transport configuration.", e.GetType().Name);
        }
    }

    public bool Complete(string token, string password)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return false;
        string hash = digest(token);
        string passwordHash = Accounts.HashPassword(password);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        long? id = c.Scalar<long?>("DELETE FROM password_resets WHERE token_hash = $h AND expires_at > $n RETURNING user_id",
            ("$h", hash), ("$n", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (id == null) return false;
        c.Exec("UPDATE users SET password_hash = $h WHERE id = $u", ("$h", passwordHash), ("$u", id));
        c.Exec("DELETE FROM tokens WHERE user_id = $u", ("$u", id));
        c.Exec("DELETE FROM password_resets WHERE user_id = $u", ("$u", id));
        tx.Commit();
        return true;
    }
}
