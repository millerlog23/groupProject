using Microsoft.EntityFrameworkCore;
using ToTrak.Api.Contracts.Models;
using ToTrak.Api.Data;
using ToTrak.Api.Entities;

namespace ToTrak.Api.Endpoints;

public static class AuditEndpoints
{
    public static WebApplication MapAuditEndpoints(this WebApplication app)
    {
        app.MapPost("/api/audit", AppendAsync);
        return app;
    }

    private static async Task<IResult> AppendAsync(
        AuditChangeRequest request,
        TasksDbContext database,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request))
            return Results.BadRequest("The audit change is incomplete or invalid.");

        var existing = await database.Audit.SingleOrDefaultAsync(
            item => item.ClientChangeId == request.ClientChangeId,
            cancellationToken);
        if (existing is not null)
            return Results.Ok(existing);

        var record = new AuditRecord
        {
            ChangeTime = DateTime.UtcNow,
            ClientChangeId = request.ClientChangeId,
            UserId = request.UserId,
            EntityChanged = request.EntityChanged,
            ChangeType = request.ChangeType,
            AffectedId = request.AffectedId,
            OldValue = request.OldValue,
            NewValue = request.NewValue
        };

        database.Audit.Add(record);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/audit/{record.ChangeId}", record);
    }

    private static bool IsValid(AuditChangeRequest request)
    {
        if (!Guid.TryParse(request.ClientChangeId, out _) ||
            request.UserId <= 0 ||
            request.AffectedId <= 0 ||
            string.IsNullOrWhiteSpace(request.EntityChanged))
            return false;

        return request.ChangeType switch
        {
            "Create" => request.OldValue is null && request.NewValue is not null,
            "Update" => request.OldValue is not null && request.NewValue is not null,
            "Delete" => request.OldValue is not null && request.NewValue is null,
            _ => false
        };
    }
}
