namespace ToTrak.App.Models;

public sealed class AssignmentDisplayItem
{
    public AssignmentItem Assignment { get; }
    public string CourseName { get; }
    public string AssignmentName => Assignment.AssignmentName;
    public DateTime AssignmentTime => Assignment.AssignmentTime;
    public bool Completed => Assignment.Completed;

    public AssignmentDisplayItem(AssignmentItem assignment, string courseName)
    {
        Assignment = assignment;
        CourseName = courseName;
    }
}
