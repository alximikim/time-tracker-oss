using System.Text.Json;

namespace TimeTracker;

public class EmployeeConfig
{
    public string EmployeeId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
}

/// <summary>
/// Хранит выбор сотрудника в %AppData%\TimeTracker\config.json. Это единственная
/// настройка, которую вводит человек — адрес backend'а и токен зашиты в
/// сборке (см. ApiSettings), чтобы не просить сотрудников что-либо вставлять.
/// </summary>
public static class Config
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TimeTracker", "config.json");

    public static EmployeeConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<EmployeeConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(EmployeeConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public static string DataDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TimeTracker", "data.db");

    /// <summary>Слаг по умолчанию для ID сотрудника — из логина Windows.</summary>
    public static string DefaultEmployeeIdSlug()
    {
        var name = Environment.UserName.ToLowerInvariant();
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
