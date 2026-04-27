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

    // Microsoft display name + UPN of the OTHER party on the call (i.e. not the
    // recorded user). Populated when call.Resource.Source.Identity.User is
    // available. Empty for calls where the SDK doesn't expose user identity
    // (some PSTN flows, opaque bot-as-source meetings, etc.).
    public string FromDisplay { get; init; } = string.Empty;
    public string FromUpn { get; init; } = string.Empty;

    public DateTime StartedAt { get; init; }

    // Set at finalization (mutable)
    public DateTime? EndedAt { get; set; }
    public int DurationSec { get; set; }
    public bool Partial { get; set; }
    public string? PartialReason { get; set; }
    public string? RecordingS3Key { get; set; }
}
