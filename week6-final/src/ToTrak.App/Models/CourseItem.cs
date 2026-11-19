namespace ToTrak.App.Models;

public class CourseItem
{
    public int UserId { get; set; }
    public long CourseId { get; set; }
    public string CourseName { get; set; } = string.Empty;
    public string? CourseDescription { get; set; }
    public DateTime LastUpdated { get; set; }
    public bool IsDeleted { get; set; }
    public string LastModifiedBy { get; set; } = string.Empty;
    public CourseItem Copy() => (CourseItem)MemberwiseClone();
}
