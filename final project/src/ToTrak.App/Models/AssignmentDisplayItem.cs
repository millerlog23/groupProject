namespace ToTrak.App.Models;

public sealed class AssignmentDisplayItem
{
    public AssignmentItem Assignment { get; }
    public string CourseName { get; }
    public string AssignmentName => Assignment.AssignmentName;
    public DateTime AssignmentTime => Assignment.AssignmentTime;
    public bool Completed => Assignment.Completed;
    public string AssignmentType => Assignment.AssignmentType;
    public string? AssignmentDescription => Assignment.AssignmentDescription;
    public string EstimatedTime => Assignment.EstimatedTime is { } minutes ? $"Estimated: {minutes} minutes" : "No time estimate";

    public AssignmentDisplayItem(AssignmentItem assignment, string courseName)
    {
        Assignment = assignment;
        CourseName = courseName;
    }
}
