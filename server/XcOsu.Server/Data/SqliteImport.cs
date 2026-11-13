// xc-osu! server: one-off import of the former SQLite files into PostgreSQL (operator command "import-sqlite"). xc softworks (kudasaixc)

using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace XcOsu.Server.Data
{
    public static class SqliteImport
    {
        // parents of a foreign key go first.
        private static readonly string[] first = { "users", "chat_channels" };

        /// <summary>
        /// Replaces the content of every PostgreSQL table with the rows of the matching table found in <paramref name="files"/>.
        /// Everything happens in one transaction: either the whole import lands or the database is left as it was.
        /// </summary>
        public static void Run(Db db, IEnumerable<string> files, Action<string> log)
        {
            using var pg = db.Open();
            using var tx = pg.BeginTransaction();

            var tables = pg.Query("SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY table_name",
                r => r.GetString(0));
            tables = first.Where(tables.Contains).Concat(tables.Where(t => !first.Contains(t))).ToList();
            var imported = new HashSet<string>();

            foreach (string file in files)
            {
                if (!File.Exists(file)) throw new FileNotFoundException("SQLite file not found", file);

                using var lite = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                lite.Open();

                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = lite.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) present.Add(r.GetString(0));
                }

                var counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                if (present.Contains("sqlite_sequence"))
                {
                    using var cmd = lite.CreateCommand();
                    cmd.CommandText = "SELECT name, seq FROM sqlite_sequence";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) counters[r.GetString(0)] = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                }

                var mine = tables.Where(present.Contains).ToList();
                if (mine.Count == 0) throw new InvalidOperationException($"{file}: no table of this server in that file.");
                foreach (string unknown in present.Where(t => !tables.Contains(t.ToLowerInvariant()) && !t.StartsWith("sqlite_", StringComparison.Ordinal)))
                    log($"  ! {unknown}: no such table on the PostgreSQL side, skipped");

                pg.Exec("TRUNCATE " + string.Join(", ", mine.Select(quote)) + " RESTART IDENTITY CASCADE");

                foreach (string table in mine)
                {
                    if (!imported.Add(table)) throw new InvalidOperationException($"{table} is present in more than one file.");
                    long rows = copy(pg, lite, table);
                    resetIdentity(pg, table, counters.GetValueOrDefault(table));
                    log($"  {table}: {rows} rows");
                }
            }

            // a row count that differs means a row was lost or duplicated: nothing is committed in that case.
            tx.Commit();
            pg.Exec("ANALYZE");
        }

        private static string quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private static long copy(NpgsqlConnection pg, SqliteConnection lite, string table)
        {
            var target = pg.Query("SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @t ORDER BY ordinal_position",
                r => (name: r.GetString(0), type: r.GetString(1)), ("@t", table));

            var source = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = lite.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM pragma_table_info($t)";
                cmd.Parameters.AddWithValue("$t", table);
                using var r = cmd.ExecuteReader();
                while (r.Read()) source.Add(r.GetString(0));
            }

            var columns = target.Where(c => source.Contains(c.name)).ToList();
            if (columns.Count == 0) return 0;

            string list = string.Join(", ", columns.Select(c => quote(c.name)));
            long expected;
            using (var cmd = lite.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {quote(table)}";
                expected = (long)cmd.ExecuteScalar()!;
            }

            long rows = 0;
            using (var cmd = lite.CreateCommand())
            {
                // insertion order is kept, so the columns that replace SQLite's rowid number the rows the same way.
                cmd.CommandText = $"SELECT {list} FROM {quote(table)} ORDER BY rowid";
                SqliteDataReader reader;
                try { reader = cmd.ExecuteReader(); }
                catch (SqliteException)
                {
                    cmd.CommandText = $"SELECT {list} FROM {quote(table)}";
                    reader = cmd.ExecuteReader();
                }

                using (reader)
                using (var writer = pg.BeginBinaryImport($"COPY {quote(table)} ({list}) FROM STDIN (FORMAT BINARY)"))
                {
                    while (reader.Read())
                    {
                        writer.StartRow();
                        for (int i = 0; i < columns.Count; i++)
                        {
                            if (reader.IsDBNull(i)) { writer.WriteNull(); continue; }
                            object value = reader.GetValue(i);
                            switch (columns[i].type)
                            {
                                case "bigint":
                                    writer.Write(integer(value, table, columns[i].name), NpgsqlDbType.Bigint);
                                    break;
                                case "integer":
                                    writer.Write(checked((int)integer(value, table, columns[i].name)), NpgsqlDbType.Integer);
                                    break;
                                case "double precision":
                                    writer.Write(Convert.ToDouble(value, CultureInfo.InvariantCulture), NpgsqlDbType.Double);
                                    break;
                                case "bytea":
                                    writer.Write(value as byte[] ?? System.Text.Encoding.UTF8.GetBytes(text(value)), NpgsqlDbType.Bytea);
                                    break;
                                case "text":
                                    writer.Write(text(value), NpgsqlDbType.Text);
                                    break;
                                default: // citext and friends: sent as text, cast by the server
                                    writer.Write(text(value), NpgsqlDbType.Unknown);
                                    break;
                            }
                        }
                        rows++;
                    }
                    writer.Complete();
                }
            }

            long landed = pg.Scalar<long>($"SELECT COUNT(*) FROM {quote(table)}");
            if (rows != expected || landed != expected)
                throw new InvalidOperationException($"{table}: {expected} rows in SQLite, {rows} read, {landed} in PostgreSQL.");
            return rows;
        }

        private static long integer(object value, string table, string column) => value switch
        {
            long l => l,
            double d => checked((long)Math.Round(d)),
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => checked((long)Math.Round(d)),
            _ => throw new InvalidOperationException($"{table}.{column}: '{value}' is not a number."),
        };

        // PostgreSQL text cannot hold a NUL character.
        private static string text(object value) => value switch
        {
            string s => s.Replace("\0", ""),
            byte[] b => System.Text.Encoding.UTF8.GetString(b).Replace("\0", ""),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        // identity columns continue after the highest id ever handed out, as AUTOINCREMENT did: an id is never reused.
        private static void resetIdentity(NpgsqlConnection pg, string table, long counter)
        {
            var identities = pg.Query("SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @t AND is_identity = 'YES'",
                r => r.GetString(0), ("@t", table));
            foreach (string column in identities)
            {
                long highest = pg.Scalar<long?>($"SELECT MAX({quote(column)}) FROM {quote(table)}") ?? 0;
                if (column == "id") highest = Math.Max(highest, counter);
                if (highest > 0)
                    pg.Exec($"SELECT setval(pg_get_serial_sequence('{table}', '{column}'), {highest.ToString(CultureInfo.InvariantCulture)})");
            }
        }
    }
}
