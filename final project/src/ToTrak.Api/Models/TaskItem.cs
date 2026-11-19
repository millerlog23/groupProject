namespace ToTrak.Api.Models;

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
}