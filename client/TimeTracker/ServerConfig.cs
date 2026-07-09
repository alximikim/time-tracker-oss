using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeTracker;

public class ServerConfig
{
    [JsonPropertyName("apiUrl")] public string ApiUrl { get; set; } = "";
    [JsonPropertyName("sharedToken")] public string SharedToken { get; set; } = "";

    /// <summary>Показывать ли кнопку "Перерыв" в трее — не всем компаниям
    /// нужен учёт обедов/отлучек, поэтому по умолчанию выключено.</summary>
    [JsonPropertyName("enableBreakButton")] public bool EnableBreakButton { get; set; }

    /// <summary>Порог для уведомления "вы сегодня отработали уже N часов" —
    /// null (не задано в конфиге) означает, что уведомление отключено.</summary>
    [JsonPropertyName("longSessionReminderHours")] public double? LongSessionReminderHours { get; set; }
}

/// <summary>
/// Читает адрес сервера и токен из server-config.json рядом с exe
/// (готовится администратором один раз перед раздачей сотрудникам — см.
/// docs/CLIENT-SETUP.md). Сотрудники этот файл не видят и не трогают, как и
/// раньше — единственное, что спрашивает у них приложение, это имя
/// (см. FirstRunForm).
/// </summary>
public static class ServerConfigLoader
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "server-config.json");

    public static ServerConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;

        try
        {
            var config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(ConfigPath));
            if (config is null) return null;
            if (string.IsNullOrWhiteSpace(config.ApiUrl) || string.IsNullOrWhiteSpace(config.SharedToken))
                return null;
            if (!Uri.TryCreate(config.ApiUrl, UriKind.Absolute, out _)) return null;

            return config;
        }
        catch
        {
            return null;
        }
    }
}
