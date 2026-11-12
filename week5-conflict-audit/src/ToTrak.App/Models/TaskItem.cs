namespace ToTrak.App.Models;

public class TaskItem
{
    public int UserId { get; set; }

    public long TaskId { get; set; }

    public string TaskName { get; set; } = string.Empty;

    public int TaskLocation { get; set; }

    public DateTime TaskTime { get; set; }

    public string TaskType { get; set; } = string.Empty;

    public bool Completed { get; set; }

    public DateTime LastUpdated { get; set; }

    public bool IsDeleted { get; set; }

    public string LastModifiedBy { get; set; } = string.Empty;

    public TaskItem Copy() => new()
{
    UserId = UserId,
    TaskId = TaskId,
    TaskName = TaskName,
    TaskLocation = TaskLocation,
    TaskTime = TaskTime,
    TaskType = TaskType,
    Completed = Completed,
    LastUpdated = LastUpdated,
    IsDeleted = IsDeleted,
    LastModifiedBy = LastModifiedBy
};
}

public sealed class PendingAuditChange
{
    public string ClientChangeId { get; set; } = string.Empty;

    public int UserId { get; set; }

    public string EntityChanged { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public long AffectedId { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public DateTime ChangedAtUtc { get; set; }

    public DateTime? BaseLastUpdatedUtc { get; set; }

    public string DeviceId { get; set; } = string.Empty;
}