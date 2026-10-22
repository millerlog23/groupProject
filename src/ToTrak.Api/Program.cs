using ToTrak.Api.Data;
using ToTrak.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("AppSettings.json", optional: false, reloadOnChange: true);
var connectionString = builder.Configuration.GetConnectionString("AssignmentsDatabase")
    ?? throw new InvalidOperationException("AssignmentsDatabase is not configured.");

builder.Services.AddProblemDetails();
builder.Services.AddDbContext<TasksDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddCors();

var app = builder.Build();
app.UseExceptionHandler();

app.UseCors(builder => builder
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

app.MapAssignmentsEndpoints();
app.MapAuditEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
    await database.Database.EnsureCreatedAsync();
    await database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS Audit
        (
            ChangeId INTEGER NOT NULL CONSTRAINT PK_Audit PRIMARY KEY AUTOINCREMENT,
            ChangeTime TEXT NOT NULL,
            ClientChangeId TEXT NOT NULL,
            UserId INTEGER NOT NULL,
            EntityChanged TEXT NOT NULL,
            ChangeType TEXT NOT NULL,
            AffectedId INTEGER NOT NULL,
            OldValue TEXT NULL,
            NewValue TEXT NULL
        );
        """);
    await database.Database.ExecuteSqlRawAsync(
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_Audit_ClientChangeId ON Audit (ClientChangeId)");
}

await app.RunAsync();
