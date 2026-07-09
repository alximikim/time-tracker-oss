using System.Globalization;
using System.Text;
using TimeTracker.Server.Data;
using TimeTracker.Server.Options;

namespace TimeTracker.Server.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var options = ctx.HttpContext.RequestServices.GetRequiredService<TimeTrackerOptions>();
            if (!AdminAuth.TryAuthenticate(ctx.HttpContext.Request, options))
            {
                ctx.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"TimeTracker Admin\"";
                return Results.StatusCode(401);
            }
            return await next(ctx);
        });

        admin.MapGet("/", (string? employeeId, string? from, string? to, ServerDb db) =>
        {
            var filter = ParseFilter(employeeId, from, to);
            var sessions = db.GetSessions(filter);
            var totals = db.GetTotals(filter);
            var dailyTotals = db.GetDailyTotals(filter);
            var employees = db.GetDistinctEmployees();
            var html = Html.BuildSessionsPage(sessions, totals, dailyTotals, employees, filter);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        admin.MapGet("/export.csv", (string? employeeId, string? from, string? to, ServerDb db) =>
        {
            var filter = ParseFilter(employeeId, from, to);
            var sessions = db.GetSessions(filter);
            // BOM обязателен — иначе Excel открывает CSV как ANSI (cp1251) и кириллица превращается в кракозябры.
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildCsv(sessions))).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", "timetracker-sessions.csv");
        });

        admin.MapGet("/sessions/{sessionId}/edit", (string sessionId, string? @return, ServerDb db) =>
        {
            var session = db.GetSessionById(sessionId);
            if (session is null) return Results.Content("Сессия не найдена.", "text/html; charset=utf-8", statusCode: 404);
            var html = Html.BuildEditSessionPage(session, error: null, returnQuery: @return);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        admin.MapPost("/sessions/{sessionId}/edit", async (string sessionId, HttpContext ctx, ServerDb db) =>
        {
            var session = db.GetSessionById(sessionId);
            if (session is null) return Results.Content("Сессия не найдена.", "text/html; charset=utf-8", statusCode: 404);

            var form = await ctx.Request.ReadFormAsync();
            var startedAtRaw = form["startedAt"].ToString();
            var endedAtRaw = form["endedAt"].ToString();
            var status = form["status"].ToString();
            var returnQuery = form["return"].ToString();

            var startedAt = ParseLocalDateTime(startedAtRaw);
            var endedAt = ParseLocalDateTime(endedAtRaw);

            if (startedAt.HasValue && endedAt.HasValue && endedAt.Value < startedAt.Value)
            {
                var errorHtml = Html.BuildEditSessionPage(
                    session,
                    error: "Время окончания не может быть раньше времени начала.",
                    returnQuery: returnQuery,
                    rawStartedAt: startedAtRaw,
                    rawEndedAt: endedAtRaw,
                    rawStatus: status);
                return Results.Content(errorHtml, "text/html; charset=utf-8", statusCode: 400);
            }

            await db.UpdateSessionAsync(sessionId, startedAt, endedAt, string.IsNullOrEmpty(status) ? null : status);
            return Results.Redirect(string.IsNullOrEmpty(returnQuery) ? "/admin" : $"/admin?{returnQuery}");
        });

        admin.MapGet("/sessions/{sessionId}/delete-confirm", (string sessionId, string? @return, ServerDb db) =>
        {
            var session = db.GetSessionById(sessionId);
            if (session is null) return Results.Content("Сессия не найдена.", "text/html; charset=utf-8", statusCode: 404);
            var html = Html.BuildDeleteConfirmPage(session, @return);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        admin.MapPost("/sessions/{sessionId}/delete", async (string sessionId, HttpContext ctx, ServerDb db) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var returnQuery = form["return"].ToString();
            await db.DeleteSessionAsync(sessionId);
            return Results.Redirect(string.IsNullOrEmpty(returnQuery) ? "/admin" : $"/admin?{returnQuery}");
        });
    }

    private static DateTime? ParseLocalDateTime(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;

    private static SessionFilter ParseFilter(string? employeeId, string? from, string? to)
    {
        DateOnly? fromDate = DateOnly.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : DefaultFrom();
        DateOnly? toDate = DateOnly.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DefaultTo();
        return new SessionFilter(string.IsNullOrEmpty(employeeId) ? null : employeeId, fromDate, toDate);
    }

    private static DateOnly DefaultFrom() => new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private static DateOnly DefaultTo() => DateOnly.FromDateTime(DateTime.Today);

    private static string BuildCsv(List<SessionRow> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[]
        {
            "Дата", "Сотрудник", "ID сотрудника", "Начало", "Конец", "Часов", "Статус", "MachineId", "SessionId", "Обновлено"
        }.Select(CsvField)));

        foreach (var s in sessions)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                (s.StartedAt ?? s.EndedAt)?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "",
                s.EmployeeName,
                s.EmployeeId,
                s.StartedAt?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                s.EndedAt?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                Html.FormatHours(s.Hours),
                s.Status ?? "в процессе",
                s.MachineId,
                s.SessionId,
                s.UpdatedAt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            }.Select(CsvField)));
        }

        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
