using ToTrak.Api.Data;
using ToTrak.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("AssignmentsDatabase")
    ?? throw new InvalidOperationException("AssignmentsDatabase is not configured.");

builder.Services.AddValidation();
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

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<AssignmentsDbContext>();
    await database.Database.MigrateAsync();
}

await app.RunAsync();
