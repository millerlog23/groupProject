namespace ToTrak.Api.Entities;

public sealed class AuditRecord
{
    public int ChangeId { get; set; }

    public DateTime ChangeTime { get; set; }

    public string ClientChangeId { get; set; } = string.Empty;

    public int UserId { get; set; }

    public string EntityChanged { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public int AffectedId { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
}
