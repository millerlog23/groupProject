namespace ToTrak.Api.Contracts.Models;

public sealed record UpsertAssignmentRequest(
    string Title,
    DateTime DueDate,
    bool IsCompleted);
