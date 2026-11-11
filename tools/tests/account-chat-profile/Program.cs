using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using osu.Game.Graphics.Containers.Markdown;
using osu.Game.Graphics.Containers.Markdown.BBCode;
using osu.Game.Online.Chat;
using XcOsu.Server.Data;
using XcOsu.Server.Services;

static void check(bool value, string message) { if (!value) throw new Exception(message); }

// Force a timeout in the exact method from the production stack trace.
var handler = typeof(MessageFormatter).GetMethod("handleAdvanced", BindingFlags.NonPublic | BindingFlags.Static)!;
string hostile = new string('a', 20000) + "!";
var text = new MessageFormatter.MessageFormatterResult(hostile);
var regex = new Regex("(?<link>(a+)+)$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
bool timedOut = false;
try { _ = regex.Matches(hostile).Count; } catch (RegexMatchTimeoutException) { timedOut = true; }
check(timedOut, "The regression input must actually trigger the timeout");
handler.Invoke(null, new object[] { regex, text, 0 });
check(text.Text == hostile && text.Links.Count == 0, "A regex timeout must preserve chat text");
var normal = MessageFormatter.FormatText("Hello https://example.com/path");
check(normal.Links.Count == 1, "Normal chat links must still work");
Console.WriteLine("PASS chat: forced regex timeout is handled, text preserved, ordinary links retained");

var pipeline = new OsuMarkdownContainerOptions { BbCode = true }.BuildPipeline();
foreach (string tag in new[] { "b", "i", "u", "s", "color=#ff66aa", "size=24", "url=https://example.com", "img", "code", "quote", "list" })
{
    string name = tag.Split('=')[0];
    var document = Markdown.Parse($"[{tag}]hello[/{name}]", pipeline);
    check(document.OfType<ParagraphBlock>().Single().Inline!.FirstChild is BbCodeInline bb && bb.Tag == name, "BBCode parser: " + tag);
}
check(BbCodeInlineParser.TryRead("[b]one [b]two[/b] three[/b]", 0, 27, out var nested, out _) && nested.Content == "one [b]two[/b] three", "Nested same-name tags");
check(!BbCodeInlineParser.TryRead("[b]unclosed", 0, 11, out _, out _), "Unclosed tag must remain plain text");
check(!BbCodeInlineParser.TryRead("[script]bad[/script]", 0, 20, out _, out _), "Unknown tag must remain plain text");
check(Markdown.Parse("**existing Markdown**", pipeline).OfType<ParagraphBlock>().Single().Inline!.FirstChild is EmphasisInline, "Markdown compatibility");
var multiline = Markdown.Parse(BbCodeInlineParser.Prepare("[b]first\n\nsecond[/b]"), pipeline);
check(multiline.OfType<ParagraphBlock>().Single().Inline!.FirstChild is BbCodeInline, "BBCode across blank lines");
Console.WriteLine("PASS profile: BBCode styles, nesting, unknown/unclosed tags, Markdown compatibility");

if (!OperatingSystem.IsLinux()) { Console.WriteLine("Mail capture test requires Linux; skipped"); return; }
string directory = Path.Combine(Path.GetTempPath(), "xc-recovery-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string capture = Path.Combine(directory, "message.eml");
    string transport = Path.Combine(directory, "sendmail");
    await File.WriteAllTextAsync(transport, "#!/bin/sh\ncat > '" + capture + "'\n");
    File.SetUnixFileMode(transport, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["DataDirectory"] = directory, ["Mail:SendmailPath"] = transport, ["PublicUrl"] = "https://osu.test"
    }).Build();
    var db = new Db(config);
    var accounts = new Accounts(db);
    check(accounts.Register("ResetUser", "reset@example.test", "old-password", "FR") == null, "Registration");
    int id = accounts.Verify("ResetUser", "old-password")!.Id;
    var oldTokens = accounts.IssueToken(id);
    var recovery = new PasswordRecovery(db, config, NullLogger<PasswordRecovery>.Instance);
    check(recovery.IsConfigured, "Capture transport configured");
    await recovery.Request("nobody@example.test");
    check(!File.Exists(capture), "Unknown account must not receive mail");
    await recovery.Request("reset@example.test");
    string firstMail = await File.ReadAllTextAsync(capture);
    string body = Encoding.UTF8.GetString(Convert.FromBase64String(firstMail.Split("\r\n\r\n", 2)[1]));
    string token = Regex.Match(body, "#token=([A-F0-9]{64})").Groups[1].Value;
    check(token.Length == 64 && body.Contains("https://osu.test/web/home/password-reset#token="), "Mail contains trusted URL and token");
    using (var c = db.Open()) check(c.Scalar<long>("SELECT COUNT(*) FROM password_resets WHERE token_hash = $t", ("$t", token)) == 0, "Raw token must not be stored");
    await recovery.Request("reset@example.test");
    check(await File.ReadAllTextAsync(capture) == firstMail, "Account cooldown");
    check(!recovery.Complete(new string('F', 64), "new-password"), "Invalid token rejected");
    check(recovery.Complete(token, "new-password"), "Valid reset");
    check(!recovery.Complete(token, "another-password"), "Token cannot be reused");
    check(accounts.Verify("ResetUser", "old-password") == null && accounts.Verify("ResetUser", "new-password") != null, "Password actually changed");
    check(accounts.ValidateAccessToken(oldTokens.access) == null && accounts.Refresh(oldTokens.refresh) == null, "Sessions revoked");
    await recovery.Request("reset@example.test");
    body = Encoding.UTF8.GetString(Convert.FromBase64String((await File.ReadAllTextAsync(capture)).Split("\r\n\r\n", 2)[1]));
    token = Regex.Match(body, "#token=([A-F0-9]{64})").Groups[1].Value;
    using (var c = db.Open()) c.Exec("UPDATE password_resets SET expires_at = 0");
    check(!recovery.Complete(token, "expired-password"), "Expired link rejected");
    await recovery.Request("reset@example.test");
    body = Encoding.UTF8.GetString(Convert.FromBase64String((await File.ReadAllTextAsync(capture)).Split("\r\n\r\n", 2)[1]));
    token = Regex.Match(body, "#token=([A-F0-9]{64})").Groups[1].Value;
    var concurrent = await Task.WhenAll(Task.Run(() => recovery.Complete(token, "concurrent-one")), Task.Run(() => recovery.Complete(token, "concurrent-two")));
    check(concurrent.Count(x => x) == 1, "Only one concurrent reset can consume the token");
    await File.WriteAllTextAsync(transport, "#!/bin/sh\nexit 1\n");
    await recovery.Request("reset@example.test");
    using (var c = db.Open()) check(c.Scalar<long>("SELECT COUNT(*) FROM password_resets") == 0, "Failed delivery removes unusable reset token");
    Console.WriteLine("PASS recovery: captured mail, unknown email, hash storage, cooldown, invalid/expired/reused tokens, password change, session revocation, concurrent use, transport failure");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}
