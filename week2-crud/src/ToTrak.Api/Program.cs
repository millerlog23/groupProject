var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "ToTrak mock API is running.");

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy"
}));

app.MapGet("/api/tasks", () => Results.Ok(new[]
{
    new
    {
        UserId = 1,
        TaskId = 1,
        TaskName = "Finish mobile development homework",
        TaskLocation = 0,
        TaskTime = new DateTime(2026, 10, 25, 17, 0, 0),
        TaskType = "School",
        Completed = false,
        LastUpdated = new DateTime(
            2026, 10, 21, 18, 0, 0, DateTimeKind.Utc)
    }
}));

app.Run();