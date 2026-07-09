using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TimeTracker.Storage;

namespace TimeTracker.Sync;

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

/// <summary>
/// Тонкая обёртка над HttpClient. Один общий экземпляр HttpClient на
/// процесс (рекомендация .NET — избегаем исчерпания сокетов при частых
/// коротких запросах).
/// </summary>
public class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ServerConfig _config;

    public ApiClient(ServerConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<bool> SendEventAsync(TrackedEvent evt, CancellationToken ct = default)
    {
        var payload = new ApiEventPayload
        {
            Token = _config.SharedToken,
            EmployeeId = evt.EmployeeId,
            EmployeeName = evt.EmployeeName,
            EventType = evt.EventType.ToWireString(),
            ClientTimestamp = evt.ClientTimestamp.ToString("o"),
            SessionId = evt.SessionId,
            MachineId = evt.MachineId,
            StopStatus = evt.StopStatus
        };

        try
        {
            using var response = await _http.PostAsJsonAsync(_config.ApiUrl, payload, ct);
            if (!response.IsSuccessStatusCode) return false;

            var result = await response.Content.ReadFromJsonAsync<ApiResponse>(cancellationToken: ct);
            return result?.Ok == true;
        }
        catch (Exception)
        {
            // Нет сети, таймаут, DNS и т.п. — считаем неудачей, SyncService сам
            // применит backoff и повторит позже. Здесь не логируем в UI.
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
