namespace ToTrak.App.Models;

public sealed class AuditEntry
{
    public string AuditId { get; set; } = string.Empty;
    public int UserId { get; set; }
    public long TaskId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public bool IsSynced { get; set; }
}