using TimeTracker.Server.Admin;
using TimeTracker.Server.Data;
using TimeTracker.Server.Events;
using TimeTracker.Server.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var options = builder.Configuration.GetSection("TimeTracker").Get<TimeTrackerOptions>() ?? new TimeTrackerOptions();
options.Validate(builder.Environment.IsDevelopment());

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new ServerDb(options.DbPath));
builder.WebHost.UseUrls(options.ListenUrl);

var app = builder.Build();

app.MapEventsEndpoints();
app.MapAdminEndpoints();

app.Run();
