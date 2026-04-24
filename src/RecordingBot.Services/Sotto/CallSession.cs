namespace SottoTeamsBot.Calls;

public sealed record class CallSession
{
    // Set at answer time (immutable)
    public string CallId { get; init; } = string.Empty;
    public string MsCallId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string? AgentId { get; init; }
    public string Direction { get; init; } = string.Empty;
    public string FromNumber { get; init; } = string.Empty;
    public string ToIdentifier { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }

    // Set at finalization (mutable)
    public DateTime? EndedAt { get; set; }
    public int DurationSec { get; set; }
    public bool Partial { get; set; }
    public string? PartialReason { get; set; }
    public string? RecordingS3Key { get; set; }
}
