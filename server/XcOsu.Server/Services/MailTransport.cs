// xc-osu! server: one way out for every account email. xc softworks (kudasaixc)

using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace XcOsu.Server.Services;

/// <summary>
/// Order of preference: an explicit SMTP relay (Mail:Host), an explicit sendmail binary (Mail:SendmailPath), else the machine's own
/// MTA over loopback SMTP. The default is deliberately not the sendmail binary: the service runs with NoNewPrivileges, under which
/// Postfix's setgid postdrop can never write to the queue, so every message silently went nowhere.
/// Callers must not log exception messages: they can contain the recipient.
/// </summary>
public static class MailTransport
{
    public static string From(IConfiguration config) => config["Mail:From"] ?? "noreply@xcsoftworks.com";

    public static bool IsConfigured(IConfiguration config)
        => MailAddress.TryCreate(From(config), out _)
           && (!string.IsNullOrWhiteSpace(config["Mail:Host"]) || config["Mail:SendmailPath"] is not { Length: > 0 } path || File.Exists(path));

    public static async Task Send(IConfiguration config, string to, string subject, string body, CancellationToken ct)
    {
        string from = new MailAddress(From(config)).Address;
        string recipient = new MailAddress(to).Address;
        if (recipient.Contains('\r') || recipient.Contains('\n')) throw new FormatException("Invalid recipient.");

        if (string.IsNullOrWhiteSpace(config["Mail:Host"]) && config["Mail:SendmailPath"] is { Length: > 0 } sendmail)
        {
            // Arguments never pass through a shell.
            var start = new ProcessStartInfo(sendmail) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in new[] { "-i", "-t", "-f", from }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Mail transport unavailable.");
            try
            {
                string mime = "From: xc-osu! <" + from + ">\r\nTo: " + recipient
                    + "\r\nSubject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(subject)) + "?=\r\n"
                    + "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\nAuto-Submitted: auto-generated\r\n\r\n"
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes(body), Base64FormattingOptions.InsertLineBreaks) + "\r\n";
                var stderr = process.StandardError.ReadToEndAsync(ct);
                await process.StandardInput.WriteAsync(mime.AsMemory(), ct);
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct);
                await stderr; // drained, never logged: it names the recipient.
                if (process.ExitCode != 0) throw new InvalidOperationException("Mail transport rejected the message.");
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            return;
        }

        using var message = new MailMessage(new MailAddress(from, "xc-osu!"), new MailAddress(recipient))
        {
            Subject = subject, SubjectEncoding = Encoding.UTF8, Body = body, BodyEncoding = Encoding.UTF8,
        };
        message.Headers.Add("Auto-Submitted", "auto-generated");
        bool relay = !string.IsNullOrWhiteSpace(config["Mail:Host"]);
        using var smtp = relay
            ? new SmtpClient(config["Mail:Host"], config.GetValue("Mail:Port", 587))
            {
                EnableSsl = true,
                UseDefaultCredentials = false,
                Credentials = string.IsNullOrEmpty(config["Mail:Username"]) ? null : new NetworkCredential(config["Mail:Username"], config["Mail:Password"]),
            }
            // the local MTA only listens on loopback and signs what it relays; there is no network hop to protect.
            : new SmtpClient("127.0.0.1", config.GetValue("Mail:LocalPort", 25)) { EnableSsl = false, UseDefaultCredentials = false };
        await smtp.SendMailAsync(message, ct);
    }
}
