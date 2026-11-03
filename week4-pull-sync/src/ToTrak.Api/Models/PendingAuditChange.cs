namespace ToTrak.Api.Models;

public class PendingAuditChange
{
    public string ClientChangeId { get; set; } = string.Empty;

    public int UserId { get; set; }

    public string EntityChanged { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public int AffectedId { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
}