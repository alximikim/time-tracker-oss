namespace TimeTracker.Server.Data;

public record SessionRow(
    string SessionId,
    string EmployeeId,
    string EmployeeName,
    string MachineId,
    DateTime? StartedAt,
    DateTime? EndedAt,
    double? Hours,
    string? Status,
    DateTime UpdatedAt);

public record EmployeeTotal(string EmployeeId, string EmployeeName, double TotalHours);

public record DailyTotal(DateOnly Day, double TotalHours);

public record SessionFilter(string? EmployeeId, DateOnly? From, DateOnly? To);
