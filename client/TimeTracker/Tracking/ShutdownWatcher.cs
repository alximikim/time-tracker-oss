using Microsoft.Win32;

namespace TimeTracker.Tracking;

/// <summary>
/// Ловит выключение/выход из Windows через SystemEvents.SessionEnding —
/// срабатывает на logoff и shutdown/restart, но НЕ на блокировку экрана
/// (это правильно: блокировка не должна завершать рабочую сессию). Работает
/// даже без видимого Form, пока какой-то поток процесса крутит цикл
/// сообщений (у нас это Application.Run(TrayAppContext) в Program.cs).
///
/// Известный пробел: kill процесса из диспетчера задач, BSOD, отключение
/// питания — SessionEnding в этих случаях не успевает сработать. Это
/// покрывается отдельным механизмом — SessionManager.RecoverOrphanedSessionIfAny()
/// при следующем запуске, по последнему heartbeat.
/// </summary>
public class ShutdownWatcher : IDisposable
{
    private readonly SessionManager _sessionManager;

    public ShutdownWatcher(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        // Не отменяем завершение сессии — просто успеваем зафиксировать стоп
        // локально (быстро, синхронно, гарантированно успевает до закрытия
        // процесса). Отправка на сервер — best-effort здесь, а гарантированно
        // досылается фоновым синком при следующем запуске приложения.
        _sessionManager.StopFromShutdown();
    }

    public void Dispose()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
    }
}
