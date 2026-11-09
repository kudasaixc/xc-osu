// xc-osu! server — SQLite persistence. xc softworks (kudasaixc)

using Microsoft.Data.Sqlite;

namespace XcOsu.Server.Data
{
    public sealed class Db
    {
        private readonly string connectionString;

        public Db(IConfiguration config)
        {
            string dir = config["DataDirectory"] ?? "/var/lib/xc-osu";
            Directory.CreateDirectory(dir);
            DataDirectory = dir;
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dir, "xc-osu.db"),
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
            }.ToString();
            migrate();
        }

        public string DataDirectory { get; }

        public SqliteConnection Open()
        {
            var c = new SqliteConnection(connectionString);
            c.Open();
            using var pragma = c.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return c;
        }

        private void migrate()
        {
            using var c = Open();
            exec(c, """
                CREATE TABLE IF NOT EXISTS users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    email TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    password_hash TEXT NOT NULL,
                    country TEXT NOT NULL DEFAULT 'XX',
                    join_date TEXT NOT NULL,
                    last_visit TEXT,
                    is_admin INTEGER NOT NULL DEFAULT 0,
                    title TEXT,
                    playmode TEXT NOT NULL DEFAULT 'osu'
                );
                CREATE TABLE IF NOT EXISTS tokens (
                    access_token TEXT PRIMARY KEY,
                    refresh_token TEXT NOT NULL UNIQUE,
                    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    expires_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS score_tokens (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL,
                    beatmap_id INTEGER NOT NULL,
                    ruleset_id INTEGER NOT NULL,
                    playlist_item_id INTEGER,
                    created_at INTEGER NOT NULL,
                    score_id INTEGER
                );
                CREATE TABLE IF NOT EXISTS scores (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    beatmap_id INTEGER NOT NULL,
                    ruleset_id INTEGER NOT NULL,
                    playlist_item_id INTEGER,
                    total_score INTEGER NOT NULL,
                    accuracy REAL NOT NULL,
                    max_combo INTEGER NOT NULL,
                    rank TEXT NOT NULL,
                    passed INTEGER NOT NULL,
                    pp REAL,
                    ranked INTEGER NOT NULL DEFAULT 0,
                    mods TEXT NOT NULL,
                    data TEXT NOT NULL,
                    ended_at TEXT NOT NULL,
                    has_replay INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_scores_beatmap ON scores(beatmap_id, ruleset_id, passed, total_score DESC);
                CREATE INDEX IF NOT EXISTS ix_scores_user ON scores(user_id, ruleset_id, ended_at DESC);
                CREATE TABLE IF NOT EXISTS beatmaps (
                    id INTEGER PRIMARY KEY,
                    set_id INTEGER NOT NULL,
                    checksum TEXT,
                    status INTEGER NOT NULL DEFAULT 0,
                    json TEXT NOT NULL,
                    fetched_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_beatmaps_checksum ON beatmaps(checksum);
                CREATE TABLE IF NOT EXISTS beatmapsets (
                    id INTEGER PRIMARY KEY,
                    json TEXT NOT NULL,
                    fetched_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS chat_channels (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    description TEXT NOT NULL DEFAULT '',
                    type TEXT NOT NULL DEFAULT 'PUBLIC',
                    pm_user_a INTEGER,
                    pm_user_b INTEGER
                );
                CREATE TABLE IF NOT EXISTS chat_messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    channel_id INTEGER NOT NULL REFERENCES chat_channels(id) ON DELETE CASCADE,
                    sender_id INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    is_action INTEGER NOT NULL DEFAULT 0,
                    timestamp TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_chat_messages ON chat_messages(channel_id, id);
                CREATE TABLE IF NOT EXISTS chat_members (
                    channel_id INTEGER NOT NULL,
                    user_id INTEGER NOT NULL,
                    last_read_id INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (channel_id, user_id)
                );
                CREATE TABLE IF NOT EXISTS friends (
                    user_id INTEGER NOT NULL,
                    target_id INTEGER NOT NULL,
                    PRIMARY KEY (user_id, target_id)
                );
                CREATE TABLE IF NOT EXISTS blocks (
                    user_id INTEGER NOT NULL,
                    target_id INTEGER NOT NULL,
                    PRIMARY KEY (user_id, target_id)
                );
                CREATE TABLE IF NOT EXISTS favourites (
                    user_id INTEGER NOT NULL,
                    beatmapset_id INTEGER NOT NULL,
                    PRIMARY KEY (user_id, beatmapset_id)
                );
                CREATE TABLE IF NOT EXISTS rooms (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    json TEXT NOT NULL,
                    host_id INTEGER NOT NULL,
                    category TEXT NOT NULL,
                    type TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    ends_at TEXT,
                    active INTEGER NOT NULL DEFAULT 1
                );
                CREATE TABLE IF NOT EXISTS playlist_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    room_id INTEGER NOT NULL,
                    json TEXT NOT NULL
                );
                INSERT INTO chat_channels (id, name, description, type)
                    SELECT 1, '#osu', 'General discussion for xc-osu!', 'PUBLIC' WHERE NOT EXISTS (SELECT 1 FROM chat_channels WHERE id = 1);
                INSERT INTO chat_channels (id, name, description, type)
                    SELECT 2, '#announce', 'xc softworks announcements', 'PUBLIC' WHERE NOT EXISTS (SELECT 1 FROM chat_channels WHERE id = 2);
                """);
        }

        private static void exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public static class SqliteExtensions
    {
        public static SqliteCommand Cmd(this SqliteConnection c, string sql, params (string name, object? value)[] args)
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }

        public static long Exec(this SqliteConnection c, string sql, params (string, object?)[] args)
        {
            using var cmd = c.Cmd(sql, args);
            return cmd.ExecuteNonQuery();
        }

        public static T? Scalar<T>(this SqliteConnection c, string sql, params (string, object?)[] args)
        {
            using var cmd = c.Cmd(sql, args);
            object? r = cmd.ExecuteScalar();
            if (r == null || r is DBNull) return default;
            return (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        }

        public static long Insert(this SqliteConnection c, string sql, params (string, object?)[] args)
        {
            using var cmd = c.Cmd(sql + "; SELECT last_insert_rowid();", args);
            return (long)cmd.ExecuteScalar()!;
        }

        public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
        {
            using var cmd = c.Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read())
                list.Add(map(r));
            return list;
        }
    }
}
