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

            // ---- v2: full online feature set
            exec(c, """
                CREATE TABLE IF NOT EXISTS rank_history (
                    user_id INTEGER NOT NULL,
                    ruleset_id INTEGER NOT NULL,
                    day TEXT NOT NULL,
                    rank INTEGER NOT NULL,
                    pp REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, ruleset_id, day)
                );
                CREATE TABLE IF NOT EXISTS beatmap_playcounts (
                    user_id INTEGER NOT NULL,
                    beatmap_id INTEGER NOT NULL,
                    count INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, beatmap_id)
                );
                CREATE TABLE IF NOT EXISTS replay_watches (
                    user_id INTEGER NOT NULL,
                    month TEXT NOT NULL,
                    count INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, month)
                );
                CREATE TABLE IF NOT EXISTS user_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL,
                    type TEXT NOT NULL,
                    json TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_user_events ON user_events(user_id, id DESC);
                CREATE TABLE IF NOT EXISTS pinned_scores (
                    user_id INTEGER NOT NULL,
                    score_id INTEGER NOT NULL,
                    sort INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, score_id)
                );
                CREATE TABLE IF NOT EXISTS comments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    commentable_type TEXT NOT NULL,
                    commentable_id INTEGER NOT NULL,
                    parent_id INTEGER,
                    user_id INTEGER NOT NULL,
                    message TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    edited_at TEXT,
                    edited_by_id INTEGER,
                    deleted_at TEXT,
                    pinned INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_comments ON comments(commentable_type, commentable_id, parent_id);
                CREATE TABLE IF NOT EXISTS comment_votes (
                    comment_id INTEGER NOT NULL,
                    user_id INTEGER NOT NULL,
                    PRIMARY KEY (comment_id, user_id)
                );
                CREATE TABLE IF NOT EXISTS beatmap_tags (
                    beatmap_id INTEGER NOT NULL,
                    tag_id INTEGER NOT NULL,
                    user_id INTEGER NOT NULL,
                    PRIMARY KEY (beatmap_id, tag_id, user_id)
                );
                CREATE TABLE IF NOT EXISTS user_achievements (
                    user_id INTEGER NOT NULL,
                    achievement_id INTEGER NOT NULL,
                    achieved_at TEXT NOT NULL,
                    PRIMARY KEY (user_id, achievement_id)
                );
                CREATE TABLE IF NOT EXISTS notifications (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    object_type TEXT NOT NULL,
                    object_id INTEGER NOT NULL,
                    category TEXT NOT NULL,
                    source_user_id INTEGER,
                    details TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    is_read INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_notifications ON notifications(user_id, id DESC);
                CREATE TABLE IF NOT EXISTS daily_challenges (
                    day TEXT PRIMARY KEY,
                    room_id INTEGER NOT NULL,
                    beatmap_id INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS daily_challenge_stats (
                    user_id INTEGER PRIMARY KEY,
                    playcount INTEGER NOT NULL DEFAULT 0,
                    daily_streak_current INTEGER NOT NULL DEFAULT 0,
                    daily_streak_best INTEGER NOT NULL DEFAULT 0,
                    weekly_streak_current INTEGER NOT NULL DEFAULT 0,
                    weekly_streak_best INTEGER NOT NULL DEFAULT 0,
                    top_10p INTEGER NOT NULL DEFAULT 0,
                    top_50p INTEGER NOT NULL DEFAULT 0,
                    last_day TEXT,
                    last_week TEXT
                );
                CREATE TABLE IF NOT EXISTS matchmaking_ratings (
                    user_id INTEGER NOT NULL,
                    pool_id INTEGER NOT NULL,
                    rating REAL NOT NULL DEFAULT 1500,
                    sigma REAL NOT NULL DEFAULT 350,
                    plays INTEGER NOT NULL DEFAULT 0,
                    first_placements INTEGER NOT NULL DEFAULT 0,
                    total_points INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, pool_id)
                );
                CREATE TABLE IF NOT EXISTS teams (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    short_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    flag_url TEXT,
                    leader_id INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS team_members (
                    team_id INTEGER NOT NULL,
                    user_id INTEGER NOT NULL PRIMARY KEY
                );
                CREATE TABLE IF NOT EXISTS reports (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    reporter_id INTEGER NOT NULL,
                    reportable_type TEXT NOT NULL,
                    reportable_id INTEGER NOT NULL,
                    reason TEXT,
                    comments TEXT,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS screenshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL,
                    hash TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_scores_playlist ON scores(playlist_item_id, user_id);
                """);

            addColumn(c, "users", "avatar_version INTEGER NOT NULL DEFAULT 0");
            addColumn(c, "users", "cover_version INTEGER NOT NULL DEFAULT 0");
            addColumn(c, "users", "silenced_until TEXT");
            addColumn(c, "users", "restricted INTEGER NOT NULL DEFAULT 0");
            addColumn(c, "users", "previous_usernames TEXT NOT NULL DEFAULT '[]'");
            addColumn(c, "users", "profile_json TEXT NOT NULL DEFAULT '{}'");
            addColumn(c, "users", "rank_highest INTEGER");
            addColumn(c, "users", "rank_highest_at TEXT");
            addColumn(c, "scores", "started_at TEXT");
            addColumn(c, "scores", "total_hits INTEGER NOT NULL DEFAULT 0");
            addColumn(c, "scores", "play_seconds INTEGER NOT NULL DEFAULT 0");
            addColumn(c, "scores", "legacy_total_score INTEGER NOT NULL DEFAULT 0");
            addColumn(c, "score_tokens", "beatmap_hash TEXT");
            addColumn(c, "score_tokens", "room_id INTEGER");
            addColumn(c, "chat_channels", "room_id INTEGER");
            addColumn(c, "chat_channels", "moderated INTEGER NOT NULL DEFAULT 0");
            addColumn(c, "chat_messages", "uuid TEXT");
        }

        private static void addColumn(SqliteConnection c, string table, string definition)
        {
            string name = definition.Split(' ')[0];
            using (var check = c.CreateCommand())
            {
                check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{name}'";
                if ((long)check.ExecuteScalar()! > 0) return;
            }

            exec(c, $"ALTER TABLE {table} ADD COLUMN {definition}");
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
