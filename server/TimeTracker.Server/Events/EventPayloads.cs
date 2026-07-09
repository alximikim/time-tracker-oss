using System.Text.Json.Serialization;

namespace TimeTracker.Server.Events;

public class ApiEventPayload
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("employeeId")] public string EmployeeId { get; set; } = "";
    [JsonPropertyName("employeeName")] public string EmployeeName { get; set; } = "";
    [JsonPropertyName("eventType")] public string EventType { get; set; } = "";
    [JsonPropertyName("clientTimestamp")] public string ClientTimestamp { get; set; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("machineId")] public string MachineId { get; set; } = "";
    [JsonPropertyName("stopStatus")] public string? StopStatus { get; set; }
}

public class ApiResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}
