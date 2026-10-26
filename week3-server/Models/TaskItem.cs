namespace ToTrak.Api.Models;

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
}