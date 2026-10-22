namespace ToTrak.Api.Contracts.Models;

public sealed record AssignmentDto(
    int AssignmentId,
    string Title,
    DateTime DueDate,
    bool IsCompleted);
