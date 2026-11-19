namespace ToTrak.Api.Models;

public class LocationItem
{
    public int UserId { get; set; }
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public bool IsDeleted { get; set; }
    public string LastModifiedBy { get; set; } = string.Empty;
}
