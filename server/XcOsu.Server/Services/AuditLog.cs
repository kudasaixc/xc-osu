// xc-osu! server: what happened to accounts, and who did it. xc softworks (kudasaixc)

using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services;

/// <summary>
/// Security-relevant actions (sign-in from a new place is not one of them; credential, session, moderation and deletion events are).
/// Never holds a secret. Rows are kept for a year (purged by <see cref="Maintenance"/>), so an incident can be reconstructed and
/// reported in time.
/// </summary>
public sealed class AuditLog(Db db, ILogger<AuditLog> log)
{
    public void Write(string action, int? actor, string? targetType = null, long? targetId = null, HttpContext? context = null, JObject? details = null)
    {
        try
        {
            using var c = db.Open();
            Write(c, action, actor, targetType, targetId, context, details);
        }
        catch (Exception e) { log.LogWarning("audit write failed ({Type}) for {Action}", e.GetType().Name, action); }
    }

    public static void Write(Npgsql.NpgsqlConnection c, string action, int? actor, string? targetType = null, long? targetId = null, HttpContext? context = null, JObject? details = null)
    {
        c.Exec("INSERT INTO audit_log (at, action, actor_id, target_type, target_id, address, details) VALUES (@at, @a, @u, @tt, @ti, @ip, @d)",
            ("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("@a", action), ("@u", actor), ("@tt", targetType), ("@ti", targetId),
            ("@ip", context == null ? null : ClientAddress.Key(context)), ("@d", details?.ToString(Newtonsoft.Json.Formatting.None)));
    }
}
