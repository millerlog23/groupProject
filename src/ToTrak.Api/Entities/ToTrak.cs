namespace ToTrak.Api.Entities;

public sealed class ToTrak
{
    public int AssignmentId { get; set; }
    public required string Title { get; set; }
    public DateTime DueDate { get; set; }
    public bool IsCompleted { get; set; }
}
