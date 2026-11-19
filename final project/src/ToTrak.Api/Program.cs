using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using ToTrak.Api.Data;
using ToTrak.Api.Models;

var builder = WebApplication.CreateBuilder(args);

string databasePath = builder.Configuration["DatabasePath"] ?? Path.Combine(
    builder.Environment.ContentRootPath,
    "totrak-server-week5.db");

var database = new ServerDatabase(databasePath);
database.Initialize();

builder.Services.AddSingleton(database);

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" || context.Request.Path == "/health" ||
        context.Request.Path == "/auth/register" || context.Request.Path == "/auth/login")
    {
        await next();
        return;
    }
    string authorization = context.Request.Headers.Authorization.ToString();
    int? userId = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
        ? database.Authenticate(authorization[7..]) : null;
    if (userId is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    context.Items["UserId"] = userId.Value;
    await next();
});

app.MapPost("/auth/register", (Credentials credentials, ServerDatabase database) =>
{
    try { return Results.Ok(database.Register(credentials)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
    { return Results.Conflict(new { error = "That username is already registered." }); }
});
app.MapPost("/auth/login", IResult (Credentials credentials, ServerDatabase database) =>
    database.Login(credentials) is { } session ? Results.Ok(session) : Results.Unauthorized());
app.MapPost("/auth/logout", (HttpContext context, ServerDatabase database) =>
{
    database.Logout(context.Request.Headers.Authorization.ToString()[7..]);
    return Results.NoContent();
});

app.MapGet("/", () => "ToTrak API is running.");

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy"
}));

if (app.Environment.IsDevelopment())
{
    app.MapGet("/debug/db", (ServerDatabase database, HttpContext context) =>
        Results.Content(
            BuildDebugDbHtml(database.GetDebugSnapshot((int)context.Items["UserId"]!)),
            "text/html; charset=utf-8"));
}

// All three endpoints use the same push handler.
app.MapPost("/pushChanges", HandlePush);
app.MapPost("/sync/changes", HandlePush);
app.MapPost("/api/audit", HandlePush);

app.MapGet("/changes",
    IResult (
        int userId,
        long? after,
        DateTimeOffset? since,
        HttpContext context,
        ServerDatabase database) =>
    {
        if (userId != (int)context.Items["UserId"]!) return Results.StatusCode(403);
        if (userId <= 0 || after < 0)
        {
            return Results.BadRequest(new
            {
                error = "userId must be positive; after cannot be negative."
            });
        }

        var changes = database.GetChanges(
            userId,
            after ?? 0,
            since);

        return Results.Ok(changes);
    });
app.MapGet("/audit",
IResult (int userId, ServerDatabase database, HttpContext context) =>
{
    if (userId != (int)context.Items["UserId"]!) return Results.StatusCode(403);
    if (userId <= 0)
    {
        return Results.BadRequest(new
        {
            error = "userId must be positive."
        });
    }

    return Results.Ok(database.GetAuditHistory(userId));
});

try
{
    await app.RunAsync();
}
catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    Console.Error.WriteLine("ToTrak API could not start: its address is already in use. Stop the other API instance or choose a different --urls address.");
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    // Report hosting failures without raising a native Windows crash dialog.
    // Console output also works when Windows Event Log access is unavailable.
    Console.Error.WriteLine($"ToTrak API could not run: {ex.GetBaseException().Message}");
    Environment.ExitCode = 1;
}

static string BuildDebugDbHtml(
    Dictionary<string, List<Dictionary<string, object?>>> snapshot)
{
    var html = new StringBuilder();
    html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
    html.Append("<meta http-equiv=\"refresh\" content=\"3\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
    html.Append("<title>ToTrak server database</title><style>");
    html.Append("body{font:15px system-ui,sans-serif;margin:2rem;background:#111827;color:#f3f4f6}");
    html.Append("h1{margin-bottom:.25rem}p{color:#cbd5e1}.table-wrap{overflow:auto;margin:1.5rem 0}");
    html.Append("table{border-collapse:collapse;min-width:100%;background:#1f2937}th,td{border:1px solid #475569;padding:.55rem .75rem;text-align:left;vertical-align:top;white-space:pre-wrap;max-width:35rem;overflow-wrap:anywhere}");
    html.Append("th{background:#0f172a}tr:nth-child(even){background:#243244}</style></head><body>");
    html.Append("<h1>ToTrak server database</h1><p>Refreshes every 3 seconds. Development-only view.</p>");

    if (snapshot.Count == 0)
    {
        html.Append("<p>No user tables found.</p>");
    }

    foreach (var table in snapshot)
    {
        html.Append("<section class=\"table-wrap\"><h2>");
        html.Append(System.Net.WebUtility.HtmlEncode(table.Key));
        html.Append("</h2>");

        if (table.Value.Count == 0)
        {
            html.Append("<p>No rows in this table.</p></section>");
            continue;
        }

        var columns = table.Value[0].Keys.ToList();
        html.Append("<table><thead><tr>");
        foreach (string column in columns)
        {
            html.Append("<th>");
            html.Append(System.Net.WebUtility.HtmlEncode(column));
            html.Append("</th>");
        }
        html.Append("</tr></thead><tbody>");

        foreach (var row in table.Value)
        {
            html.Append("<tr>");
            foreach (string column in columns)
            {
                html.Append("<td>");
                if (!row.TryGetValue(column, out var value) || value is null)
                {
                    html.Append("<em>null</em>");
                }
                else
                {
                    string text = value is byte[] bytes
                        ? Convert.ToBase64String(bytes)
                        : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    html.Append(System.Net.WebUtility.HtmlEncode(text));
                }
                html.Append("</td>");
            }
            html.Append("</tr>");
        }

        html.Append("</tbody></table></section>");
    }

    html.Append("</body></html>");
    return html.ToString();
}

static IResult HandlePush(
    PendingAuditChange change,
    HttpContext context,
    ServerDatabase database)
{
    if (change.UserId != (int)context.Items["UserId"]!) return Results.StatusCode(403);
    try
    {
        var result = database.ApplyChange(change);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new
        {
            error = "NewValue must contain valid item JSON."
        });
    }
    catch (InvalidOperationException ex)
    {
        if (change.ChangeType == "Create" && ex.Message.Contains("ID collision", StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { error = ex.Message, nextId = database.GetNextEntityId(change.UserId, change.EntityChanged) });
        return Results.Conflict(new { error = ex.Message });
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        return Results.Conflict(new
        {
            error = ex.Message
        });
    }
}
