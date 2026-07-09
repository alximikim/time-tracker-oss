using Microsoft.Win32;

namespace TimeTracker;

/// <summary>
/// Автозапуск через HKCU Run key — не требует прав администратора (в отличие
/// от HKLM) и не требует COM/IShellLink для .lnk в папке автозагрузки.
/// Прописывается заново при каждом старте — самовосстанавливается, если exe
/// переместили.
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TimeTracker";

    public static void Ensure()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.SetValue(ValueName, $"\"{exePath}\"");
    }

    public static void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
