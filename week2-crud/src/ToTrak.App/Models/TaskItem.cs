namespace ToTrak.App.Models;

public class TaskItem
{
    public int UserId { get; set; }

    public int TaskId { get; set; }

    public string TaskName { get; set; } = string.Empty;

    public int TaskLocation { get; set; }

    public DateTime TaskTime { get; set; }

    public string TaskType { get; set; } = string.Empty;

    public bool Completed { get; set; }

    public DateTime LastUpdated { get; set; }

    public TaskItem Copy() => new()
    {
        UserId = UserId,
        TaskId = TaskId,
        TaskName = TaskName,
        TaskLocation = TaskLocation,
        TaskTime = TaskTime,
        TaskType = TaskType,
        Completed = Completed,
        LastUpdated = LastUpdated
    };
}

public sealed class PendingAuditChange
{
    public string ClientChangeId { get; set; } = string.Empty;
    public int UserId { get; set; }
    public string EntityChanged { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public int AffectedId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}