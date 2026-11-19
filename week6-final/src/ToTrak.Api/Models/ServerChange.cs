namespace ToTrak.Api.Models;

public sealed class ServerChange
{
    public long ServerChangeId { get; set; }

    public string ClientChangeId { get; set; } = string.Empty;

    public int UserId { get; set; }

    public string EntityChanged { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public long AffectedId { get; set; }

    public long? TaskId { get; set; }

    public long? AssignmentId { get; set; }

    public long? CourseId { get; set; }

    public int? LocationId { get; set; }

    public string? NewValue { get; set; }

    public string ReceivedAt { get; set; } = string.Empty;
}