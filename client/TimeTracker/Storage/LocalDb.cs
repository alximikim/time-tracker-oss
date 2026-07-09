using Microsoft.Data.Sqlite;

namespace TimeTracker.Storage;

/// <summary>
/// Локальная очередь событий на SQLite. Единственный источник истины на
/// клиенте: пишем сюда синхронно на каждое действие (старт/стоп/heartbeat),
/// а фоновый синк уже сам разбирается, что и когда отправлять на сервер.
/// </summary>
public class LocalDb
{
    private readonly string _connectionString;

    public LocalDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // WAL позволяет читателям (например, сторонним SQLite-браузерам,
        // которыми кто-то откроет файл для отладки) не блокировать наши
        // собственные записи; busy_timeout заставляет SQLite подождать и
        // повторить попытку вместо немедленного "database is locked".
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
            CREATE TABLE IF NOT EXISTS Events (
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              SessionId TEXT NOT NULL,
              EventType TEXT NOT NULL,
              ClientTimestamp TEXT NOT NULL,
              EmployeeId TEXT NOT NULL,
              EmployeeName TEXT NOT NULL,
              MachineId TEXT NOT NULL,
              StopStatus TEXT NULL,
              LastHeartbeat TEXT NULL,
              Synced INTEGER NOT NULL DEFAULT 0,
              SyncedAt TEXT NULL,
              CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS IX_Events_Synced ON Events(Synced);
            CREATE INDEX IF NOT EXISTS IX_Events_SessionId ON Events(SessionId);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Вставляет новое событие в очередь и возвращает его Id.</summary>
    public long InsertEvent(TrackedEvent evt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Events
                (SessionId, EventType, ClientTimestamp, EmployeeId, EmployeeName, MachineId, StopStatus, LastHeartbeat, Synced)
            VALUES
                ($sessionId, $eventType, $clientTimestamp, $employeeId, $employeeName, $machineId, $stopStatus, $lastHeartbeat, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$sessionId", evt.SessionId);
        cmd.Parameters.AddWithValue("$eventType", evt.EventType.ToWireString());
        cmd.Parameters.AddWithValue("$clientTimestamp", evt.ClientTimestamp.ToString("o"));
        cmd.Parameters.AddWithValue("$employeeId", evt.EmployeeId);
        cmd.Parameters.AddWithValue("$employeeName", evt.EmployeeName);
        cmd.Parameters.AddWithValue("$machineId", evt.MachineId);
        cmd.Parameters.AddWithValue("$stopStatus", (object?)evt.StopStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastHeartbeat",
            evt.EventType == EventType.Start ? evt.ClientTimestamp.ToString("o") : (object)DBNull.Value);

        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Обновляет LastHeartbeat на строке 'start' активной сессии — маркер
    /// живости, по которому при следующем запуске можно закрыть "осиротевшую"
    /// сессию (крэш/BSOD/отключение питания), не дожидаясь SessionEnding.
    /// </summary>
    public void TouchHeartbeat(string sessionId, DateTime timestamp)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Events SET LastHeartbeat = $ts
            WHERE SessionId = $sessionId AND EventType = 'start';
            """;
        cmd.Parameters.AddWithValue("$ts", timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Все ещё не отправленные события, в порядке создания.</summary>
    public List<TrackedEvent> GetUnsyncedEvents()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, SessionId, EventType, ClientTimestamp, EmployeeId, EmployeeName, MachineId, StopStatus
            FROM Events
            WHERE Synced = 0
            ORDER BY Id ASC;
            """;

        var result = new List<TrackedEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TrackedEvent
            {
                Id = reader.GetInt64(0),
                SessionId = reader.GetString(1),
                EventType = ParseEventType(reader.GetString(2)),
                ClientTimestamp = DateTime.Parse(reader.GetString(3)),
                EmployeeId = reader.GetString(4),
                EmployeeName = reader.GetString(5),
                MachineId = reader.GetString(6),
                StopStatus = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }
        return result;
    }

    public void MarkSynced(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Events SET Synced = 1, SyncedAt = datetime('now')
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Открытая сессия — есть 'start' без парного 'stop'. Используется при
    /// запуске приложения, чтобы обнаружить сессию, "осиротевшую" из-за
    /// крэша/BSOD/отключения питания (SessionEnding в таких случаях не
    /// успевает сработать).
    /// </summary>
    public OpenSession? FindOrphanedOpenSession()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.SessionId, s.EmployeeId, s.EmployeeName, s.MachineId, s.LastHeartbeat
            FROM Events s
            WHERE s.EventType = 'start'
              AND NOT EXISTS (
                  SELECT 1 FROM Events e
                  WHERE e.EventType = 'stop' AND e.SessionId = s.SessionId
              )
            ORDER BY s.Id DESC
            LIMIT 1;
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new OpenSession(
            SessionId: reader.GetString(0),
            EmployeeId: reader.GetString(1),
            EmployeeName: reader.GetString(2),
            MachineId: reader.GetString(3),
            LastHeartbeat: reader.IsDBNull(4) ? DateTime.Now : DateTime.Parse(reader.GetString(4))
        );
    }

    private static EventType ParseEventType(string value) => value switch
    {
        "start" => EventType.Start,
        "stop" => EventType.Stop,
        "heartbeat" => EventType.Heartbeat,
        _ => throw new InvalidOperationException($"Неизвестный EventType в базе: {value}")
    };

    /// <summary>
    /// Сумма часов по всем сегодняшним сессиям, у которых есть и start, и
    /// stop (открытая текущая сессия сюда не попадает — у неё ещё нет stop).
    /// Нужна для напоминания "отработано уже N часов" — считает время до
    /// перерывов и после них вместе. При ~паре сессий в день на человека
    /// группировка в C#, а не в SQL, — самый простой и надёжный вариант.
    /// </summary>
    public double GetTodaysCompletedHours()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SessionId, EventType, ClientTimestamp
            FROM Events
            WHERE EventType IN ('start', 'stop');
            """;

        var starts = new Dictionary<string, DateTime>();
        var stops = new Dictionary<string, DateTime>();

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var sessionId = reader.GetString(0);
                var eventType = reader.GetString(1);
                var timestamp = DateTime.Parse(reader.GetString(2));

                if (eventType == "start") starts[sessionId] = timestamp;
                else stops[sessionId] = timestamp;
            }
        }

        var today = DateTime.Today;
        var totalHours = 0.0;
        foreach (var (sessionId, start) in starts)
        {
            if (start.Date == today && stops.TryGetValue(sessionId, out var stop))
            {
                totalHours += (stop - start).TotalHours;
            }
        }
        return totalHours;
    }
}

public record OpenSession(
    string SessionId,
    string EmployeeId,
    string EmployeeName,
    string MachineId,
    DateTime LastHeartbeat);
