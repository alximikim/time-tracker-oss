using Microsoft.Data.Sqlite;

namespace TimeTracker.Server.Data;

/// <summary>
/// Хранилище сессий на SQLite — полная замена Google-таблице из старого
/// Apps Script backend'а. Тот же стиль, что и у клиентского LocalDb.cs:
/// сырой ADO.NET, без ORM, WAL + busy_timeout на каждом соединении.
/// </summary>
public class ServerDb
{
    private readonly string _connectionString;

    /// <summary>
    /// Единый лок вокруг check-then-act в Handle*Async — прямой аналог
    /// LockService.getScriptLock() из старого Apps Script backend'а. При
    /// ~15 сотрудниках, синкающихся раз в 20с-5мин, цена сериализации
    /// незаметна; WAL/busy_timeout остаются доп. защитой и не блокируют
    /// чтение из админ-панели.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ServerDb(string dbPath)
    {
        var fullPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = $"Data Source={fullPath}";
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Sessions (
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              SessionId TEXT NOT NULL,
              EmployeeId TEXT NOT NULL,
              EmployeeName TEXT NOT NULL,
              MachineId TEXT NOT NULL,
              StartedAt TEXT NULL,
              EndedAt TEXT NULL,
              Hours REAL NULL,
              Status TEXT NULL,
              CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
              UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Sessions_SessionId ON Sessions(SessionId);
            CREATE INDEX IF NOT EXISTS IX_Sessions_EmployeeId ON Sessions(EmployeeId);
            CREATE INDEX IF NOT EXISTS IX_Sessions_StartedAt ON Sessions(StartedAt);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// eventType "start" — создаёт строку сессии, если её ещё нет
    /// (идемпотентно — повтор после обрыва связи не плодит дубли).
    /// </summary>
    public async Task<(bool Ok, string Message)> HandleStartAsync(
        string sessionId, string employeeId, string employeeName, string machineId, DateTime startedAt)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO Sessions
                    (SessionId, EmployeeId, EmployeeName, MachineId, StartedAt, UpdatedAt)
                VALUES
                    ($sessionId, $employeeId, $employeeName, $machineId, $startedAt, datetime('now'));
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$employeeId", employeeId);
            cmd.Parameters.AddWithValue("$employeeName", employeeName);
            cmd.Parameters.AddWithValue("$machineId", machineId);
            cmd.Parameters.AddWithValue("$startedAt", startedAt);

            var inserted = cmd.ExecuteNonQuery();
            return inserted > 0 ? (true, "started") : (true, "session already exists");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// eventType "stop" — закрывает существующую строку сессии, либо создаёт
    /// её "на лету", если stop пришёл без предшествующего start (гонка/потеря
    /// start). Если строка уже закрыта — no-op.
    /// </summary>
    public async Task<(bool Ok, string Message)> HandleStopAsync(
        string sessionId, string employeeId, string employeeName, string machineId,
        DateTime endedAt, string? stopStatus)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = Open();

            DateTime? startedAt = null;
            var exists = false;
            var alreadyClosed = false;

            using (var find = conn.CreateCommand())
            {
                find.CommandText = "SELECT StartedAt, EndedAt FROM Sessions WHERE SessionId = $sessionId;";
                find.Parameters.AddWithValue("$sessionId", sessionId);
                using var reader = find.ExecuteReader();
                if (reader.Read())
                {
                    exists = true;
                    startedAt = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
                    alreadyClosed = !reader.IsDBNull(1);
                }
            }

            if (!exists)
            {
                using var insert = conn.CreateCommand();
                insert.CommandText = """
                    INSERT INTO Sessions
                        (SessionId, EmployeeId, EmployeeName, MachineId, EndedAt, Status, UpdatedAt)
                    VALUES
                        ($sessionId, $employeeId, $employeeName, $machineId, $endedAt, $status, datetime('now'));
                    """;
                insert.Parameters.AddWithValue("$sessionId", sessionId);
                insert.Parameters.AddWithValue("$employeeId", employeeId);
                insert.Parameters.AddWithValue("$employeeName", employeeName);
                insert.Parameters.AddWithValue("$machineId", machineId);
                insert.Parameters.AddWithValue("$endedAt", endedAt);
                insert.Parameters.AddWithValue("$status", (object?)stopStatus ?? "кнопка");
                insert.ExecuteNonQuery();
                return (true, "stop без start — создана строка");
            }

            if (alreadyClosed)
                return (true, "session already closed");

            double? hours = startedAt.HasValue
                ? Math.Round((endedAt - startedAt.Value).TotalHours, 2)
                : null;

            using var update = conn.CreateCommand();
            update.CommandText = """
                UPDATE Sessions
                SET EndedAt = $endedAt, Hours = $hours, Status = $status, UpdatedAt = datetime('now')
                WHERE SessionId = $sessionId AND EndedAt IS NULL;
                """;
            update.Parameters.AddWithValue("$endedAt", endedAt);
            update.Parameters.AddWithValue("$hours", (object?)hours ?? DBNull.Value);
            update.Parameters.AddWithValue("$status", (object?)stopStatus ?? "кнопка");
            update.Parameters.AddWithValue("$sessionId", sessionId);
            update.ExecuteNonQuery();

            return (true, "stopped");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// eventType "heartbeat" — только обновляет UpdatedAt существующей
    /// строки; no-op, если сессия не найдена (закрытую сессию тоже трогает —
    /// как и в старом Apps Script backend'е).
    /// </summary>
    public async Task<(bool Ok, string Message)> HandleHeartbeatAsync(string sessionId)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Sessions SET UpdatedAt = datetime('now') WHERE SessionId = $sessionId;";
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            var affected = cmd.ExecuteNonQuery();
            return affected > 0 ? (true, "heartbeat") : (true, "session not found, heartbeat ignored");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Ручная правка из админ-панели. Пересчитывает Hours той же
    /// формулой, что HandleStopAsync. EmployeeId/EmployeeName/MachineId
    /// намеренно не параметры — их нельзя поменять через эту форму, чтобы
    /// не расколоть группировку в GetTotals/GetDistinctEmployees.</summary>
    public async Task<bool> UpdateSessionAsync(string sessionId, DateTime? startedAt, DateTime? endedAt, string? status)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();

            double? hours = (startedAt.HasValue && endedAt.HasValue)
                ? Math.Round((endedAt.Value - startedAt.Value).TotalHours, 2)
                : null;

            cmd.CommandText = """
                UPDATE Sessions
                SET StartedAt = $startedAt, EndedAt = $endedAt, Hours = $hours, Status = $status, UpdatedAt = datetime('now')
                WHERE SessionId = $sessionId;
                """;
            cmd.Parameters.AddWithValue("$startedAt", (object?)startedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$endedAt", (object?)endedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hours", (object?)hours ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            var affected = cmd.ExecuteNonQuery();
            return affected > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Sessions WHERE SessionId = $sessionId;";
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            var affected = cmd.ExecuteNonQuery();
            return affected > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public List<SessionRow> GetSessions(SessionFilter filter)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = BuildWhereClause(cmd, filter);
        cmd.CommandText = $"""
            SELECT SessionId, EmployeeId, EmployeeName, MachineId, StartedAt, EndedAt, Hours, Status, UpdatedAt
            FROM Sessions
            WHERE 1=1{where}
            ORDER BY COALESCE(StartedAt, EndedAt) DESC;
            """;

        var result = new List<SessionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SessionRow(
                SessionId: reader.GetString(0),
                EmployeeId: reader.GetString(1),
                EmployeeName: reader.GetString(2),
                MachineId: reader.GetString(3),
                StartedAt: reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                EndedAt: reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                Hours: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                Status: reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAt: reader.GetDateTime(8)));
        }
        return result;
    }

    public List<EmployeeTotal> GetTotals(SessionFilter filter)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = BuildWhereClause(cmd, filter);
        cmd.CommandText = $"""
            SELECT EmployeeId, EmployeeName, SUM(Hours)
            FROM Sessions
            WHERE Hours IS NOT NULL{where}
            GROUP BY EmployeeId, EmployeeName
            ORDER BY EmployeeName;
            """;

        var result = new List<EmployeeTotal>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new EmployeeTotal(reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
        }
        return result;
    }

    public List<(string EmployeeId, string EmployeeName)> GetDistinctEmployees()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT EmployeeId, EmployeeName FROM Sessions ORDER BY EmployeeName;";

        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public SessionRow? GetSessionById(string sessionId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SessionId, EmployeeId, EmployeeName, MachineId, StartedAt, EndedAt, Hours, Status, UpdatedAt
            FROM Sessions
            WHERE SessionId = $sessionId;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new SessionRow(
            SessionId: reader.GetString(0),
            EmployeeId: reader.GetString(1),
            EmployeeName: reader.GetString(2),
            MachineId: reader.GetString(3),
            StartedAt: reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            EndedAt: reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            Hours: reader.IsDBNull(6) ? null : reader.GetDouble(6),
            Status: reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedAt: reader.GetDateTime(8));
    }

    /// <summary>Часы по дням для дашборда — той же формой фильтрации, что
    /// GetTotals, но группировка по дню вместо сотрудника.</summary>
    public List<DailyTotal> GetDailyTotals(SessionFilter filter)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = BuildWhereClause(cmd, filter);
        cmd.CommandText = $"""
            SELECT date(COALESCE(StartedAt, EndedAt)) AS Day, SUM(Hours)
            FROM Sessions
            WHERE Hours IS NOT NULL{where}
            GROUP BY Day
            ORDER BY Day;
            """;

        var result = new List<DailyTotal>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DailyTotal(
                DateOnly.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetDouble(1)));
        }
        return result;
    }

    private static string BuildWhereClause(SqliteCommand cmd, SessionFilter filter)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrEmpty(filter.EmployeeId))
        {
            clauses.Add("EmployeeId = $employeeId");
            cmd.Parameters.AddWithValue("$employeeId", filter.EmployeeId);
        }
        if (filter.From is { } from)
        {
            clauses.Add("date(COALESCE(StartedAt, EndedAt)) >= date($from)");
            cmd.Parameters.AddWithValue("$from", from.ToDateTime(TimeOnly.MinValue));
        }
        if (filter.To is { } to)
        {
            clauses.Add("date(COALESCE(StartedAt, EndedAt)) <= date($to)");
            cmd.Parameters.AddWithValue("$to", to.ToDateTime(TimeOnly.MinValue));
        }

        return clauses.Count == 0 ? "" : " AND " + string.Join(" AND ", clauses);
    }
}
