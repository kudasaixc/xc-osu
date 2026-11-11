using System.Reflection;
using Microsoft.AspNetCore.SignalR.Client;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.Containers;
using osu.Game.Online;
using osu.Game.Online.Metadata;
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

// A page is finalised one tree per update, and a superseded page cannot attach old trees.
using (var queue = new FrameStagedDrawableQueue<TrackedBox>())
{
    var first = new TrackedBox(); var second = new TrackedBox(); var discarded = new TrackedBox();
    var attached = new List<TrackedBox>();
    queue.Replace(new[] { first, second, discarded });
    queue.AttachNext(attached.Add);
    check(attached.SequenceEqual(new[] { first }) && queue.Count == 2, "One tree attached per frame");
    queue.AttachNext(attached.Add);
    check(attached.SequenceEqual(new[] { first, second }), "Panel order preserved");
    var replacement = new TrackedBox();
    queue.Replace(new[] { replacement });
    check(discarded.WasDisposed && !first.WasDisposed, "Only unattached superseded panels disposed");
    queue.AttachNext(attached.Add);
    check(attached.Last() == replacement && queue.Count == 0, "No stale panel after replacement");
    queue.AttachNext(_ => throw new Exception("Empty queue must do nothing"));
    first.Dispose(); second.Dispose(); replacement.Dispose();
}
Console.WriteLine("PASS UI staging: one tree per frame, ordering, cancellation, ownership and replacement");

var metadata = new OnlineMetadataClient(new EndpointConfiguration());
await metadata.RefreshFriends();
check(metadata.GetChangesSince(0).IsCanceled, "Disconnected metadata catch-up returns a cancelled task");
var disposedHub = new HubConnectionBuilder().WithUrl("https://unused.test/metadata").Build();
await disposedHub.DisposeAsync();
typeof(OnlineMetadataClient).GetField("connector", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(metadata, new DisposedHubConnector(disposedHub));
await metadata.RefreshFriends();
Console.WriteLine("PASS metadata: disconnected and disposed SignalR connections do not surface logout errors");

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
foreach (string tag in new[] { "b", "i", "u", "s", "color=#ff66aa", "size=24", "url=https://example.com", "img", "code", "quote", "list", "centre", "center", "box=GOAL 1", "spoiler" })
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
const string screenshotPage = "[centre]\n[heading]CURRENT GOALS[/heading]\n\n[box=GOAL 1 — SHORT-TERM]\n[centre][s]Reach 5 digits[/s]\n\n[b]ACHIEVED![/b]\n[The Big Black](https://example.com/map)[/centre]\n[/box]\n[/centre]";
var screenshotDocument = Markdown.Parse(BbCodeInlineParser.Prepare(screenshotPage), pipeline);
check(screenshotDocument.OfType<ParagraphBlock>().Single().Inline!.FirstChild is BbCodeInline { Tag: "centre" }, "Screenshot multiline block remains whole");
using (var centred = new ProfileBbCodeBlock(new BbCodeInline("centre", "", "[b]CURRENT GOALS[/b]")))
{
    check(centred.IsCentred && centred.Body.CreateTextFlow().TextAnchor == osu.Framework.Graphics.Anchor.TopCentre, "Centre block uses centred text layout");
    check(centred.Header == null, "Centred block has no spoiler header");
}
using (var box = new ProfileBbCodeBlock(new BbCodeInline("box", "GOAL 1 — SHORT-TERM", "[centre][b]ACHIEVED![/b][/centre]")))
{
    check(!box.IsCentred && box.Header != null && box.Body.Alpha == 0, "Box has a collapsed clickable header");
    box.Header!.Action();
    check(box.Body.Alpha == 1 && box.Body.Text.Contains("[centre]"), "Opening box preserves nested content");
    box.Header.Action();
    check(box.Body.Alpha == 0, "Box closes again");
}
ProfileLayoutGame.Verify(screenshotPage);
Console.WriteLine("PASS profile: inline styles, centre/center, multiline boxes, nested blocks, toggle, Markdown compatibility");

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

sealed class DisposedHubConnector(HubConnection hub) : IHubClientConnector
{
    public HubConnection CurrentConnection => hub;
    public IBindable<bool> IsConnected { get; } = new BindableBool(true);
    public Action<HubConnection>? ConfigureConnection { get; set; }
    public Task Disconnect() => Task.CompletedTask;
    public Task Reconnect() => Task.CompletedTask;
    public void Dispose() { }
}

partial class TrackedBox : Box
{
    public bool WasDisposed => IsDisposed;
}
