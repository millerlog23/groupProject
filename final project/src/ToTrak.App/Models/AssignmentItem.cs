namespace ToTrak.App.Models;

public class AssignmentItem
{
    public int UserId { get; set; }
    public long AssignmentId { get; set; }
    public string AssignmentName { get; set; } = string.Empty;
    public long CourseId { get; set; }
    public int AssignmentLocation { get; set; }
    public DateTime AssignmentTime { get; set; }
    public string AssignmentType { get; set; } = string.Empty;
    public string? AssignmentDescription { get; set; }
    public int? EstimatedTime { get; set; }
    public bool Completed { get; set; }
    public DateTime LastUpdated { get; set; }
    public bool IsDeleted { get; set; }
    public string LastModifiedBy { get; set; } = string.Empty;

    public AssignmentItem Copy() => new()
    {
        UserId = UserId,
        AssignmentId = AssignmentId,
        AssignmentName = AssignmentName,
        CourseId = CourseId,
        AssignmentLocation = AssignmentLocation,
        AssignmentTime = AssignmentTime,
        AssignmentType = AssignmentType,
        AssignmentDescription = AssignmentDescription,
        EstimatedTime = EstimatedTime,
        Completed = Completed,
        LastUpdated = LastUpdated,
        IsDeleted = IsDeleted,
        LastModifiedBy = LastModifiedBy
    };
}
