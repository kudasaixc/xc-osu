// xc-osu! server — client telemetry (frame pacing, stutters, hardware, boot timings, errors). xc softworks (kudasaixc)
//
// Anonymous by construction: a session is a random id made by the page, an install is a random id kept in the browser's
// localStorage. The request's address is never read, nothing here is tied to an account. Kept in its own SQLite file
// (telemetry.db) so it can be dropped, copied or analysed (tools/tmreport.py) without touching the game database.

using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed class Telemetry : IDisposable
    {
        public const int MAX_BODY_BYTES = 512 * 1024;
        private const int max_items = 4000;
        private const int max_item_chars = 24 * 1024;
        private const long max_session_bytes = 40L * 1024 * 1024;
        private const long max_daily_bytes = 400L * 1024 * 1024;
        private const long max_db_bytes = 3L * 1024 * 1024 * 1024;
        private const int retention_days = 45;

        private readonly string connectionString;
        private readonly ILogger<Telemetry> log;
        private readonly object writeLock = new object();
        private readonly Timer purgeTimer;

        private long dayBytes;
        private int day;

        public Telemetry(Db db, ILogger<Telemetry> log)
        {
            this.log = log;
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(db.DataDirectory, "telemetry.db"),
                Pooling = true,
            }.ToString();

            using (var c = open())
            {
                c.Exec("""
                    CREATE TABLE IF NOT EXISTS tm_sessions (
                        sid TEXT PRIMARY KEY,
                        iid TEXT,
                        first_seen INTEGER NOT NULL,
                        last_seen INTEGER NOT NULL,
                        build TEXT,
                        channel TEXT,
                        gpu TEXT,
                        cores INTEGER,
                        ram REAL,
                        browser TEXT,
                        os TEXT,
                        refresh REAL,
                        boot_ms REAL,
                        last_seq INTEGER NOT NULL DEFAULT 0,
                        bytes INTEGER NOT NULL DEFAULT 0,
                        info TEXT
                    );
                    CREATE TABLE IF NOT EXISTS tm_windows (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        sid TEXT NOT NULL,
                        at INTEGER NOT NULL,
                        scr TEXT,
                        ov TEXT,
                        play INTEGER,
                        hidden INTEGER,
                        late INTEGER,
                        empty INTEGER,
                        draw_max REAL,
                        update_max REAL,
                        gc_pause REAL,
                        data TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS tm_events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        sid TEXT NOT NULL,
                        at INTEGER NOT NULL,
                        kind TEXT NOT NULL,
                        name TEXT,
                        ms REAL,
                        scr TEXT,
                        ov TEXT,
                        data TEXT
                    );
                    CREATE INDEX IF NOT EXISTS tm_windows_sid ON tm_windows (sid, at);
                    CREATE INDEX IF NOT EXISTS tm_events_sid ON tm_events (sid, at);
                    CREATE INDEX IF NOT EXISTS tm_events_kind ON tm_events (kind, at);
                    CREATE INDEX IF NOT EXISTS tm_sessions_seen ON tm_sessions (last_seen);
                    """);
            }

            purgeTimer = new Timer(_ => purge(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
        }

        private SqliteConnection open()
        {
            var c = new SqliteConnection(connectionString);
            c.Open();
            c.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
            return c;
        }

        private static bool validId(string? s)
        {
            if (s == null || s.Length < 16 || s.Length > 40) return false;

            foreach (char ch in s)
                if (!(ch is >= '0' and <= '9' or >= 'a' and <= 'f' or '-')) return false;

            return true;
        }

        private static string? cut(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

        private static string? str(JToken? t, int max = 120) => t == null || t.Type == JTokenType.Null ? null : cut(t.Type == JTokenType.String ? t.Value<string>() : t.ToString(Formatting.None), max);

        private static double? num(JToken? t) => t != null && t.Type is JTokenType.Integer or JTokenType.Float ? t!.Value<double>() : null;

        /// <summary>Stores one batch. Returns the highest sequence number stored for the session (what the page may forget), or null when refused.</summary>
        public long? Ingest(string body)
        {
            JObject root;

            try
            {
                root = JObject.Parse(body);
            }
            catch (JsonException)
            {
                return null;
            }

            string? sid = root.Value<string>("sid");
            if (!validId(sid)) return null;

            string? iid = root.Value<string>("iid");
            if (!validId(iid)) iid = null;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (writeLock)
            {
                int today = (int)(now / 86400000);
                if (today != day) { day = today; dayBytes = 0; }
                if (dayBytes > max_daily_bytes) return null;
                dayBytes += body.Length;

                using var c = open();
                using var tx = c.BeginTransaction();

                c.Exec("INSERT OR IGNORE INTO tm_sessions (sid, iid, first_seen, last_seen) VALUES ($s, $i, $n, $n)", ("$s", sid), ("$i", iid), ("$n", now));

                long lastSeq = c.Scalar<long>("SELECT last_seq FROM tm_sessions WHERE sid = $s", ("$s", sid));
                long bytes = c.Scalar<long>("SELECT bytes FROM tm_sessions WHERE sid = $s", ("$s", sid));
                if (bytes > max_session_bytes) return lastSeq;

                if (root["meta"] is JObject meta)
                {
                    var hw = meta["hw"] as JObject;
                    c.Exec("""
                        UPDATE tm_sessions SET build = $build, channel = $channel, gpu = $gpu, cores = $cores, ram = $ram, browser = $browser, os = $os,
                                               refresh = COALESCE($refresh, refresh), boot_ms = COALESCE($boot, boot_ms), info = $info
                        WHERE sid = $s
                        """,
                        ("$s", sid),
                        ("$build", str(meta["build"], 40)),
                        ("$channel", str(meta["channel"], 40)),
                        ("$gpu", str(hw?["gpu"], 200)),
                        ("$cores", num(hw?["cores"])),
                        ("$ram", num(hw?["ram"])),
                        ("$browser", str(hw?["browser"], 120)),
                        ("$os", str(hw?["os"], 80)),
                        ("$refresh", num(hw?["refresh"])),
                        ("$boot", num((meta["boot"] as JObject)?["ms"])),
                        ("$info", cut(meta.ToString(Formatting.None), 64 * 1024)));
                }

                long maxSeq = lastSeq;
                int stored = 0;

                if (root["items"] is JArray items)
                {
                    foreach (var token in items.OrderBy(t => t is JObject o ? o.Value<long?>("q") ?? 0 : 0))
                    {
                        if (stored >= max_items) break;
                        if (token is not JObject item) continue;

                        long q = item.Value<long?>("q") ?? 0;
                        // already stored: the page re-sends whatever was not acknowledged (closed tab, lost reply)
                        if (q <= maxSeq) continue;
                        if (q > maxSeq) maxSeq = q;

                        long at = item.Value<long?>("at") ?? now;

                        switch (item.Value<string>("k"))
                        {
                            case "w":
                            {
                                var th = item["th"] as JObject;
                                var draw = th?["draw"] as JObject;
                                string data = item.ToString(Formatting.None);
                                if (data.Length > max_item_chars) break;

                                c.Exec("""
                                    INSERT INTO tm_windows (sid, at, scr, ov, play, hidden, late, empty, draw_max, update_max, gc_pause, data)
                                    VALUES ($s, $at, $scr, $ov, $play, $hidden, $late, $empty, $dm, $um, $gp, $data)
                                    """,
                                    ("$s", sid), ("$at", at), ("$scr", str(item["scr"])), ("$ov", str(item["ov"])), ("$play", num(item["play"])),
                                    ("$hidden", num((item["js"] as JObject)?["hidden"])),
                                    ("$late", num(draw?["late"])), ("$empty", num(draw?["empty"])), ("$dm", num(draw?["im"])),
                                    ("$um", num((th?["update"] as JObject)?["wm"])),
                                    ("$gp", num((item["gc"] as JObject)?["pause"])),
                                    ("$data", data));
                                stored++;
                                break;
                            }

                            case "s":
                            {
                                if (item["sp"] is not JArray spikes) break;

                                foreach (var st in spikes)
                                {
                                    if (st is not JObject sp || stored >= max_items) continue;

                                    c.Exec("INSERT INTO tm_events (sid, at, kind, name, ms, scr, ov, data) VALUES ($s, $at, 'spike', $name, $ms, $scr, $ov, $data)",
                                        ("$s", sid), ("$at", sp.Value<long?>("at") ?? at), ("$name", str(sp["th"], 20) + ":" + str(sp["kd"], 20)),
                                        ("$ms", num(sp["ms"])), ("$scr", str(sp["scr"])), ("$ov", str(sp["ov"])), ("$data", cut(sp.ToString(Formatting.None), 2000)));
                                    stored++;
                                }

                                if (num(item["spx"]) is double dropped)
                                {
                                    c.Exec("INSERT INTO tm_events (sid, at, kind, name, ms) VALUES ($s, $at, 'spikes-dropped', NULL, $ms)", ("$s", sid), ("$at", at), ("$ms", dropped));
                                }

                                break;
                            }

                            case "e":
                            {
                                string data = item.ToString(Formatting.None);
                                if (data.Length > max_item_chars) data = data[..max_item_chars];

                                c.Exec("INSERT INTO tm_events (sid, at, kind, name, ms, scr, ov, data) VALUES ($s, $at, $kind, $name, $ms, $scr, $ov, $data)",
                                    ("$s", sid), ("$at", at), ("$kind", str(item["n"], 40) ?? "event"), ("$name", str(item["name"], 300)),
                                    ("$ms", num(item["ms"])), ("$scr", str(item["scr"])), ("$ov", str(item["ov"])), ("$data", data));
                                stored++;
                                break;
                            }
                        }
                    }
                }

                c.Exec("UPDATE tm_sessions SET last_seen = $n, last_seq = $q, bytes = bytes + $b, iid = COALESCE(iid, $i) WHERE sid = $s",
                    ("$s", sid), ("$n", now), ("$q", maxSeq), ("$b", body.Length), ("$i", iid));

                tx.Commit();
                return maxSeq;
            }
        }

        private void purge()
        {
            try
            {
                lock (writeLock)
                {
                    using var c = open();
                    long cutoff = DateTimeOffset.UtcNow.AddDays(-retention_days).ToUnixTimeMilliseconds();
                    long removed = c.Exec("DELETE FROM tm_windows WHERE at < $c", ("$c", cutoff))
                                   + c.Exec("DELETE FROM tm_events WHERE at < $c", ("$c", cutoff))
                                   + c.Exec("DELETE FROM tm_sessions WHERE last_seen < $c", ("$c", cutoff));

                    // disk guard: drop the oldest tenth until the file fits
                    for (int i = 0; i < 20; i++)
                    {
                        long size = c.Scalar<long>("SELECT (SELECT page_count FROM pragma_page_count()) - (SELECT freelist_count FROM pragma_freelist_count())")
                                    * c.Scalar<long>("SELECT page_size FROM pragma_page_size()");
                        if (size < max_db_bytes) break;

                        removed += c.Exec("DELETE FROM tm_windows WHERE id IN (SELECT id FROM tm_windows ORDER BY id LIMIT (SELECT COUNT(*) / 10 + 1 FROM tm_windows))")
                                   + c.Exec("DELETE FROM tm_events WHERE id IN (SELECT id FROM tm_events ORDER BY id LIMIT (SELECT COUNT(*) / 10 + 1 FROM tm_events))");
                    }

                    if (removed > 0)
                        log.LogInformation("telemetry: purged {Rows} rows", removed);
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "telemetry purge failed");
            }
        }

        public void Dispose() => purgeTimer.Dispose();
    }
}
