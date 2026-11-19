namespace ToTrak.App.Models;

public sealed record TaskDisplayItem(TaskItem Task, string LocationName)
{
    public string TaskName => Task.TaskName;
    public DateTime TaskTime => Task.TaskTime;
    public string TaskType => Task.TaskType;
    public bool Completed => Task.Completed;
}
