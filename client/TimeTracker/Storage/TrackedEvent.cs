namespace TimeTracker.Storage;

public enum EventType
{
    Start,
    Stop,
    Heartbeat
}

public static class EventTypeExtensions
{
    public static string ToWireString(this EventType type) => type switch
    {
        EventType.Start => "start",
        EventType.Stop => "stop",
        EventType.Heartbeat => "heartbeat",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

public class TrackedEvent
{
    public long Id { get; set; }
    public required string SessionId { get; set; }
    public required EventType EventType { get; set; }
    public required DateTime ClientTimestamp { get; set; }
    public required string EmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public required string MachineId { get; set; }
    public string? StopStatus { get; set; }
    public bool Synced { get; set; }
}
