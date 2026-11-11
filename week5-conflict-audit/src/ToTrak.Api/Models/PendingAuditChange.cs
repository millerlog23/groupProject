namespace ToTrak.Api.Models;

public sealed class PendingAuditChange
{
    public string ClientChangeId { get; set; } = string.Empty;

    public int UserId { get; set; }

    public string EntityChanged { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public long AffectedId { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public DateTime ChangedAtUtc { get; set; }

    public DateTime? BaseLastUpdatedUtc { get; set; }

    public string DeviceId { get; set; } = string.Empty;
}