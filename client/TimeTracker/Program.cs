namespace TimeTracker;

internal static class Program
{
    private const string MutexName = "Global\\TimeTracker.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Учёт времени уже запущен — смотрите иконку в трее.",
                "Учёт времени", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var serverConfig = ServerConfigLoader.Load();
        if (serverConfig is null)
        {
            MessageBox.Show(
                "Не найден или повреждён файл server-config.json рядом с приложением.\n" +
                "Обратитесь к администратору для настройки адреса сервера.",
                "Учёт времени — ошибка конфигурации",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext(serverConfig));
    }
}
