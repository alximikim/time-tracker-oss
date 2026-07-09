using TimeTracker.Storage;

namespace TimeTracker.Sync;

/// <summary>
/// Фоновая отправка очереди на сервер. Это и есть "офлайн-режим": события
/// всегда пишутся в LocalDb немедленно и синхронно (см. SessionManager), а
/// этот сервис лишь досылает их, когда получится — сотруднику для этого
/// ничего делать не нужно.
/// </summary>
public class SyncService : IDisposable
{
    private const int BaseIntervalSeconds = 20;
    private const int MaxIntervalSeconds = 300;

    private readonly LocalDb _db;
    private readonly ApiClient _api;
    private readonly System.Windows.Forms.Timer _timer;

    private int _currentIntervalSeconds = BaseIntervalSeconds;
    private bool _syncInProgress;
    private bool _resyncRequested;

    public SyncService(LocalDb db, ApiClient api)
    {
        _db = db;
        _api = api;
        _timer = new System.Windows.Forms.Timer { Interval = BaseIntervalSeconds * 1000 };
        _timer.Tick += async (_, _) => await RunSyncCycleAsync();
    }

    public void Start() => _timer.Start();

    /// <summary>
    /// Вызывается сразу после любой локальной записи (старт/стоп/heartbeat),
    /// чтобы не ждать следующего тика таймера, когда есть сеть.
    /// </summary>
    public void RequestSyncNow()
    {
        if (_syncInProgress)
        {
            _resyncRequested = true;
            return;
        }

        _ = RunSyncCycleAsync();
    }

    private async Task RunSyncCycleAsync()
    {
        if (_syncInProgress) return;
        _syncInProgress = true;

        try
        {
            var pending = _db.GetUnsyncedEvents();
            var allSucceeded = true;

            foreach (var evt in pending)
            {
                var ok = await _api.SendEventAsync(evt);
                if (!ok)
                {
                    allSucceeded = false;
                    break;
                }
                _db.MarkSynced(evt.Id);
            }

            ApplyBackoff(success: allSucceeded);
        }
        catch (Exception)
        {
            // Например, локальная БД временно занята другим процессом —
            // не даём необработанному исключению потеряться в fire-and-forget
            // вызове (RequestSyncNow не await'ит эту задачу) и просто повторим
            // на следующем тике с бэкоффом, как при любой другой неудаче.
            ApplyBackoff(success: false);
        }
        finally
        {
            _syncInProgress = false;
            if (_resyncRequested)
            {
                _resyncRequested = false;
                _ = RunSyncCycleAsync();
            }
        }
    }

    /// <summary>
    /// Нет сети/сервер недоступен — увеличиваем интервал (20с → 40с → 80с →
    /// потолок 5 мин), чтобы не спамить запросами впустую. Любой успешный
    /// цикл сбрасывает интервал обратно на базовый.
    /// </summary>
    private void ApplyBackoff(bool success)
    {
        _currentIntervalSeconds = success
            ? BaseIntervalSeconds
            : Math.Min(_currentIntervalSeconds * 2, MaxIntervalSeconds);

        _timer.Interval = _currentIntervalSeconds * 1000;
    }

    public void Dispose() => _timer.Dispose();
}
