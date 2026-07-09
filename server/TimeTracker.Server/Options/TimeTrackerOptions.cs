namespace TimeTracker.Server.Options;

/// <summary>
/// Секреты (SharedToken/AdminPassword) намеренно не имеют значений по
/// умолчанию — appsettings.json, который лежит в репозитории, их не
/// содержит. Задаются через переменные окружения (TimeTracker__SharedToken
/// и т.п.) либо через gitignored appsettings.Local.json — см.
/// docs/DEPLOY-SERVER.md.
/// </summary>
public class TimeTrackerOptions
{
    public string SharedToken { get; set; } = "";
    public string AdminUsername { get; set; } = "admin";
    public string AdminPassword { get; set; } = "";
    public string DbPath { get; set; } = "data/timetracker.db";
    public string ListenUrl { get; set; } = "http://0.0.0.0:5140";

    public void Validate(bool isDevelopment)
    {
        if (isDevelopment) return;

        if (string.IsNullOrEmpty(SharedToken) || string.IsNullOrEmpty(AdminPassword))
        {
            throw new InvalidOperationException(
                "Не заданы TimeTracker:SharedToken / TimeTracker:AdminPassword. " +
                "Установите переменные окружения TimeTracker__SharedToken и " +
                "TimeTracker__AdminPassword, либо создайте appsettings.Local.json " +
                "рядом с exe (шаблон — appsettings.Example.json). См. docs/DEPLOY-SERVER.md.");
        }
    }
}
