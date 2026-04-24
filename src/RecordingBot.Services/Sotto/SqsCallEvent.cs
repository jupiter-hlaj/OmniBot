using System.Text.Json;
using System.Text.Json.Serialization;
using SottoTeamsBot.Calls;

namespace SottoTeamsBot.Models;

public sealed class SqsCallEvent
{
    public string Provider { get; init; } = "teams";
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
    public string ToIdentifier { get; init; } = string.Empty;
    public int DurationSec { get; init; }
    public string RecordingUrl { get; init; } = string.Empty;
    public string RecordingFormat { get; init; } = "wav";
    public string EndedAt { get; init; } = string.Empty;
    public Dictionary<string, string> RawPayload { get; init; } = new();

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static SqsCallEvent FromSession(CallSession session) => new()
    {
        Provider = "teams",
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
        ToIdentifier = session.ToIdentifier,
        DurationSec = session.DurationSec,
        RecordingUrl = string.Empty,
        RecordingFormat = "wav",
        EndedAt = session.EndedAt?.ToString("O") ?? string.Empty,
        RawPayload = new()
    };
}
