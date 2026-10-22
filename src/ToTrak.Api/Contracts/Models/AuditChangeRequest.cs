namespace ToTrak.Api.Contracts.Models;

public sealed record AuditChangeRequest(
    string ClientChangeId,
    int UserId,
    string EntityChanged,
    string ChangeType,
    int AffectedId,
    string? OldValue,
    string? NewValue);
