using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TimeTracker.Server.Data;
using TimeTracker.Server.Options;

namespace TimeTracker.Server.Events;

/// <summary>
/// POST /api/events — тот же JSON-контракт, что раньше принимал Apps
/// Script backend (см. старый Code.gs), чтобы клиентский ApiClient.cs почти
/// не менялся. Всегда отвечает HTTP 200 с {ok, message} — клиент трактует
/// неудачу только по ok != true, а не по HTTP-статусу.
/// </summary>
public static class EventsEndpoint
{
    public static void MapEventsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () =>
            Results.Json(new ApiResponse { Ok = true, Message = "TimeTracker server running" }));

        app.MapPost("/api/events", async (ApiEventPayload payload, ServerDb db, TimeTrackerOptions options) =>
        {
            if (!TokenMatches(payload.Token, options.SharedToken))
                return Results.Json(new ApiResponse { Ok = false, Message = "Неверный токен" });

            if (string.IsNullOrEmpty(payload.SessionId) || string.IsNullOrEmpty(payload.EventType))
                return Results.Json(new ApiResponse { Ok = false, Message = "Не хватает sessionId/eventType" });

            var timestamp = ParseTimestamp(payload.ClientTimestamp);

            var (ok, message) = payload.EventType switch
            {
                "start" => await db.HandleStartAsync(
                    payload.SessionId, payload.EmployeeId, payload.EmployeeName, payload.MachineId, timestamp),
                "stop" => await db.HandleStopAsync(
                    payload.SessionId, payload.EmployeeId, payload.EmployeeName, payload.MachineId, timestamp, payload.StopStatus),
                "heartbeat" => await db.HandleHeartbeatAsync(payload.SessionId),
                _ => (false, $"Неизвестный eventType: {payload.EventType}")
            };

            return Results.Json(new ApiResponse { Ok = ok, Message = message });
        });
    }

    private static bool TokenMatches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;

        var providedBytes = Encoding.UTF8.GetBytes(provided ?? "");
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (providedBytes.Length != expectedBytes.Length) return false;

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static DateTime ParseTimestamp(string? isoString)
    {
        if (string.IsNullOrEmpty(isoString)) return DateTime.Now;

        return DateTime.TryParse(isoString, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.Now;
    }
}
