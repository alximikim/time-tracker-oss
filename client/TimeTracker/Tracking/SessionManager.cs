using TimeTracker.Storage;

namespace TimeTracker.Tracking;

public enum WorkState
{
    Stopped,
    Running,
    Paused
}

/// <summary>
/// Состояние Stopped/Running/Paused и вся логика вокруг него. Единственный
/// способ перейти в Running — явный вызов StartWork() (кнопка). Никакого
/// автозапуска сессии по включению компьютера и никакого детекта
/// бездействия — по требованию заказчика кнопка сама по себе и есть
/// механизм доверия; Paused — тоже явное действие сотрудника (кнопка
/// "Перерыв"), а не автоматика.
///
/// Пауза технически не отдельный протокол, а обычная пара стоп/старт с
/// пометкой StopStatus.Break — сервер уже суммирует часы по всем сессиям
/// сотрудника за день, так что "10:00 старт, 13:00 перерыв, 14:00
/// продолжение, 18:00 стоп" даёт правильный итог без единой строчки нового
/// кода на сервере.
/// </summary>
public class SessionManager
{
    private readonly LocalDb _db;
    private readonly EmployeeConfig _employee;
    private readonly string _machineId;
    private readonly Action _requestImmediateSync;
    private readonly double? _longSessionReminderHours;

    private readonly System.Windows.Forms.Timer _heartbeatTimer;
    private int _ticksSinceLastQueuedHeartbeat;
    private const int HeartbeatTickSeconds = 60;
    private const int QueuedHeartbeatEveryNTicks = 5; // ~5 минут

    private bool _longSessionNotified;
    private DateTime _longSessionNotifiedDate;

    /// <summary>Часы по уже закрытым сегодняшним сессиям на момент старта
    /// текущей — считается один раз в StartWork(), а не на каждый тик UI,
    /// чтобы не дёргать SQLite раз в секунду.</summary>
    private double _todaysCompletedHoursBeforeCurrentSession;

    public WorkState State { get; private set; } = WorkState.Stopped;
    public string? CurrentSessionId { get; private set; }
    public DateTime? CurrentSessionStart { get; private set; }

    /// <summary>Сколько часов отработано сегодня всего — сумма уже закрытых
    /// сессий (до перерыва/перезапуска) плюс идущий отрезок текущей, если
    /// сессия активна. Используется и для тултипа трея (счётчик не сбрасывается
    /// в 0 после перерыва/перезапуска в течение дня), и для напоминания о
    /// длинной сессии.</summary>
    public double TodaysElapsedHours => CurrentSessionStart is { } start
        ? _todaysCompletedHoursBeforeCurrentSession + (DateTime.Now - start).TotalHours
        : _todaysCompletedHoursBeforeCurrentSession;

    public event Action? StateChanged;

    /// <summary>Срабатывает не чаще раза в день, когда суммарные часы за
    /// сегодня (все сессии, включая уже закрытые перед перерывом) впервые
    /// достигают настроенного порога. Аргумент — сколько часов уже
    /// отработано. Порог не задан (null) — событие не срабатывает никогда.</summary>
    public event Action<double>? LongSessionReminderDue;

    public SessionManager(
        LocalDb db, EmployeeConfig employee, string machineId, Action requestImmediateSync,
        double? longSessionReminderHours = null)
    {
        _db = db;
        _employee = employee;
        _machineId = machineId;
        _requestImmediateSync = requestImmediateSync;
        _longSessionReminderHours = longSessionReminderHours;

        _heartbeatTimer = new System.Windows.Forms.Timer { Interval = HeartbeatTickSeconds * 1000 };
        _heartbeatTimer.Tick += (_, _) => OnHeartbeatTick();
    }

    /// <summary>
    /// Вызывается один раз при старте приложения — до того, как что-либо
    /// ещё трогает состояние. Закрывает "осиротевшую" сессию, если предыдущий
    /// запуск не завершился штатно (крэш процесса, BSOD, отключение питания —
    /// случаи, когда ShutdownWatcher не успевает сработать). Приложение всегда
    /// стартует в Stopped: сотрудник обязан заново нажать "Начать работу".
    /// </summary>
    public void RecoverOrphanedSessionIfAny()
    {
        var orphan = _db.FindOrphanedOpenSession();
        if (orphan is null) return;

        _db.InsertEvent(new TrackedEvent
        {
            SessionId = orphan.SessionId,
            EventType = EventType.Stop,
            ClientTimestamp = orphan.LastHeartbeat,
            EmployeeId = orphan.EmployeeId,
            EmployeeName = orphan.EmployeeName,
            MachineId = orphan.MachineId,
            StopStatus = Storage.StopStatus.RecoveredAfterCrash
        });

        _requestImmediateSync();
    }

    /// <summary>Начинает новую сессию — как первый запуск за день, так и
    /// возвращение с перерыва (Paused -> Running).</summary>
    public void StartWork()
    {
        if (State == WorkState.Running) return;

        var sessionId = Guid.NewGuid().ToString();
        var now = DateTime.Now;
        _todaysCompletedHoursBeforeCurrentSession = _db.GetTodaysCompletedHours();

        _db.InsertEvent(new TrackedEvent
        {
            SessionId = sessionId,
            EventType = EventType.Start,
            ClientTimestamp = now,
            EmployeeId = _employee.EmployeeId,
            EmployeeName = _employee.EmployeeName,
            MachineId = _machineId
        });

        CurrentSessionId = sessionId;
        CurrentSessionStart = now;
        State = WorkState.Running;
        _ticksSinceLastQueuedHeartbeat = 0;
        _heartbeatTimer.Start();

        _requestImmediateSync();
        StateChanged?.Invoke();
    }

    /// <summary>Остановка вручную, кнопкой из трей-меню — работает и из
    /// Running (закрывает текущую сессию), и из Paused (сессия уже закрыта
    /// перерывом, тут только переключаем UI-состояние на "остановлено").</summary>
    public void StopWork()
    {
        switch (State)
        {
            case WorkState.Running:
                StopInternal(Storage.StopStatus.Button, WorkState.Stopped);
                break;
            case WorkState.Paused:
                State = WorkState.Stopped;
                StateChanged?.Invoke();
                break;
        }
    }

    /// <summary>Остановка при выключении/выходе из Windows (см.
    /// ShutdownWatcher) — актуальна только для Running: если сотрудник был
    /// на паузе, открытой сессии уже нет и закрывать нечего.</summary>
    public void StopFromShutdown() => StopInternal(Storage.StopStatus.Shutdown, WorkState.Stopped);

    /// <summary>Перерыв (обед, отлучка) — закрывает текущую сессию со
    /// статусом "перерыв", в отличие от StopWork не считается концом
    /// рабочего дня. Доступность кнопки в UI — по конфигу EnableBreakButton,
    /// не все компании хотят эту функцию.</summary>
    public void PauseWork() => StopInternal(Storage.StopStatus.Break, WorkState.Paused);

    private void StopInternal(string stopStatus, WorkState resultState)
    {
        if (State != WorkState.Running || CurrentSessionId is null) return;

        _db.InsertEvent(new TrackedEvent
        {
            SessionId = CurrentSessionId,
            EventType = EventType.Stop,
            ClientTimestamp = DateTime.Now,
            EmployeeId = _employee.EmployeeId,
            EmployeeName = _employee.EmployeeName,
            MachineId = _machineId,
            StopStatus = stopStatus
        });

        _heartbeatTimer.Stop();
        CurrentSessionId = null;
        CurrentSessionStart = null;
        State = resultState;

        _requestImmediateSync();
        StateChanged?.Invoke();
    }

    private void OnHeartbeatTick()
    {
        if (State != WorkState.Running || CurrentSessionId is null) return;

        var now = DateTime.Now;
        _db.TouchHeartbeat(CurrentSessionId, now);
        CheckLongSessionReminder(now);

        _ticksSinceLastQueuedHeartbeat++;
        if (_ticksSinceLastQueuedHeartbeat >= QueuedHeartbeatEveryNTicks)
        {
            _ticksSinceLastQueuedHeartbeat = 0;
            _db.InsertEvent(new TrackedEvent
            {
                SessionId = CurrentSessionId,
                EventType = EventType.Heartbeat,
                ClientTimestamp = now,
                EmployeeId = _employee.EmployeeId,
                EmployeeName = _employee.EmployeeName,
                MachineId = _machineId
            });
            _requestImmediateSync();
        }
    }

    private void CheckLongSessionReminder(DateTime now)
    {
        if (_longSessionReminderHours is not { } thresholdHours) return;
        if (_longSessionNotified && _longSessionNotifiedDate == now.Date) return;

        var todaysHours = TodaysElapsedHours;
        if (todaysHours < thresholdHours) return;

        _longSessionNotified = true;
        _longSessionNotifiedDate = now.Date;
        LongSessionReminderDue?.Invoke(todaysHours);
    }
}
