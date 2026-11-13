// xc-osu! test helper — a throwaway PostgreSQL database per suite, so a test run can never touch the live one.

using Npgsql;

sealed class TestDatabase : IDisposable
{
    private const string ADMIN = "Host=/var/run/postgresql;Database=postgres";

    public string Name { get; }
    public string ConnectionString { get; }

    public TestDatabase()
    {
        Name = "xc_test_" + Guid.NewGuid().ToString("N");
        ConnectionString = "Host=/var/run/postgresql;Database=" + Name;
        using var c = new NpgsqlConnection(ADMIN);
        c.Open();
        using var cmd = c.CreateCommand();
        // same shape as the live database: UTF8, byte order for comparisons.
        cmd.CommandText = $"CREATE DATABASE \"{Name}\" TEMPLATE template0 ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C'";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        using var c = new NpgsqlConnection(ADMIN);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }
}
