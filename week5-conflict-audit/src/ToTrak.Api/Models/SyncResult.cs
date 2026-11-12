namespace ToTrak.Api.Models;

public sealed class SyncResult
{
    public bool Success { get; set; } = true;
    public string ClientChangeId { get; set; } = string.Empty;
    public bool AlreadyProcessed { get; set; }
    public bool WasConflict { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public TaskItem? ServerTask { get; set; }
}