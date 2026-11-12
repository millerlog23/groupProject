using Microsoft.Data.Sqlite;
using System.Text.Json;
using ToTrak.Api.Data;
using ToTrak.Api.Models;

var builder = WebApplication.CreateBuilder(args);

string databasePath = Path.Combine(
    builder.Environment.ContentRootPath,
    "totrak-server.db");

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

app.Run();

static IResult HandlePush(
    PendingAuditChange change,
    ServerDatabase database)
{
    try
    {
        bool applied = database.ApplyChange(change);

        return Results.Ok(new
        {
            success = true,
            clientChangeId = change.ClientChangeId,
            alreadyProcessed = !applied
        });
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
            error = "The change conflicts with an existing database record."
        });
    }
}