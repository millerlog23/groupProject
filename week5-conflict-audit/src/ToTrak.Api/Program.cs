using Microsoft.Data.Sqlite;
using System.Text.Json;
using ToTrak.Api.Data;
using ToTrak.Api.Models;

var builder = WebApplication.CreateBuilder(args);

string databasePath = Path.Combine(
    builder.Environment.ContentRootPath,
    "totrak-server-week5.db");

var database = new ServerDatabase(databasePath);
database.Initialize();

builder.Services.AddSingleton(database);

var app = builder.Build();

app.MapGet("/", () => "ToTrak API is running.");

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy"
}));

// Both URLs use the same push handler.
app.MapPost("/sync/changes", HandlePush);
app.MapPost("/api/audit", HandlePush);

app.MapGet("/changes",
    IResult (
        int userId,
        long? after,
        DateTimeOffset? since,
        ServerDatabase database) =>
    {
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
IResult (int userId, ServerDatabase database) =>
{
    if (userId <= 0)
    {
        return Results.BadRequest(new
        {
            error = "userId must be positive."
        });
    }

    return Results.Ok(database.GetAuditHistory(userId));
});

app.Run();

static IResult HandlePush(
    PendingAuditChange change,
    ServerDatabase database)
{
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
            error = "NewValue must contain valid task JSON."
        });
    }
    catch (InvalidOperationException ex)
    {
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