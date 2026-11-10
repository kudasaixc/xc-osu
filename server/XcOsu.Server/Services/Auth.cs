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

        private readonly Db db;

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
            if (!email.Contains('@') || email.Length > 254)
                add("user_email", "Please enter a valid email address.");
            if (password.Length < 8)
                add("password", "New password must be at least 8 characters.");
            else if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
                add("password", "Password may not contain username.");

            using var c = db.Open();
            if (c.Scalar<long>("SELECT COUNT(*) FROM users WHERE username = $u", ("$u", username)) > 0)
                add("username", "Username is already in use!");
            if (c.Scalar<long>("SELECT COUNT(*) FROM users WHERE email = $e", ("$e", email)) > 0)
                add("user_email", "Email address already used.");

            if (errors.Count > 0)
                return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());

            bool first = c.Scalar<long>("SELECT COUNT(*) FROM users") == 0;
            c.Exec("INSERT INTO users (username, email, password_hash, country, join_date, is_admin) VALUES ($u, $e, $p, $c, $d, $a)",
                ("$u", username), ("$e", email), ("$p", HashPassword(password)), ("$c", country), ("$d", DateTimeOffset.UtcNow.ToString("O")), ("$a", first ? 1 : 0));
            return null;
        }

        public AccountRow? Verify(string username, string password)
        {
            using var c = db.Open();
            var user = c.Query($"SELECT {COLUMNS} FROM users WHERE username = $n OR email = $n", Map, ("$n", username.Trim())).FirstOrDefault();
            return user != null && !user.Restricted && VerifyPassword(password, user.PasswordHash) ? user : null;
        }

        public (string access, string refresh) IssueToken(int userId)
        {
            string access = randomToken(), refresh = randomToken();
            using var c = db.Open();
            c.Exec("DELETE FROM tokens WHERE expires_at < $now", ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30L * 24 * 3600));
            c.Exec("INSERT INTO tokens (access_token, refresh_token, user_id, expires_at) VALUES ($a, $r, $u, $e)",
                ("$a", access), ("$r", refresh), ("$u", userId), ("$e", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TOKEN_LIFETIME_SECONDS));
            c.Exec("UPDATE users SET last_visit = $t WHERE id = $u", ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$u", userId));
            return (access, refresh);
        }

        public int? Refresh(string refreshToken)
        {
            using var c = db.Open();
            long? user = c.Scalar<long?>("SELECT user_id FROM tokens WHERE refresh_token = $r", ("$r", refreshToken));
            if (user == null) return null;
            c.Exec("DELETE FROM tokens WHERE refresh_token = $r", ("$r", refreshToken));
            return (int)user;
        }

        public int? ValidateAccessToken(string token)
        {
            using var c = db.Open();
            return (int?)c.Scalar<long?>("SELECT user_id FROM tokens WHERE access_token = $a AND expires_at > $now",
                ("$a", token), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }

        public void Revoke(string token)
        {
            using var c = db.Open();
            c.Exec("DELETE FROM tokens WHERE access_token = $a", ("$a", token));
        }

        private static string randomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        public static string HashPassword(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA512, 32);
            return $"pbkdf2-sha512$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public static bool VerifyPassword(string password, string stored)
        {
            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha512") return false;
            int iterations = int.Parse(parts[1]);
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
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
            else if (Request.Query.TryGetValue("access_token", out var q))
                token = q.ToString();

            if (string.IsNullOrEmpty(token))
                return Task.FromResult(AuthenticateResult.NoResult());

            int? userId = accounts.ValidateAccessToken(token);
            if (userId == null)
                return Task.FromResult(AuthenticateResult.Fail("invalid token"));

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, SCHEME);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SCHEME)));
        }
    }

    public static class PrincipalExtensions
    {
        public static int? UserId(this ClaimsPrincipal p) =>
            int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : null;
    }
}
