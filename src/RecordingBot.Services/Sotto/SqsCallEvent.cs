using System.Text.Json;
using System.Text.Json.Serialization;
using SottoTeamsBot.Calls;

namespace SottoTeamsBot.Models;

public sealed class SqsCallEvent
{
    public string Provider { get; init; } = "teams";
    public string EventType { get; init; } = "call_ended";
    public string TenantId { get; init; } = string.Empty;
    public string? AgentId { get; init; }
    public string CallId { get; init; } = string.Empty;
    public string MsCallId { get; init; } = string.Empty;
    public bool RecordingAlreadyUploaded { get; init; } = true;
    public string RecordingS3Key { get; init; } = string.Empty;
    public int AgentChannel { get; init; } = 0;
    public bool Partial { get; init; }
    public string? PartialReason { get; init; }
    public string ProviderCallId { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string FromNumber { get; init; } = string.Empty;
    public string FromDisplay { get; init; } = string.Empty;
    public string FromUpn { get; init; } = string.Empty;
    public string ToIdentifier { get; init; } = string.Empty;
    public int DurationSec { get; init; }
    public string RecordingUrl { get; init; } = string.Empty;
    public string RecordingFormat { get; init; } = "wav";

    // StartedAt / EndedAt must be nullable: the Python consumer's
    // NormalizedCallEvent typed these as Optional[datetime], which accepts
    // a valid ISO-8601 string or JSON null. An empty string ("") is rejected
    // by Pydantic with "Input should be a valid datetime or date" and the
    // SQS message dead-letters after 3 retries. Use null (serializes as
    // JSON null) when the field doesn't apply to the event type.
    public string? StartedAt { get; init; }
    public string? EndedAt { get; init; }

    public Dictionary<string, string> RawPayload { get; init; } = new();

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    // call_ended — fired in SottoFinalizeAsync after the recording is uploaded.
    // Backwards-compatible with the pre-C-5b shape: EventType defaults to
    // "call_ended" so any existing consumer that ignores EventType still works.
    public static SqsCallEvent FromSession(CallSession session) => new()
    {
        Provider = "teams",
        EventType = "call_ended",
        TenantId = session.TenantId,
        AgentId = session.AgentId,
        CallId = session.CallId,
        MsCallId = session.MsCallId,
        RecordingAlreadyUploaded = true,
        RecordingS3Key = session.RecordingS3Key ?? string.Empty,
        AgentChannel = 0,
        Partial = session.Partial,
        PartialReason = session.PartialReason,
        ProviderCallId = session.MsCallId,
        Direction = session.Direction,
        FromNumber = session.FromNumber,
        FromDisplay = session.FromDisplay,
        FromUpn = session.FromUpn,
        ToIdentifier = session.ToIdentifier,
        DurationSec = session.DurationSec,
        RecordingUrl = string.Empty,
        RecordingFormat = "wav",
        StartedAt = session.StartedAt.ToString("O"),
        EndedAt = session.EndedAt?.ToString("O"),
        RawPayload = new()
    };

    // call_started — fired at end of SottoInitializeSessionAsync after the
    // participant sweep. Says "this call exists, it's ringing." Caller ID may
    // or may not be populated depending on whether the phone-identity
    // participant arrived during the init window. Cockpit uses this to render
    // the incoming-call UI.
    public static SqsCallEvent FromSessionStarted(CallSession session) => new()
    {
        Provider = "teams",
        EventType = "call_started",
        TenantId = session.TenantId,
        AgentId = session.AgentId,
        CallId = session.CallId,
        MsCallId = session.MsCallId,
        RecordingAlreadyUploaded = false,
        RecordingS3Key = string.Empty,
        AgentChannel = 0,
        Partial = false,
        PartialReason = null,
        ProviderCallId = session.MsCallId,
        Direction = session.Direction,
        FromNumber = session.FromNumber,
        FromDisplay = session.FromDisplay,
        FromUpn = session.FromUpn,
        ToIdentifier = session.ToIdentifier,
        DurationSec = 0,
        RecordingUrl = string.Empty,
        RecordingFormat = string.Empty,
        StartedAt = session.StartedAt.ToString("O"),
        EndedAt = null,
        RawPayload = new()
    };

    // call_declined — fired in CallHandler.CallOnUpdated when the call goes
    // Terminated without ever reaching Established (the agent declined the
    // ringing call, or it was missed). The Engine A call_started already
    // showed a Cockpit ringing card; this lets the backend push a
    // call_declined WS event to the agent so the card clears immediately
    // instead of sitting through the 10-min absolute stale window.
    // No recording, no audio — the bot never established its media session.
    public static SqsCallEvent FromSessionDeclined(CallSession session) => new()
    {
        Provider = "teams",
        EventType = "call_declined",
        TenantId = session.TenantId,
        AgentId = session.AgentId,
        CallId = session.CallId,
        MsCallId = session.MsCallId,
        RecordingAlreadyUploaded = false,
        RecordingS3Key = string.Empty,
        AgentChannel = 0,
        Partial = false,
        PartialReason = null,
        ProviderCallId = session.MsCallId,
        Direction = session.Direction,
        FromNumber = session.FromNumber,
        FromDisplay = session.FromDisplay,
        FromUpn = session.FromUpn,
        ToIdentifier = session.ToIdentifier,
        DurationSec = 0,
        RecordingUrl = string.Empty,
        RecordingFormat = string.Empty,
        StartedAt = session.StartedAt.ToString("O"),
        EndedAt = DateTime.UtcNow.ToString("O"),
        RawPayload = new()
    };

    // call_caller_identified — fired inside SottoTryUpdateFromPhoneIdentity
    // immediately after _session is updated with the extracted phone identity.
    // Says "we identified the caller as X." Carries identification info only;
    // Cockpit uses provider_call_id to find the existing session and update
    // its caller display.
    public static SqsCallEvent FromSessionCallerIdentified(CallSession session) => new()
    {
        Provider = "teams",
        EventType = "call_caller_identified",
        TenantId = session.TenantId,
        AgentId = session.AgentId,
        CallId = session.CallId,
        MsCallId = session.MsCallId,
        RecordingAlreadyUploaded = false,
        RecordingS3Key = string.Empty,
        AgentChannel = 0,
        Partial = false,
        PartialReason = null,
        ProviderCallId = session.MsCallId,
        Direction = session.Direction,
        FromNumber = session.FromNumber,
        FromDisplay = session.FromDisplay,
        FromUpn = session.FromUpn,
        ToIdentifier = session.ToIdentifier,
        DurationSec = 0,
        RecordingUrl = string.Empty,
        RecordingFormat = string.Empty,
        StartedAt = null,
        EndedAt = null,
        RawPayload = new()
    };
}
