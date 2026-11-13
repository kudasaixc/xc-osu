// xc-osu! server — accounts and OAuth tokens. xc softworks (kudasaixc)

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed record AccountRow(int Id, string Username, string Email, string PasswordHash, string Country, DateTimeOffset JoinDate, bool IsAdmin, string? Title, string Playmode,
                                    int AvatarVersion, int CoverVersion, DateTimeOffset? SilencedUntil, bool Restricted, string PreviousUsernames, string ProfileJson,
                                    int? RankHighest, DateTimeOffset? RankHighestAt, DateTimeOffset? LastVisit);

    public sealed partial class Accounts
    {
        public const int TOKEN_LIFETIME_SECONDS = 60 * 60 * 24;

        /// <summary>The one password length policy: registration, change, reset and the web forms all use these bounds.</summary>
        public const int PASSWORD_MIN = 8, PASSWORD_MAX = 256;
        public static readonly string PASSWORD_RULE = $"Use a password between {PASSWORD_MIN} and {PASSWORD_MAX} characters.";

        private readonly Db db;
        private static readonly SemaphoreSlim passwordSlots = new(4, 4);

        public Accounts(Db db) => this.db = db;

        public static AccountRow Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new AccountRow(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            DateTimeOffset.Parse(r.GetString(5)), r.GetInt32(6) != 0, r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8),
            r.GetInt32(9), r.GetInt32(10), r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)), r.GetInt32(12) != 0, r.GetString(13), r.GetString(14),
            r.IsDBNull(15) ? null : r.GetInt32(15), r.IsDBNull(16) ? null : DateTimeOffset.Parse(r.GetString(16)), r.IsDBNull(17) ? null : DateTimeOffset.Parse(r.GetString(17)));

        public const string COLUMNS = "id, username, email, password_hash, country, join_date, is_admin, title, playmode, avatar_version, cover_version, silenced_until, restricted, "
                                      + "previous_usernames, profile_json, rank_highest, rank_highest_at, last_visit";

        public AccountRow? Get(int id)
        {
            using var c = db.Open();
            return c.Query($"SELECT {COLUMNS} FROM users WHERE id = $id", Map, ("$id", id)).FirstOrDefault();
        }

        public AccountRow? GetByName(string name)
        {
            using var c = db.Open();
            return c.Query($"SELECT {COLUMNS} FROM users WHERE username = $n", Map, ("$n", name)).FirstOrDefault();
        }

        public List<AccountRow> GetMany(IEnumerable<int> ids)
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return new List<AccountRow>();
            using var c = db.Open();
            return c.Query($"SELECT {COLUMNS} FROM users WHERE id IN ({string.Join(',', list)})", Map);
        }

        [GeneratedRegex(@"^[A-Za-z0-9\[\]_\- ]{3,15}$")]
        private static partial Regex usernameRegex();

        /// <summary>Returns per-field errors in osu-web's form_error shape, or null on success.</summary>
        public Dictionary<string, string[]>? Register(string username, string email, string password, string country)
        {
            var errors = new Dictionary<string, List<string>>();
            void add(string f, string m) { if (!errors.TryGetValue(f, out var l)) errors[f] = l = new List<string>(); l.Add(m); }

            username = username.Trim();
            if (!usernameRegex().IsMatch(username) || username.Contains("  ") || username.StartsWith(' ') || username.EndsWith(' '))
                add("username", "Username must be 3-15 characters: letters, numbers, spaces, [ ] _ -");
            if (email.Length > 254 || email.Contains('\r') || email.Contains('\n') || !System.Net.Mail.MailAddress.TryCreate(email, out var parsedEmail) || parsedEmail.Address != email)
                add("user_email", "Please enter a valid email address.");
            if (password.Length is < PASSWORD_MIN or > PASSWORD_MAX)
                add("password", PASSWORD_RULE);
            else if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
                add("password", "Password may not contain username.");

            using var c = db.Open();
            if (c.Scalar<long>("SELECT COUNT(*) FROM users WHERE username = $u", ("$u", username)) > 0)
                add("username", "Username is already in use!");
            if (c.Scalar<long>("SELECT COUNT(*) FROM users WHERE email = $e", ("$e", email)) > 0)
                add("user_email", "Registration cannot be completed with these details.");

            if (errors.Count > 0)
                return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());

            c.Exec("INSERT INTO users (username, email, password_hash, country, join_date, is_admin, verification_required) VALUES ($u, $e, $p, $c, $d, $a, 1)",
                ("$u", username), ("$e", email), ("$p", HashPassword(password)), ("$c", country), ("$d", DateTimeOffset.UtcNow.ToString("O")), ("$a", 0));
            return null;
        }

        public AccountRow? Verify(string username, string password)
        {
            if (username.Length > 254 || password.Length > PASSWORD_MAX) return null;
            string accountKey = TokenHash(username.Trim().ToUpperInvariant());
            if (!LoginAttempts.CanTry(accountKey) || !AbuseGuard.Allow("login-account:" + accountKey, 10, TimeSpan.FromMinutes(1))) return null;
            using var c = db.Open();
            var user = c.Query($"SELECT {COLUMNS} FROM users WHERE username = $n OR email = $n", Map, ("$n", username.Trim())).FirstOrDefault();
            bool valid = user != null && !user.Restricted && VerifyPassword(password, user.PasswordHash);
            LoginAttempts.Record(accountKey, valid);
            return valid ? user : null;
        }

        public const string SESSION_CLAIM = "xc:session";

        /// <summary>Raised after sessions were removed. Argument: the affected account, or null when unknown (re-check everyone).</summary>
        public event Action<int?>? SessionsRevoked;

        private long revocationEpoch;

        /// <summary>Drop cached session checks and tell live connections to re-validate. Call after any token deletion or restriction.</summary>
        public void NotifyRevoked(int? userId)
        {
            Interlocked.Increment(ref revocationEpoch);
            sessionChecks.Clear();
            try { SessionsRevoked?.Invoke(userId); } catch { /* a listener must never break a revocation */ }
        }

        public (string access, string refresh) IssueToken(int userId, long? sessionExpiresAt = null, string? sessionId = null)
        {
            using var c = db.Open();
            c.Exec("DELETE FROM tokens WHERE refresh_expires_at < $now", ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return issue(c, userId, sessionExpiresAt, sessionId);
        }

        private static (string access, string refresh) issue(Microsoft.Data.Sqlite.SqliteConnection c, int userId, long? sessionExpiresAt, string? sessionId)
        {
            string access = randomToken(), refresh = randomToken();
            c.Exec("INSERT INTO tokens (access_token, refresh_token, user_id, expires_at, refresh_expires_at, session_expires_at, session_id) VALUES ($a, $r, $u, $e, $re, $se, $sid)",
                ("$a", TokenHash(access)), ("$r", TokenHash(refresh)), ("$u", userId), ("$e", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TOKEN_LIFETIME_SECONDS),
                ("$re", Math.Min(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), sessionExpiresAt ?? long.MaxValue)),
                ("$se", sessionExpiresAt ?? DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds()),
                ("$sid", sessionId ?? randomToken()[..32]));
            c.Exec("UPDATE users SET last_visit = $t WHERE id = $u", ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$u", userId));
            return (access, refresh);
        }

        private const string refresh_sql = "DELETE FROM tokens WHERE refresh_token = $r AND refresh_expires_at > $now AND session_expires_at > $now AND user_id IN (SELECT id FROM users WHERE restricted = 0) RETURNING user_id, session_expires_at, session_id";

        private static (int UserId, long SessionExpiresAt, string SessionId)? consume(Microsoft.Data.Sqlite.SqliteConnection c, string refreshToken)
        {
            if (refreshToken.Length != 64) return null;
            var rows = c.Query(refresh_sql, r => (UserId: r.GetInt32(0), SessionExpiresAt: r.GetInt64(1), SessionId: r.GetString(2)),
                ("$r", TokenHash(refreshToken)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return rows.Count == 1 ? rows[0] : null;
        }

        /// <summary>Consume a refresh token (single use). Prefer <see cref="Rotate"/>, which never leaves the session without a row.</summary>
        public (int UserId, long SessionExpiresAt, string SessionId)? Refresh(string refreshToken)
        {
            using var c = db.Open();
            return consume(c, refreshToken);
        }

        /// <summary>
        /// Authorised rotation: the old pair is replaced by a new pair of the same session in one transaction, so connections bound
        /// to that session (a multiplayer match, a spectated play) observe it as continuously valid.
        /// </summary>
        public (int UserId, string access, string refresh)? Rotate(string refreshToken)
        {
            if (refreshToken.Length != 64) return null;
            using var c = db.Open();
            c.Exec("BEGIN IMMEDIATE");
            try
            {
                var old = consume(c, refreshToken);
                if (old == null) { c.Exec("ROLLBACK"); return null; }
                var pair = issue(c, old.Value.UserId, old.Value.SessionExpiresAt, old.Value.SessionId);
                c.Exec("COMMIT");
                return (old.Value.UserId, pair.access, pair.refresh);
            }
            catch { try { c.Exec("ROLLBACK"); } catch { } throw; }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Active, long CheckedAtMs, long Epoch)> sessionChecks = new();

        /// <summary>
        /// Whether this exact session (not merely "some session of the account") is still usable. Hubs ask on every invocation, so the
        /// answer is cached for a moment; every revocation path bumps the epoch through <see cref="NotifyRevoked"/>, which voids the cache.
        /// </summary>
        public bool HasActiveSession(int userId, string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            long nowMs = Environment.TickCount64, epoch = Interlocked.Read(ref revocationEpoch);
            if (sessionChecks.TryGetValue(sessionId, out var known) && known.Epoch == epoch && nowMs - known.CheckedAtMs < 2000) return known.Active;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var c = db.Open();
            bool active = c.Scalar<long>("SELECT COUNT(*) FROM tokens WHERE session_id = $s AND user_id = $u AND session_expires_at > $now AND (expires_at > $now OR refresh_expires_at > $now) AND user_id IN (SELECT id FROM users WHERE restricted = 0)",
                ("$s", sessionId), ("$u", userId), ("$now", now)) > 0;
            if (sessionChecks.Count > 50000) sessionChecks.Clear();
            sessionChecks[sessionId] = (active, nowMs, epoch);
            return active;
        }

        /// <summary>Set once at startup: off when account mail is disabled, since nobody could then confirm anything.</summary>
        public static bool EmailConfirmationEnforced { get; set; } = true;

        public const string CONFIRM_EMAIL_MESSAGE = "Confirm your email address first: open the link we emailed you, or resend it from your account settings on the website.";

        /// <summary>
        /// Accounts created since confirmation became mandatory can sign in and play straight away, but anything that reaches other
        /// players (chat, comments, reports, profile media, teams, hosting rooms) waits for a confirmed address. Older accounts are exempt.
        /// </summary>
        public bool NeedsEmailConfirmation(int userId)
        {
            if (!EmailConfirmationEnforced) return false;
            using var c = db.Open();
            return c.Scalar<long>("SELECT COUNT(*) FROM users WHERE id = $u AND verification_required = 1 AND email_verified = 0", ("$u", userId)) > 0;
        }

        public bool EmailVerified(int userId)
        {
            using var c = db.Open();
            return c.Scalar<long>("SELECT COUNT(*) FROM users WHERE id = $u AND email_verified = 1", ("$u", userId)) > 0;
        }

        public static string TokenHash(string token) => "sha256:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        public int? ValidateAccessToken(string token) => ValidateAccess(token)?.UserId;

        public (int UserId, string SessionId)? ValidateAccess(string token)
        {
            using var c = db.Open();
            var rows = c.Query("SELECT user_id, session_id FROM tokens WHERE access_token = $a AND expires_at > $now AND session_expires_at > $now AND user_id IN (SELECT id FROM users WHERE restricted = 0)",
                r => (UserId: r.GetInt32(0), SessionId: r.GetString(1)),
                ("$a", TokenHash(token)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return rows.Count == 1 ? rows[0] : null;
        }

        /// <summary>Sign out: ends the whole session the access token belongs to (its refresh token included).</summary>
        public void Revoke(string token)
        {
            int? user = null;
            using (var c = db.Open())
            {
                var rows = c.Query("DELETE FROM tokens WHERE access_token = $a RETURNING user_id, session_id", r => (r.GetInt32(0), r.GetString(1)), ("$a", TokenHash(token)));
                foreach (var (u, sid) in rows)
                {
                    user = u;
                    c.Exec("DELETE FROM tokens WHERE session_id = $s AND user_id = $u", ("$s", sid), ("$u", u));
                }
            }
            if (user != null) NotifyRevoked(user);
        }

        /// <summary>End every session of an account (password / email change, reset, moderation).</summary>
        public void RevokeAll(int userId)
        {
            using (var c = db.Open()) c.Exec("DELETE FROM tokens WHERE user_id = $u", ("$u", userId));
            NotifyRevoked(userId);
        }

        private static string randomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        public static string HashPassword(string password)
        {
            if (password.Length is < PASSWORD_MIN or > PASSWORD_MAX) throw new ArgumentException("Invalid password length.");
            if (!passwordSlots.Wait(TimeSpan.FromSeconds(15))) throw new BadHttpRequestException("Password service busy. Please retry.", 429);
            try
            {
                byte[] salt = RandomNumberGenerator.GetBytes(16);
                byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA512, 32);
                return $"pbkdf2-sha512$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
            }
            finally { passwordSlots.Release(); }
        }

        public static bool VerifyPassword(string password, string stored)
        {
            if (password.Length > 256) return false;
            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha512") return false;
            int iterations = int.Parse(parts[1]);
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            if (!passwordSlots.Wait(TimeSpan.FromSeconds(15))) return false;
            try
            {
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally { passwordSlots.Release(); }
        }
    }

    /// <summary>
    /// Bearer token authentication. Also accepts <c>?access_token=</c>, which browsers need for SignalR WebSockets.
    /// </summary>
    public sealed class XcTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SCHEME = "xc-token";

        private readonly Accounts accounts;

        public XcTokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Accounts accounts)
            : base(options, logger, encoder)
        {
            this.accounts = accounts;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? token = null;
            string auth = Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = auth[7..].Trim();
            else if ((Request.Path == "/signalr/spectator" || Request.Path == "/signalr/multiplayer" || Request.Path == "/signalr/metadata" || (Request.Path == "/notifications" && Context.WebSockets.IsWebSocketRequest)) && Request.Query.TryGetValue("access_token", out var q))
                token = q.ToString();

            if (token == null && Request.Cookies.TryGetValue(BrowserSession.AccessCookie, out var cookie)
                && (BrowserSession.SameOrigin(Request) || (!Context.WebSockets.IsWebSocketRequest && (HttpMethods.IsGet(Request.Method) || HttpMethods.IsHead(Request.Method)))))
                token = cookie;

            if (string.IsNullOrEmpty(token))
                return Task.FromResult(AuthenticateResult.NoResult());

            var session = accounts.ValidateAccess(token);
            if (session == null)
                return Task.FromResult(AuthenticateResult.Fail("invalid token"));

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, session.Value.UserId.ToString()),
                new Claim(Accounts.SESSION_CLAIM, session.Value.SessionId),
            }, SCHEME);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SCHEME)));
        }
    }

    public static class PrincipalExtensions
    {
        public static int? UserId(this ClaimsPrincipal p) =>
            int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : null;

        public static string? SessionId(this ClaimsPrincipal p) => p.FindFirstValue(Accounts.SESSION_CLAIM);
    }
}
