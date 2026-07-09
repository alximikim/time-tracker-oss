using System.Globalization;
using System.Net;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Admin;

/// <summary>
/// Админ-панель без Razor/JS — HTML собирается строками. Не мешает
/// self-contained single-file publish и соответствует стилю "сырой ADO.NET
/// без ORM", уже принятому у клиента — для внутреннего инструмента на
/// ~15 человек фреймворк не оправдан. Все значения из БД (имя сотрудника,
/// MachineId и т.п. приходят от клиента и не доверенные) экранируются
/// через Encode(). Редактирование/удаление — обычные HTML-формы с
/// перезагрузкой страницы, никакого JavaScript нигде на этих страницах.
/// </summary>
public static class Html
{
    public static string BuildSessionsPage(
        List<SessionRow> sessions,
        List<EmployeeTotal> totals,
        List<DailyTotal> dailyTotals,
        List<(string EmployeeId, string EmployeeName)> employees,
        SessionFilter filter)
    {
        var employeeOptions = string.Join("\n", employees.Select(e =>
        {
            var selected = e.EmployeeId == filter.EmployeeId ? " selected" : "";
            return $"<option value=\"{Encode(e.EmployeeId)}\"{selected}>{Encode(e.EmployeeName)}</option>";
        }));

        var fromValue = filter.From?.ToString("yyyy-MM-dd") ?? "";
        var toValue = filter.To?.ToString("yyyy-MM-dd") ?? "";

        var query = $"employeeId={Uri.EscapeDataString(filter.EmployeeId ?? "")}" +
                    $"&from={Uri.EscapeDataString(fromValue)}&to={Uri.EscapeDataString(toValue)}";
        var returnParam = Uri.EscapeDataString(query);

        var totalsRows = string.Join("\n", totals.Select(t =>
            $"<tr><td>{Encode(t.EmployeeName)}</td><td>{FormatHours(t.TotalHours)}</td></tr>"));

        var sessionRows = string.Join("\n", sessions.Select(s => $"""
            <tr>
              <td>{FormatDate(s.StartedAt ?? s.EndedAt)}</td>
              <td>{Encode(s.EmployeeName)}</td>
              <td>{Encode(s.EmployeeId)}</td>
              <td>{FormatTime(s.StartedAt)}</td>
              <td>{FormatTime(s.EndedAt)}</td>
              <td>{FormatHours(s.Hours)}</td>
              <td>{Encode(s.Status ?? "в процессе")}</td>
              <td>{Encode(s.MachineId)}</td>
              <td class="actions">
                <a href="/admin/sessions/{Uri.EscapeDataString(s.SessionId)}/edit?return={returnParam}">Изменить</a>
                <a href="/admin/sessions/{Uri.EscapeDataString(s.SessionId)}/delete-confirm?return={returnParam}" class="danger-link">Удалить</a>
              </td>
            </tr>
            """));

        var dailyBars = BuildDailyBars(dailyTotals);

        return $$"""
            <!DOCTYPE html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <title>Учёт времени — администратор</title>
              {{Styles()}}
            </head>
            <body>
              <h1>Учёт времени — сессии</h1>
              <form method="get">
                <label>Сотрудник:
                  <select name="employeeId">
                    <option value="">Все</option>
                    {{employeeOptions}}
                  </select>
                </label>
                <label>С: <input type="date" name="from" value="{{fromValue}}"></label>
                <label>По: <input type="date" name="to" value="{{toValue}}"></label>
                <button type="submit">Применить</button>
                <a href="/admin/export.csv?{{query}}">Экспорт CSV</a>
              </form>

              <h2>Часы по дням</h2>
              <div class="bars">
                {{dailyBars}}
              </div>

              <h2>Итоги за период</h2>
              <table>
                <tr><th>Сотрудник</th><th>Часов</th></tr>
                {{totalsRows}}
              </table>

              <h2>Сессии</h2>
              <table>
                <tr>
                  <th>Дата</th><th>Сотрудник</th><th>ID</th><th>Начало</th>
                  <th>Конец</th><th>Часов</th><th>Статус</th><th>MachineId</th><th>Действия</th>
                </tr>
                {{sessionRows}}
              </table>
            </body>
            </html>
            """;
    }

    public static string BuildEditSessionPage(
        SessionRow session,
        string? error,
        string? returnQuery,
        string? rawStartedAt = null,
        string? rawEndedAt = null,
        string? rawStatus = null)
    {
        var startedAtValue = rawStartedAt ?? ToDateTimeLocal(session.StartedAt);
        var endedAtValue = rawEndedAt ?? ToDateTimeLocal(session.EndedAt);
        var currentStatus = rawStatus ?? session.Status ?? "";

        var statusOptions = StopStatuses.Known
            .Append(currentStatus)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .Select(s =>
            {
                var selected = s == currentStatus ? " selected" : "";
                return $"<option value=\"{Encode(s)}\"{selected}>{Encode(s)}</option>";
            });

        var errorHtml = error is null ? "" : $"""<p class="error">{Encode(error)}</p>""";
        var returnEncoded = Encode(returnQuery ?? "");
        var cancelHref = string.IsNullOrEmpty(returnQuery) ? "/admin" : $"/admin?{returnQuery}";

        return $$"""
            <!DOCTYPE html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <title>Изменить сессию — администратор</title>
              {{Styles()}}
            </head>
            <body>
              <h1>Изменить сессию</h1>
              <p class="muted">
                {{Encode(session.EmployeeName)}} ({{Encode(session.EmployeeId)}}) — {{Encode(session.MachineId)}}
              </p>
              {{errorHtml}}
              <form method="post" action="/admin/sessions/{{Uri.EscapeDataString(session.SessionId)}}/edit">
                <p>
                  <label>Начало:
                    <input type="datetime-local" step="1" name="startedAt" value="{{startedAtValue}}">
                  </label>
                </p>
                <p>
                  <label>Конец:
                    <input type="datetime-local" step="1" name="endedAt" value="{{endedAtValue}}">
                  </label>
                  <span class="muted">(пусто — сессия ещё идёт)</span>
                </p>
                <p>
                  <label>Статус:
                    <select name="status">
                      <option value="">— нет —</option>
                      {{string.Join("\n", statusOptions)}}
                    </select>
                  </label>
                </p>
                <input type="hidden" name="return" value="{{returnEncoded}}">
                <button type="submit">Сохранить</button>
                <a href="{{cancelHref}}">Отмена</a>
              </form>
            </body>
            </html>
            """;
    }

    public static string BuildDeleteConfirmPage(SessionRow session, string? returnQuery)
    {
        var cancelHref = string.IsNullOrEmpty(returnQuery) ? "/admin" : $"/admin?{returnQuery}";
        var returnEncoded = Encode(returnQuery ?? "");

        return $$"""
            <!DOCTYPE html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <title>Удалить сессию — администратор</title>
              {{Styles()}}
            </head>
            <body>
              <h1>Удалить сессию?</h1>
              <table>
                <tr><th>Сотрудник</th><td>{{Encode(session.EmployeeName)}} ({{Encode(session.EmployeeId)}})</td></tr>
                <tr><th>Дата</th><td>{{FormatDate(session.StartedAt ?? session.EndedAt)}}</td></tr>
                <tr><th>Начало</th><td>{{FormatTime(session.StartedAt)}}</td></tr>
                <tr><th>Конец</th><td>{{FormatTime(session.EndedAt)}}</td></tr>
                <tr><th>Часов</th><td>{{FormatHours(session.Hours)}}</td></tr>
                <tr><th>Статус</th><td>{{Encode(session.Status ?? "в процессе")}}</td></tr>
              </table>
              <p class="error">Это действие необратимо.</p>
              <form method="post" action="/admin/sessions/{{Uri.EscapeDataString(session.SessionId)}}/delete">
                <input type="hidden" name="return" value="{{returnEncoded}}">
                <button type="submit" class="danger">Удалить</button>
                <a href="{{cancelHref}}">Отмена</a>
              </form>
            </body>
            </html>
            """;
    }

    private static string BuildDailyBars(List<DailyTotal> dailyTotals)
    {
        if (dailyTotals.Count == 0) return "<p class=\"muted\">Нет данных за период.</p>";

        var maxHours = dailyTotals.Max(d => d.TotalHours);
        return string.Join("\n", dailyTotals.Select(d =>
        {
            var pct = maxHours > 0 ? d.TotalHours / maxHours * 100 : 0;
            var pctStr = pct.ToString("0.#", CultureInfo.InvariantCulture);
            return $"""
                <div class="bar-row">
                  <span class="bar-label">{d.Day:dd.MM}</span>
                  <div class="bar-track"><div class="bar-fill" style="width:{pctStr}%"></div></div>
                  <span class="bar-value">{FormatHours(d.TotalHours)}</span>
                </div>
                """;
        }));
    }

    /// <summary>Общие стили для всех страниц админки. CSS-переменные +
    /// prefers-color-scheme — тёмная тема включается автоматически по
    /// настройкам ОС/браузера, без единой строчки JS.</summary>
    private static string Styles() => """
        <style>
          :root {
            --bg: #ffffff; --fg: #1f2328; --muted: #6b7280; --border: #d8dce2;
            --th-bg: #f2f4f7; --hover: #f7f8fa; --link: #0b5fff; --danger: #c0392b;
            --card-bg: #ffffff;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: #14161a; --fg: #e6e6e6; --muted: #9aa0a6; --border: #33373d;
              --th-bg: #1d2025; --hover: #1d2025; --link: #6ea8ff; --danger: #ff6b5e;
              --card-bg: #1a1d22;
            }
          }
          * { box-sizing: border-box; }
          body {
            font-family: -apple-system, "Segoe UI", Roboto, sans-serif;
            margin: 0 auto; max-width: 1100px; padding: 2rem 1.5rem;
            color: var(--fg); background: var(--bg); line-height: 1.4;
          }
          h1 { font-size: 1.5rem; margin-bottom: 0.25rem; }
          h2 { font-size: 1.1rem; margin-top: 2rem; color: var(--fg); }
          .muted { color: var(--muted); font-size: 0.9rem; }
          table { border-collapse: collapse; width: 100%; margin-top: 0.75rem; background: var(--card-bg); }
          th, td { border: 1px solid var(--border); padding: 0.5rem 0.7rem; text-align: left; }
          th { background: var(--th-bg); }
          tr:hover td { background: var(--hover); }
          form { margin-bottom: 1rem; }
          label { margin-right: 1rem; }
          select, input, button {
            background: var(--card-bg); color: var(--fg); border: 1px solid var(--border);
            border-radius: 4px; padding: 0.35rem 0.5rem; font-size: 0.95rem;
          }
          button { cursor: pointer; background: var(--link); color: #fff; border: none; padding: 0.45rem 0.9rem; }
          button.danger { background: var(--danger); }
          a { color: var(--link); text-decoration: none; }
          a:hover { text-decoration: underline; }
          .actions a { margin-right: 0.6rem; font-size: 0.9rem; }
          .danger-link { color: var(--danger); }
          .error { color: var(--danger); font-weight: 600; }
          .bars { background: var(--card-bg); border: 1px solid var(--border); border-radius: 6px; padding: 0.75rem 1rem; margin-top: 0.75rem; }
          .bar-row { display: flex; align-items: center; gap: 0.6rem; margin: 0.3rem 0; }
          .bar-label { width: 3.5rem; font-size: 0.85rem; color: var(--muted); }
          .bar-track { flex: 1; background: var(--border); height: 0.9rem; border-radius: 0.25rem; overflow: hidden; }
          .bar-fill { background: var(--link); height: 100%; }
          .bar-value { width: 3.5rem; text-align: right; font-size: 0.85rem; }
        </style>
        """;

    public static string Encode(string value) => WebUtility.HtmlEncode(value);

    /// <summary>InvariantCulture — иначе разделитель дробной части (","/".")
    /// зависит от локали ОС сервера и ломает CSV-экспорт (см. AdminEndpoints.CsvField)
    /// и ширину CSS-баров в дашборде (некорректный "width:1,5%" рендерится
    /// как 0).</summary>
    public static string FormatHours(double? hours) =>
        hours?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";

    private static string FormatDate(DateTime? value) => value?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "";
    private static string FormatTime(DateTime? value) => value?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

    /// <summary>Формат значения для &lt;input type="datetime-local" step="1"&gt;
    /// — с секундами, чтобы пересохранение обоих полей разом не теряло до
    /// 59с точности в пересчитанных Hours.</summary>
    private static string ToDateTimeLocal(DateTime? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) ?? "";
}
