using ToTrak.Api.Data;
using ToTrak.Api.Entities;
using ToTrak.Api.Contracts.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using AssignmentEntity = ToTrak.Api.Entities.ToTrak;

namespace ToTrak.Api.Endpoints;

public static class AssignmentsEndpoints
{
    public static WebApplication MapAssignmentsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/assignments");
        group.MapGet("", GetAllAsync);
        group.MapGet("/{id:int}", GetByIdAsync).WithName(nameof(GetByIdAsync));
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        return app;
    }

    private static async Task<Ok<List<AssignmentDto>>> GetAllAsync(
        TasksDbContext database,
        CancellationToken cancellationToken)
    {
        var items = await database.Assignments.AsNoTracking()
            .OrderBy(item => item.DueDate)
            .Select(item => new AssignmentDto(
                item.AssignmentId, item.Title, item.DueDate, item.IsCompleted))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok<AssignmentDto>, NotFound>> GetByIdAsync(
        int id,
        TasksDbContext database,
        CancellationToken cancellationToken)
    {
        var item = await database.Assignments.AsNoTracking()
            .Where(item => item.AssignmentId == id)
            .Select(item => new AssignmentDto(
                item.AssignmentId, item.Title, item.DueDate, item.IsCompleted))
            .SingleOrDefaultAsync(cancellationToken);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item);
    }

    private static async Task<Results<Created<AssignmentDto>, ValidationProblem>> CreateAsync(
        UpsertAssignmentRequest request,
        TasksDbContext database,
        CancellationToken cancellationToken)
    {
        var validationProblem = ValidateDueDate(request);
        if (validationProblem is not null)
            return validationProblem;

        var entity = new AssignmentEntity
        {
            Title = request.Title.Trim(),
            DueDate = request.DueDate.Date,
            IsCompleted = request.IsCompleted
        };
        database.Assignments.Add(entity);
        await database.SaveChangesAsync(cancellationToken);

        var result = ToDto(entity);
        return TypedResults.Created($"/api/assignments/{entity.AssignmentId}", result);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> UpdateAsync(
        int id,
        UpsertAssignmentRequest request,
        TasksDbContext database,
        CancellationToken cancellationToken)
    {
        var validationProblem = ValidateDueDate(request);
        if (validationProblem is not null)
            return validationProblem;

        var entity = await database.Assignments.FindAsync([id], cancellationToken);
        if (entity is null)
            return TypedResults.NotFound();

        entity.Title = request.Title.Trim();
        entity.DueDate = request.DueDate.Date;
        entity.IsCompleted = request.IsCompleted;
        await database.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        int id,
        TasksDbContext database,
        CancellationToken cancellationToken)
    {
        var entity = await database.Assignments.FindAsync([id], cancellationToken);
        if (entity is null)
            return TypedResults.NotFound();

        database.Assignments.Remove(entity);
        await database.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static ValidationProblem? ValidateDueDate(UpsertAssignmentRequest request) =>
        request.DueDate == default
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.DueDate)] = ["A due date is required."]
            })
            : null;

    private static AssignmentDto ToDto(AssignmentEntity item) =>
        new(item.AssignmentId, item.Title, item.DueDate, item.IsCompleted);
}
