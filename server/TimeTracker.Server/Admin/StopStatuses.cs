namespace TimeTracker.Server.Admin;

/// <summary>
/// Копия значений из client/TimeTracker/Storage/StopStatus.cs — сервер не
/// ссылается на клиентский проект, поэтому список для выпадающего списка
/// в форме редактирования продублирован здесь вручную. Обновлять при
/// добавлении новых статусов на клиенте.
/// </summary>
public static class StopStatuses
{
    public static readonly string[] Known =
    [
        "кнопка", "выключение", "восстановлено-после-сбоя", "перерыв"
    ];
}
