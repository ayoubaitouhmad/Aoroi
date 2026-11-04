using Microsoft.Data.Sqlite;
using ParentalGuard.Core.Models;

namespace ParentalGuard.Service.Data;

/// <summary>
/// Durable, local store of usage. SQLite is a single file with no server to run, which is
/// exactly right here. We keep two things:
///   - usage_daily: one row per (user, day) with accumulated active seconds. Survives reboots,
///     so a restart cannot be used to reset the day's count.
///   - activity_log: an append-only audit of every heartbeat sample, for the parent's reports.
///
/// All methods are safe to call from the single IPC loop. SQLite handles its own locking;
/// we keep one connection open for the Service's lifetime.
/// </summary>
public sealed class UsageStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public UsageStore(string databaseFile)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _conn = new SqliteConnection(cs);
        _conn.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS usage_daily (
                user_name    TEXT NOT NULL,
                day          TEXT NOT NULL,          -- ISO yyyy-MM-dd (local day)
                active_secs  INTEGER NOT NULL DEFAULT 0,
                updated_at   TEXT NOT NULL,
                PRIMARY KEY (user_name, day)
            );

            CREATE TABLE IF NOT EXISTS activity_log (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                user_name    TEXT NOT NULL,
                sampled_at   TEXT NOT NULL,          -- ISO timestamp, local
                state        INTEGER NOT NULL,       -- ActivityState
                idle_secs    INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_activity_user_time
                ON activity_log (user_name, sampled_at);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Add <paramref name="seconds"/> of active time to today's tally for a user and return the
    /// new total in minutes. Uses UPSERT so the first sample of the day creates the row.
    /// </summary>
    public int AddActiveSeconds(string userName, DateOnly day, int seconds)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO usage_daily (user_name, day, active_secs, updated_at)
                VALUES ($u, $d, $s, $now)
                ON CONFLICT(user_name, day) DO UPDATE SET
                    active_secs = active_secs + excluded.active_secs,
                    updated_at  = excluded.updated_at;

                SELECT active_secs FROM usage_daily WHERE user_name = $u AND day = $d;
                """;
            cmd.Parameters.AddWithValue("$u", userName);
            cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$s", seconds);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));

            var total = Convert.ToInt32(cmd.ExecuteScalar());
            return total / 60;
        }
    }

    /// <summary>Minutes of active time already recorded for a user on a given day.</summary>
    public int GetUsedMinutes(string userName, DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT active_secs FROM usage_daily WHERE user_name = $u AND day = $d;";
            cmd.Parameters.AddWithValue("$u", userName);
            cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? 0 : Convert.ToInt32(result) / 60;
        }
    }

    /// <summary>Append one raw sample to the audit log for the parent's reports.</summary>
    public void LogSample(string userName, DateTimeOffset at, ActivityState state, int idleSeconds)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO activity_log (user_name, sampled_at, state, idle_secs)
                VALUES ($u, $at, $st, $idle);
                """;
            cmd.Parameters.AddWithValue("$u", userName);
            cmd.Parameters.AddWithValue("$at", at.ToString("o"));
            cmd.Parameters.AddWithValue("$st", (int)state);
            cmd.Parameters.AddWithValue("$idle", idleSeconds);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
