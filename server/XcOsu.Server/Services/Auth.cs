// xc-osu! server: accounts and OAuth tokens. xc softworks (kudasaixc)

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
                                    int? RankHighest, DateTimeOffset? RankHighestAt, DateTimeOffset? LastVisit, int? BirthYear);

    public sealed partial class Accounts
    {
        /// <summary>An access token lives one hour; clients renew it with their refresh token (the session itself lasts up to 90 days).</summary>
        public const int TOKEN_LIFETIME_SECONDS = 60 * 60;

        /// <summary>The one password length policy: registration, change, reset and the web forms all use these bounds.</summary>
        public const int PASSWORD_MIN = 8, PASSWORD_MAX = 256;
        public static readonly string PASSWORD_RULE = $"Use a password between {PASSWORD_MIN} and {PASSWORD_MAX} characters.";

        private readonly Db db;
        private static readonly int password_slot_count = Math.Max(4, Environment.ProcessorCount);
        private static readonly SemaphoreSlim passwordSlots = new(password_slot_count, password_slot_count);

        /// <summary>Every password hashing slot is busy. Never a verdict on the password: callers answer "retry", they do not count a failure.</summary>
        public sealed class PasswordServiceBusyException() : BadHttpRequestException("The sign-in service is busy. Please retry in a few seconds.", 503);

        public Accounts(Db db) => this.db = db;

        public static AccountRow Map(DbRow r) => new AccountRow(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            DateTimeOffset.Parse(r.GetString(5)), r.GetInt32(6) != 0, r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8),
            r.GetInt32(9), r.GetInt32(10), r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)), r.GetInt32(12) != 0, r.GetString(13), r.GetString(14),
            r.IsDBNull(15) ? null : r.GetInt32(15), r.IsDBNull(16) ? null : DateTimeOffset.Parse(r.GetString(16)), r.IsDBNull(17) ? null : DateTimeOffset.Parse(r.GetString(17)), r.IsDBNull(18) ? null : r.GetInt32(18));

        public const string COLUMNS = "id, username, email, password_hash, country, join_date, is_admin, title, playmode, avatar_version, cover_version, silenced_until, restricted, "
                                      + "previous_usernames, profile_json, rank_highest, rank_highest_at, last_visit, birth_year";

        public AccountRow? Get(int id)
        {
            using var c = db.Open();
            return Get(id, c);
        }

        /// <summary>Same as <see cref="Get(int)"/> on a connection the caller already holds (never take a second one from the pool).</summary>
        public AccountRow? Get(int id, Npgsql.NpgsqlConnection c) =>
            c.Query($"SELECT {COLUMNS} FROM users WHERE id = @id", Map, ("@id", id)).FirstOrDefault();

        public AccountRow? GetByName(string name)
        {
            using var c = db.Open();
            return c.Query($"SELECT {COLUMNS} FROM users WHERE username = @n", Map, ("@n", name)).FirstOrDefault();
        }

        public List<AccountRow> GetMany(IEnumerable<int> ids)
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return new List<AccountRow>();
            using var c = db.Open();
            return GetMany(list, c);
        }

        public List<AccountRow> GetMany(IEnumerable<int> ids, Npgsql.NpgsqlConnection c)
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return new List<AccountRow>();
            return c.Query($"SELECT {COLUMNS} FROM users WHERE id IN ({string.Join(',', list)})", Map);
        }

        [GeneratedRegex(@"^[A-Za-z0-9\[\]_\- ]{3,15}$")]
        private static partial Regex usernameRegex();

        /// <summary>Names nobody may register: staff and system names, and anything that reads like them once look-alike characters are folded.</summary>
        private static readonly string[] reserved_names =
        {
            "admin", "administrator", "moderator", "mod", "staff", "support", "system", "root", "owner", "official", "server", "bot",
            "xcsoftworks", "xcsoftwork", "xcosu", "xcosuteam", "kudasaixc", "nanbeige", "banchobot", "bancho", "peppy", "ppy", "osu", "osuteam",
            "security", "noreply", "abuse", "help", "everyone", "here", "deleted", "deleteduser", "guest", "null", "undefined",
        };

        /// <summary>Folds a name for the reserved-name check: case, separators and the usual digit look-alikes.</summary>
        public static string FoldName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name.ToLowerInvariant())
            {
                char c = ch switch { '0' => 'o', '1' => 'l', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '8' => 'b', '$' => 's', '@' => 'a', 'i' => 'l', _ => ch };
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        public static bool IsReservedName(string name)
        {
            string folded = FoldName(name);
            return reserved_names.Any(r => folded == FoldName(r) || (FoldName(r).Length >= 5 && folded.Contains(FoldName(r))));
        }

        public const int MINIMUM_AGE = 13;

        /// <summary>Age a player born in <paramref name="birthYear"/> reaches this year (we only keep the year).</summary>
        public static int AgeThisYear(long birthYear) => DateTime.UtcNow.Year - (int)birthYear;

        /// <summary>Returns per-field errors in osu-web's form_error shape, or null on success. <paramref name="existingEmail"/> is set when the
        /// address already has an account: nothing is created, and the caller answers exactly as for a success (no account enumeration).</summary>
        public async Task<(Dictionary<string, string[]>? errors, bool existingEmail)> Register(string username, string email, string password, string country, int? birthYear, PwnedPasswords pwned)
        {
            var errors = new Dictionary<string, List<string>>();
            void add(string f, string m) { if (!errors.TryGetValue(f, out var l)) errors[f] = l = new List<string>(); l.Add(m); }

            username = username.Trim();
            email = email.Trim();
            if (!usernameRegex().IsMatch(username) || username.Contains("  ") || username.StartsWith(' ') || username.EndsWith(' '))
                add("username", "Username must be 3-15 characters: letters, numbers, spaces, [ ] _ -");
            else if (IsReservedName(username))
                add("username", "This username is reserved.");
            if (email.Length > 254 || email.Contains('\r') || email.Contains('\n') || !System.Net.Mail.MailAddress.TryCreate(email, out var parsedEmail) || parsedEmail.Address != email)
                add("user_email", "Please enter a valid email address.");
            if (PasswordProblem(password, username, email) is string weak)
                add("password", weak);
            else if (await pwned.IsBreached(password))
                add("password", PwnedPasswords.MESSAGE);
            if (birthYear is int year && (year < DateTime.UtcNow.Year - 120 || year > DateTime.UtcNow.Year))
                add("birth_year", "Please enter a valid year of birth.");
            else if (birthYear is int young && AgeThisYear(young) < MINIMUM_AGE)
                add("birth_year", $"xc-osu! accounts are for players aged {MINIMUM_AGE} or over.");

            using var c = db.Open();
            if (c.Scalar<long>("SELECT COUNT(*) FROM users WHERE username = @u", ("@u", username)) > 0)
                add("username", "Username is already in use!");

            if (errors.Count > 0)
                return (errors.ToDictionary(e => e.Key, e => e.Value.ToArray()), false);
            if (c.Scalar<long>("SELECT COUNT(*) FROM users WHERE email = @e", ("@e", email)) > 0)
                return (null, true);

            try
            {
                c.Exec("INSERT INTO users (username, email, password_hash, country, join_date, is_admin, verification_required, birth_year, profile_json) VALUES (@u, @e, @p, @c, @d, @a, 1, @b, @pj)",
                    ("@u", username), ("@e", email), ("@p", HashPassword(password)), ("@c", country), ("@d", DateTimeOffset.UtcNow.ToString("O")), ("@a", 0), ("@b", birthYear),
                    // under 16 (the strictest age of digital consent in the EU), private messages start limited to friends.
                    ("@pj", birthYear is int y && AgeThisYear(y) < 16 ? "{\"pm_friends_only\":true}" : "{}"));
            }
            catch (Exception e) when (e.IsUniqueViolation())
            {
                // two registrations raced past the checks above; the unique index decided.
                return (new Dictionary<string, string[]> { ["username"] = new[] { "Username is already in use!" } }, false);
            }
            return (null, false);
        }

        /// <summary>The password rule shared by registration, change and reset; null when acceptable.</summary>
        public static string? PasswordProblem(string password, string? username, string? email)
        {
            if (password.Length is < PASSWORD_MIN or > PASSWORD_MAX) return PASSWORD_RULE;
            if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase)) return "Password may not contain username.";
            if (!string.IsNullOrEmpty(email) && (password.Equals(email, StringComparison.OrdinalIgnoreCase)
                                                 || password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase) && email.Split('@')[0].Length >= 4))
                return "Password may not contain your email address.";
            if (password.Distinct().Count() < 4) return "This password is too easy to guess.";
            return null;
        }

        /// <summary>
        /// Checks a password sign-in. Null: refused (wrong password, unknown or restricted account, or this address must wait).
        /// Throws <see cref="PasswordServiceBusyException"/> when it could not check at all; that is never counted as a failure.
        /// </summary>
        public AccountRow? Verify(string username, string password, string address)
        {
            if (username.Length > 254 || password.Length > PASSWORD_MAX) return null;
            string accountKey = TokenHash(username.Trim().ToUpperInvariant());
            if (!LoginAttempts.CanTry(accountKey, address)) return null;
            AccountRow? user;
            using (var c = db.Open())
                user = c.Query($"SELECT {COLUMNS} FROM users WHERE username = @n OR email = @n", Map, ("@n", username.Trim())).FirstOrDefault();
            bool valid = user != null && !user.Restricted && VerifyPassword(password, user.PasswordHash);
            LoginAttempts.Record(accountKey, address, valid);
            return valid ? user : null;
        }

        public const string SESSION_CLAIM = "xc:session";
        public const string VERIFIED_CLAIM = "xc:verified";

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

        /// <summary>A new session for a password sign-in. It starts unverified when the account uses two-factor authentication.</summary>
        public (string access, string refresh) IssueToken(int userId, long? sessionExpiresAt = null, string? sessionId = null)
        {
            using var c = db.Open();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            c.Exec("DELETE FROM tokens WHERE refresh_expires_at < @now", ("@now", now));
            c.Exec("DELETE FROM consumed_refresh_tokens WHERE consumed_at < @old", ("@old", now - 31 * 86400));
            bool needsSecondFactor = c.Scalar<long>("SELECT totp_enabled FROM users WHERE id = @u", ("@u", userId)) == 1;
            return issue(c, userId, sessionExpiresAt, sessionId, verified: !needsSecondFactor);
        }

        private static (string access, string refresh) issue(Npgsql.NpgsqlConnection c, int userId, long? sessionExpiresAt, string? sessionId, bool verified)
        {
            string access = randomToken(), refresh = randomToken();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            c.Exec("INSERT INTO tokens (access_token, refresh_token, user_id, expires_at, refresh_expires_at, session_expires_at, session_id, created_at, verified) VALUES (@a, @r, @u, @e, @re, @se, @sid, @c, @v)",
                ("@a", TokenHash(access)), ("@r", TokenHash(refresh)), ("@u", userId), ("@e", now + TOKEN_LIFETIME_SECONDS),
                ("@re", Math.Min(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), sessionExpiresAt ?? long.MaxValue)),
                ("@se", sessionExpiresAt ?? DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds()),
                ("@sid", sessionId ?? randomToken()[..32]), ("@c", now), ("@v", verified ? 1 : 0));
            c.Exec("UPDATE users SET last_visit = @t WHERE id = @u", ("@t", DateTimeOffset.UtcNow.ToString("O")), ("@u", userId));
            return (access, refresh);
        }

        private const string refresh_sql = "DELETE FROM tokens WHERE refresh_token = @r AND refresh_expires_at > @now AND session_expires_at > @now AND user_id IN (SELECT id FROM users WHERE restricted = 0) RETURNING user_id, session_expires_at, session_id, verified";

        private static (int UserId, long SessionExpiresAt, string SessionId, bool Verified)? consume(Npgsql.NpgsqlConnection c, string refreshToken)
        {
            if (refreshToken.Length != 64) return null;
            string hash = TokenHash(refreshToken);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var rows = c.Query(refresh_sql, r => (UserId: r.GetInt32(0), SessionExpiresAt: r.GetInt64(1), SessionId: r.GetString(2), Verified: r.GetInt64(3) != 0),
                ("@r", hash), ("@now", now));
            if (rows.Count != 1) return null;
            c.Exec("INSERT INTO consumed_refresh_tokens (token_hash, session_id, user_id, consumed_at) VALUES (@h, @s, @u, @n) ON CONFLICT DO NOTHING",
                ("@h", hash), ("@s", rows[0].SessionId), ("@u", rows[0].UserId), ("@n", now));
            return rows[0];
        }

        /// <summary>
        /// A refresh token that was already exchanged is being presented again: either a client retried a lost answer (seconds later), or a
        /// copy of the token is in someone else's hands. Past a short grace period the whole session is ended. Returns the account affected.
        /// </summary>
        private int? endSessionOfReusedToken(Npgsql.NpgsqlConnection c, string refreshToken)
        {
            if (refreshToken.Length != 64) return null;
            var reused = c.Query("SELECT session_id, user_id, consumed_at FROM consumed_refresh_tokens WHERE token_hash = @h",
                r => (session: r.GetString(0), user: r.GetInt32(1), at: r.GetInt64(2)), ("@h", TokenHash(refreshToken))).FirstOrDefault();
            if (reused == default || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - reused.at < 30) return null;
            if (c.Exec("DELETE FROM tokens WHERE session_id = @s AND user_id = @u", ("@s", reused.session), ("@u", reused.user)) > 0)
                AuditLog.Write(c, "session.refresh_token_reused", null, "user", reused.user);
            return reused.user;
        }

        /// <summary>
        /// Authorised rotation: the old pair is replaced by a new pair of the same session in one transaction, so connections bound
        /// to that session (a multiplayer match, a spectated play) observe it as continuously valid.
        /// </summary>
        public (int UserId, string access, string refresh)? Rotate(string refreshToken)
        {
            if (refreshToken.Length != 64) return null;
            int? reusedBy;
            using (var c = db.Open())
            {
                // the DELETE … RETURNING takes the row lock: a concurrent rotation of the same token waits, then finds nothing.
                using (var tx = c.BeginTransaction())
                {
                    var old = consume(c, refreshToken);
                    if (old != null)
                    {
                        var pair = issue(c, old.Value.UserId, old.Value.SessionExpiresAt, old.Value.SessionId, old.Value.Verified);
                        tx.Commit();
                        return (old.Value.UserId, pair.access, pair.refresh);
                    }
                }
                reusedBy = endSessionOfReusedToken(c, refreshToken);
            }
            if (reusedBy != null) NotifyRevoked(reusedBy);
            return null;
        }

        public sealed record SessionInfo(string Id, long CreatedAt, long ExpiresAt, bool Verified);

        /// <summary>The account's open sessions (one per signed-in device or browser), newest first.</summary>
        public List<SessionInfo> Sessions(int userId)
        {
            using var c = db.Open();
            return c.Query("""
                SELECT session_id, MIN(NULLIF(created_at, 0)), MAX(session_expires_at), MIN(verified) FROM tokens
                WHERE user_id = @u AND session_expires_at > @now AND (expires_at > @now OR refresh_expires_at > @now)
                GROUP BY session_id ORDER BY 2 DESC NULLS LAST
                """, r => new SessionInfo(r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt64(1), r.GetInt64(2), r.GetInt64(3) != 0),
                ("@u", userId), ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }

        /// <summary>Ends one session of the account (sign out a device from the account page).</summary>
        public bool RevokeSession(int userId, string sessionId)
        {
            long removed;
            using (var c = db.Open()) removed = c.Exec("DELETE FROM tokens WHERE user_id = @u AND session_id = @s", ("@u", userId), ("@s", sessionId));
            if (removed > 0) NotifyRevoked(userId);
            return removed > 0;
        }

        /// <summary>The second factor was given for this session: every token of it becomes usable.</summary>
        public void MarkSessionVerified(int userId, string sessionId)
        {
            using (var c = db.Open()) c.Exec("UPDATE tokens SET verified = 1 WHERE user_id = @u AND session_id = @s", ("@u", userId), ("@s", sessionId));
            NotifyRevoked(userId);
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
            bool active = c.Scalar<long>("SELECT COUNT(*) FROM tokens WHERE session_id = @s AND user_id = @u AND session_expires_at > @now AND (expires_at > @now OR refresh_expires_at > @now) AND user_id IN (SELECT id FROM users WHERE restricted = 0)",
                ("@s", sessionId), ("@u", userId), ("@now", now)) > 0;
            if (sessionChecks.Count > 50000) sessionChecks.Clear();
            sessionChecks[sessionId] = (active, nowMs, epoch);
            return active;
        }

        /// <summary>Which of these sessions are still usable (same rule as <see cref="HasActiveSession"/>), in one query.</summary>
        public HashSet<string> ActiveSessions(string[] sessionIds)
        {
            if (sessionIds.Length == 0) return new HashSet<string>();
            using var c = db.Open();
            return c.Query("SELECT DISTINCT session_id FROM tokens WHERE session_id = ANY(@s) AND session_expires_at > @now AND (expires_at > @now OR refresh_expires_at > @now) AND user_id IN (SELECT id FROM users WHERE restricted = 0)",
                r => r.GetString(0), ("@s", sessionIds), ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())).ToHashSet();
        }

        /// <summary>Set once at startup: off when account mail is disabled, since nobody could then confirm anything.</summary>
        public static bool EmailConfirmationEnforced { get; set; } = true;

        public const string CONFIRM_EMAIL_MESSAGE = "Finish setting up your account first: confirm your email address (open the link we emailed you) and enter your year of birth, in your account settings on the website.";

        /// <summary>
        /// Accounts created since confirmation became mandatory can sign in and play straight away, but anything that reaches other
        /// players (chat, comments, reports, profile media, teams, hosting rooms) waits for a confirmed address. Older accounts are exempt.
        /// </summary>
        public bool NeedsEmailConfirmation(int userId)
        {
            using var c = db.Open();
            // a year of birth is asked of everyone before anything social (age of digital consent, protection of minors).
            return c.Scalar<long>("SELECT COUNT(*) FROM users WHERE id = @u AND (birth_year IS NULL OR (@enforced = 1 AND verification_required = 1 AND email_verified = 0))",
                ("@u", userId), ("@enforced", EmailConfirmationEnforced ? 1 : 0)) > 0;
        }

        public bool TwoFactorEnabled(int userId)
        {
            using var c = db.Open();
            return c.Scalar<long>("SELECT totp_enabled FROM users WHERE id = @u", ("@u", userId)) == 1;
        }

        public bool EmailVerified(int userId)
        {
            using var c = db.Open();
            return c.Scalar<long>("SELECT COUNT(*) FROM users WHERE id = @u AND email_verified = 1", ("@u", userId)) > 0;
        }

        public static string TokenHash(string token) => "sha256:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        public int? ValidateAccessToken(string token) => ValidateAccess(token)?.UserId;

        public (int UserId, string SessionId, bool Verified)? ValidateAccess(string token)
        {
            if (token.Length != 64) return null;
            using var c = db.Open();
            var rows = c.Query("SELECT user_id, session_id, verified FROM tokens WHERE access_token = @a AND expires_at > @now AND session_expires_at > @now AND user_id IN (SELECT id FROM users WHERE restricted = 0)",
                r => (UserId: r.GetInt32(0), SessionId: r.GetString(1), Verified: r.GetInt64(2) != 0),
                ("@a", TokenHash(token)), ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return rows.Count == 1 ? rows[0] : null;
        }

        /// <summary>Whether this session still waits for its second factor (the verified state is cached like <see cref="HasActiveSession"/>).</summary>
        public bool SessionVerified(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            using var c = db.Open();
            return c.Scalar<long>("SELECT COUNT(*) FROM tokens WHERE session_id = @s AND verified = 0", ("@s", sessionId)) == 0;
        }

        /// <summary>Sign out: ends the whole session the access token belongs to (its refresh token included).</summary>
        public void Revoke(string token)
        {
            int? user = null;
            using (var c = db.Open())
            {
                var rows = c.Query("DELETE FROM tokens WHERE access_token = @a RETURNING user_id, session_id", r => (r.GetInt32(0), r.GetString(1)), ("@a", TokenHash(token)));
                foreach (var (u, sid) in rows)
                {
                    user = u;
                    c.Exec("DELETE FROM tokens WHERE session_id = @s AND user_id = @u", ("@s", sid), ("@u", u));
                }
            }
            if (user != null) NotifyRevoked(user);
        }

        /// <summary>End every session of an account (password / email change, reset, moderation).</summary>
        public void RevokeAll(int userId)
        {
            using (var c = db.Open()) c.Exec("DELETE FROM tokens WHERE user_id = @u", ("@u", userId));
            NotifyRevoked(userId);
        }

        private static string randomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        public static string HashPassword(string password)
        {
            if (password.Length is < PASSWORD_MIN or > PASSWORD_MAX) throw new ArgumentException("Invalid password length.");
            if (!passwordSlots.Wait(TimeSpan.FromSeconds(10))) throw new PasswordServiceBusyException();
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
            if (!passwordSlots.Wait(TimeSpan.FromSeconds(10))) throw new PasswordServiceBusyException();
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
                new Claim(Accounts.VERIFIED_CLAIM, session.Value.Verified ? "1" : "0"),
            }, SCHEME);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SCHEME)));
        }
    }

    public static class PrincipalExtensions
    {
        public static int? UserId(this ClaimsPrincipal p) =>
            int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : null;

        public static string? SessionId(this ClaimsPrincipal p) => p.FindFirstValue(Accounts.SESSION_CLAIM);

        /// <summary>False while a session of a two-factor account has not given its code yet.</summary>
        public static bool SessionVerified(this ClaimsPrincipal p) => p.FindFirstValue(Accounts.VERIFIED_CLAIM) != "0";
    }
}
