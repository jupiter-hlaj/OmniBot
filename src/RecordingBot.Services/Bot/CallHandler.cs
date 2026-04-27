using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using RecordingBot.Model.Constants;
using RecordingBot.Services.Contract;
using RecordingBot.Services.ServiceSetup;
using RecordingBot.Services.Util;
using SottoTeamsBot.Audio;
using SottoTeamsBot.Aws;
using SottoTeamsBot.Calls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace RecordingBot.Services.Bot
{
    public class CallHandler : HeartbeatHandler
    {
        private int _recordingStatusIndex = -1;
        private readonly AzureSettings _settings;
        private readonly IEventPublisher _eventPublisher;
        private readonly CaptureEvents _capture;
        private bool _isDisposed = false;

        // Sotto integration: dependencies + session state for S3 upload and SQS enqueue.
        private readonly DynamoResolver _dynamo;
        private readonly AwsUploader _uploader;
        private readonly AudioEncoder _encoder;
        private CallSession _session;
        private bool _sottoFinalized;
        private readonly SemaphoreSlim _sottoFinalizeLock = new(1, 1);

        public ICall Call { get; }
        public BotMediaStream BotMediaStream { get; private set; }

        public CallHandler(ICall statefulCall, IAzureSettings settings, IEventPublisher eventPublisher, DynamoResolver dynamo, AwsUploader uploader, AudioEncoder encoder) : base(TimeSpan.FromMinutes(10), statefulCall?.GraphLogger)
        {
            _settings = (AzureSettings)settings;
            _eventPublisher = eventPublisher;
            _dynamo = dynamo;
            _uploader = uploader;
            _encoder = encoder;

            Call = statefulCall;
            Call.OnUpdated += CallOnUpdated;
            Call.Participants.OnUpdated += ParticipantsOnUpdated;

            BotMediaStream = new BotMediaStream(Call.GetLocalMediaSession(), Call.Id, GraphLogger, eventPublisher, _settings);

            if (_settings.CaptureEvents)
            {
                var path = Path.Combine(Path.GetTempPath(), BotConstants.DEFAULT_OUTPUT_FOLDER, _settings.EventsFolder, statefulCall.GetLocalMediaSession().MediaSessionId.ToString(), "participants");
                _capture = new CaptureEvents(path);
            }
        }

        /// <inheritdoc/>
        protected override Task HeartbeatAsync(ElapsedEventArgs args)
        {
            return Call.KeepAliveAsync();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _isDisposed = true;
            Call.OnUpdated -= CallOnUpdated;
            Call.Participants.OnUpdated -= ParticipantsOnUpdated;

            BotMediaStream?.Dispose();

            if (disposing)
            {
                _sottoFinalizeLock?.Dispose();
            }

            // Event - Dispose of the call completed ok
            _eventPublisher.Publish("CallDisposedOK", $"Call.Id: {Call.Id}");
        }

        private void OnRecordingStatusFlip(ICall source)
        {
            _ = Task.Run(async () =>
            {
                // TODO: consider rewriting the recording status checking
                var recordingStatus = new[] { RecordingStatus.Recording, RecordingStatus.NotRecording, RecordingStatus.Failed };

                var recordingIndex = _recordingStatusIndex + 1;
                if (recordingIndex >= recordingStatus.Length)
                {
                    var recordedParticipantId = Call.Resource.IncomingContext.ObservedParticipantId;

                    var recordedParticipant = Call.Participants[recordedParticipantId];
                    await recordedParticipant.DeleteAsync().ConfigureAwait(false);
                    // Event - Recording has ended
                    _eventPublisher.Publish("CallRecordingFlip", $"Call.Id: {Call.Id} ended");
                    return;
                }

                var newStatus = recordingStatus[recordingIndex];
                try
                {
                    // Event - Log the recording status
                    var status = Enum.GetName(typeof(RecordingStatus), newStatus);
                    _eventPublisher.Publish("CallRecordingFlip", $"Call.Id: {Call.Id} status changed to {status}");

                    // NOTE: if your implementation supports stopping the recording during the call, you can call the same method above with RecordingStatus.NotRecording
                    await source
                        .UpdateRecordingStatusAsync(newStatus)
                        .ConfigureAwait(false);

                    _recordingStatusIndex = recordingIndex;
                }
                catch (Exception exc)
                {
                    // e.g. bot joins via direct join - may not have the permissions
                    GraphLogger.Error(exc, $"Failed to flip the recording status to {newStatus}");
                    // Event - Recording status exception - failed to update 
                    _eventPublisher.Publish("CallRecordingFlip", $"Failed to flip the recording status to {newStatus}");
                }
            }).ForgetAndLogExceptionAsync(GraphLogger);
        }

        private async void CallOnUpdated(ICall sender, ResourceEventArgs<Call> e)
        {
            GraphLogger.Info($"Call status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");

            // Event - Recording update e.g established/updated/start/ended
            _eventPublisher.Publish($"Call{e.NewResource.State}", $"Call.ID {Call.Id} Sender.Id {sender.Id} status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");

            if (e.OldResource.State != e.NewResource.State && e.NewResource.State == CallState.Established && !_isDisposed)
            {
                // Call is established. We should start receiving Audio, we can inform clients that we have started recording.
                OnRecordingStatusFlip(sender);

                // Sotto integration: resolve tenant + agent from DynamoDB so we know where to write in S3.
                _ = SottoInitializeSessionAsync(sender);
            }

            if ((e.OldResource.State == CallState.Established) && (e.NewResource.State == CallState.Terminated))
            {
                if (BotMediaStream != null)
                {
                    var aQoE = BotMediaStream.GetAudioQualityOfExperienceData();

                    if (aQoE != null && _settings.CaptureEvents)
                    {
                        await _capture?.Append(aQoE);
                    }
                    await BotMediaStream.StopMedia();
                }

                if (_settings.CaptureEvents)
                {
                    await _capture?.Finalize();
                }

                // Sotto integration: build stereo WAV from SottoAudioBuffer, upload to S3, publish SQS.
                await SottoFinalizeAsync();
            }
        }

        /// <summary>
        /// Sotto integration: resolve Sotto tenant_id and agent_id from the Microsoft tenant/participant IDs
        /// and prime the CallSession state used at finalization.
        /// </summary>
        private async Task SottoInitializeSessionAsync(ICall call)
        {
            try
            {
                var msTenantId = call.Resource?.TenantId ?? string.Empty;
                var observedId = call.Resource?.IncomingContext?.ObservedParticipantId ?? string.Empty;
                var tenantId = await _dynamo.ResolveTenantIdAsync(msTenantId).ConfigureAwait(false) ?? string.Empty;
                var agentId = string.IsNullOrEmpty(tenantId) ? null
                    : await _dynamo.ResolveAgentIdAsync(msTenantId, observedId).ConfigureAwait(false);

                // Extract the OTHER party's identity. For compliance recording
                // the bot's call.Source is typically the recorded user themselves
                // (the user being observed), so we look at Targets first and
                // skip any identity matching observedId. Returns:
                //   PSTN identities → fromNumber populated (E.164)
                //   Teams users     → fromDisplay + fromUpn populated
                var (fromNumber, fromDisplay, fromUpn) = ExtractFromIdentity(call, observedId);

                // DIAGNOSTIC: dump Source + Targets identity shapes so we can
                // see what Microsoft puts in each field for different call
                // types (PSTN, meeting, federated). Logged once per call init;
                // safe to keep on for now since identity is not sensitive
                // beyond what we already log (UPN, display name).
                try
                {
                    var sourceJson = System.Text.Json.JsonSerializer.Serialize(call.Resource?.Source?.Identity, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                    GraphLogger.Info($"DIAG Source.Identity for {call.Id}: {sourceJson}");
                    if (call.Resource?.Targets != null)
                    {
                        int idx = 0;
                        foreach (var t in call.Resource.Targets)
                        {
                            var targetJson = System.Text.Json.JsonSerializer.Serialize(t?.Identity, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                            GraphLogger.Info($"DIAG Targets[{idx}].Identity for {call.Id}: {targetJson}");
                            idx++;
                        }
                    } else {
                        GraphLogger.Info($"DIAG Targets for {call.Id}: <null>");
                    }
                }
                catch (Exception diagEx)
                {
                    GraphLogger.Warn($"DIAG dump failed for {call.Id}: {diagEx.Message}");
                }

                _session = new CallSession
                {
                    CallId = Guid.NewGuid().ToString("N"),
                    MsCallId = call.Id,
                    TenantId = tenantId,
                    AgentId = agentId,
                    Direction = "inbound",
                    FromNumber = fromNumber,
                    FromDisplay = fromDisplay,
                    FromUpn = fromUpn,
                    ToIdentifier = observedId,
                    StartedAt = DateTime.UtcNow,
                };

                GraphLogger.Info($"Sotto session initialized: call_id={_session.CallId} tenant_id={tenantId} agent_id={agentId ?? "<none>"} ms_tenant={msTenantId} from_number=\"{fromNumber}\" from_display=\"{fromDisplay}\" from_upn=\"{fromUpn}\"");
            }
            catch (Exception ex)
            {
                GraphLogger.Error(ex, $"SottoInitializeSessionAsync failed for call {Call.Id}");
            }
        }

        /// <summary>
        /// Best-effort extraction of the OTHER party's display name + UPN — i.e.
        /// the caller for inbound, the callee for outbound, the meeting peer for
        /// meetings. Strategy:
        ///   1. Iterate call.Resource.Targets, take the first identity that
        ///      isn't the observed/recorded user.
        ///   2. Fallback to call.Resource.Source if no qualifying target found
        ///      (some flows put the external caller on Source instead).
        ///   3. Skip any identity matching observedUserId so the recorded user
        ///      themselves never gets returned as the "from" party.
        /// Each identity-extraction attempt handles User (Teams users) and
        /// AdditionalData["phone"] (PSTN). Returns empty strings on no match.
        /// </summary>
        private static (string fromNumber, string displayName, string upn) ExtractFromIdentity(ICall call, string observedUserId)
        {
            try
            {
                // 1) Try Targets first — for compliance recording these are
                //    typically the participants other than the recorded user.
                var targets = call.Resource?.Targets;
                if (targets != null)
                {
                    foreach (var target in targets)
                    {
                        var triple = TryExtractFromIdentitySet(target?.Identity, observedUserId);
                        if (!string.IsNullOrEmpty(triple.fromNumber) || !string.IsNullOrEmpty(triple.displayName) || !string.IsNullOrEmpty(triple.upn))
                            return triple;
                    }
                }

                // 2) Fallback to Source — some PSTN flows put the external
                //    caller here. Skip if it matches the observed user.
                var s = TryExtractFromIdentitySet(call.Resource?.Source?.Identity, observedUserId);
                if (!string.IsNullOrEmpty(s.fromNumber) || !string.IsNullOrEmpty(s.displayName) || !string.IsNullOrEmpty(s.upn))
                    return s;
            }
            catch
            {
                // Defensive: any reflection / null / cast issue → empty.
            }
            return (string.Empty, string.Empty, string.Empty);
        }

        /// <summary>
        /// Extract (displayName, upn-or-phone) from a Microsoft Graph IdentitySet,
        /// returning empty strings if the identity matches observedUserId or no
        /// recognizable identity type is present. User identity wins; falls back
        /// to AdditionalData["phone"] for PSTN parties.
        /// </summary>
        private static (string fromNumber, string displayName, string upn) TryExtractFromIdentitySet(
            Microsoft.Graph.Models.IdentitySet identitySet, string observedUserId)
        {
            if (identitySet == null) return (string.Empty, string.Empty, string.Empty);

            // User identity (Teams users) — no phone number, just display + UPN.
            if (identitySet.User != null)
            {
                var userId = identitySet.User.Id ?? string.Empty;
                // Skip the recorded user themselves
                if (!string.IsNullOrEmpty(observedUserId) && userId == observedUserId)
                {
                    return (string.Empty, string.Empty, string.Empty);
                }
                var displayName = identitySet.User.DisplayName ?? string.Empty;
                var upn = string.Empty;
                if (identitySet.User.AdditionalData != null &&
                    identitySet.User.AdditionalData.TryGetValue("userPrincipalName", out var upnObj))
                {
                    upn = upnObj?.ToString() ?? string.Empty;
                }
                if (string.IsNullOrEmpty(upn)) upn = userId;
                return (string.Empty, displayName, upn);
            }

            // Phone identity (PSTN) under AdditionalData["phone"]:
            //   { "id": "+15551234567", "displayName": "..." }
            // We surface the phone number to BOTH from_number (so the UI
            // renders the number directly) and copy displayName when present
            // (CNAM, if Microsoft delivers it).
            if (identitySet.AdditionalData != null &&
                identitySet.AdditionalData.TryGetValue("phone", out var phoneObj) &&
                phoneObj is System.Text.Json.JsonElement phoneEl &&
                phoneEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var dn = phoneEl.TryGetProperty("displayName", out var dnEl) ? dnEl.GetString() ?? string.Empty : string.Empty;
                var id = phoneEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(dn)) return (id, dn, string.Empty);
            }

            // Older shape: AdditionalData["phoneNumber"] direct E.164 string.
            if (identitySet.AdditionalData != null &&
                identitySet.AdditionalData.TryGetValue("phoneNumber", out var phoneNumObj))
            {
                var phoneNum = phoneNumObj?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(phoneNum)) return (phoneNum, string.Empty, string.Empty);
            }

            return (string.Empty, string.Empty, string.Empty);
        }

        /// <summary>
        /// Sotto integration: called once on CallState.Terminated. Builds a stereo WAV from
        /// the channel-aware audio buffer, uploads to S3 under the tenant's prefix, then
        /// publishes an SQS message so the existing Sotto pipeline (WhisperX, Bedrock, Cockpit push)
        /// picks it up.
        /// </summary>
        private async Task SottoFinalizeAsync()
        {
            if (_sottoFinalized) return;
            await _sottoFinalizeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_sottoFinalized) return;
                _sottoFinalized = true;

                var session = _session ?? new CallSession
                {
                    CallId = Guid.NewGuid().ToString("N"),
                    MsCallId = Call.Id,
                    TenantId = string.Empty,
                    Direction = "inbound",
                    FromNumber = string.Empty,
                    ToIdentifier = string.Empty,
                    StartedAt = DateTime.UtcNow,
                };

                session.EndedAt = DateTime.UtcNow;
                session.DurationSec = (int)(session.EndedAt.Value - session.StartedAt).TotalSeconds;

                if (BotMediaStream == null)
                {
                    GraphLogger.Warn($"SottoFinalizeAsync: BotMediaStream is null for call {Call.Id}, skipping upload");
                    return;
                }

                using var audio = _encoder.Encode(BotMediaStream.SottoAudioBuffer);

                // Encoded stream is empty when no audio was captured.
                if (audio.Length == 0)
                {
                    GraphLogger.Warn($"Sotto call {session.CallId} produced no audio; skipping S3 upload");
                    return;
                }

                if (string.IsNullOrEmpty(session.TenantId))
                {
                    GraphLogger.Warn($"Sotto call {session.CallId} has no resolved tenant_id; uploading under empty prefix (debug only)");
                }

                var ext = _encoder.Options.FileExtension;
                var key = $"{session.TenantId}/recordings/{session.StartedAt:yyyy}/{session.StartedAt:MM}/{session.CallId}.{ext}";
                session.RecordingS3Key = key;

                await _uploader.UploadAsync(audio, key, _encoder.Options.ContentType).ConfigureAwait(false);
                await _uploader.PublishSqsMessageAsync(session).ConfigureAwait(false);

                GraphLogger.Info($"Sotto call finalized: call_id={session.CallId} tenant_id={session.TenantId} s3_key={key} bytes={audio.Length} duration_sec={session.DurationSec} format={_encoder.Options.Codec}/{_encoder.Options.SampleRate}Hz/{_encoder.Options.Channels}ch/{_encoder.Options.BitrateKbps}kbps");
            }
            catch (Exception ex)
            {
                GraphLogger.Error(ex, $"SottoFinalizeAsync failed for call {Call.Id}");
            }
            finally
            {
                // CallOnUpdated is async void so the SDK does not await us; if
                // CallHandler.Dispose runs while our S3 upload is in flight, the
                // semaphore is already disposed by the time we reach this Release.
                try { _sottoFinalizeLock.Release(); }
                catch (ObjectDisposedException) { }
            }
        }

        private static string CreateParticipantUpdateJson(string participantId, string participantDisplayName = "")
        {
            StringBuilder stringBuilder = new();

            stringBuilder.Append('{');
            stringBuilder.AppendFormat("\"Id\": \"{0}\"", participantId);

            if (!string.IsNullOrWhiteSpace(participantDisplayName))
            {
                stringBuilder.AppendFormat(", \"DisplayName\": \"{0}\"", participantDisplayName);
            }

            stringBuilder.Append('}');

            return stringBuilder.ToString();
        }

        private static string UpdateParticipant(List<IParticipant> participants, IParticipant participant, bool added, string participantDisplayName = "")
        {
            if (added)
                participants.Add(participant);
            else
                participants.Remove(participant);
            return CreateParticipantUpdateJson(participant.Id, participantDisplayName);
        }

        private void UpdateParticipants(ICollection<IParticipant> eventArgs, bool added = true)
        {
            foreach (var participant in eventArgs)
            {
                var json = string.Empty;

                // todo remove the cast with the new graph implementation,
                // for now we want the bot to only subscribe to "real" participants
                var participantDetails = participant.Resource.Info.Identity.User;

                if (participantDetails != null)
                {
                    json = UpdateParticipant(BotMediaStream.participants, participant, added, participantDetails.DisplayName);
                }
                else if (participant.Resource.Info.Identity.AdditionalData?.Count > 0 && CheckParticipantIsUsable(participant))
                {
                    json = UpdateParticipant(BotMediaStream.participants, participant, added);
                }

                if (json.Length > 0)
                {
                    if (added)
                    {
                        _eventPublisher.Publish("CallParticipantAdded", json);
                    }
                    else
                    {
                        _eventPublisher.Publish("CallParticipantRemoved", json);
                    }
                }
            }
        }

        public void ParticipantsOnUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
        {
            if (_settings.CaptureEvents)
            {
                _capture?.Append(args);
            }

            UpdateParticipants(args.AddedResources);
            UpdateParticipants(args.RemovedResources, false);

            // Sotto: extract the OTHER party's identity from the participants
            // collection. For compliance recording, call.Resource.Source is
            // ALWAYS the recorded user (Microsoft creates a separate P2P leg
            // bot↔recorded-user), so the actual external caller never appears
            // there. The PSTN caller / meeting peer only shows up in
            // Call.Participants. Algorithm:
            //   1. Skip participants whose Id matches IncomingContext
            //      .ObservedParticipantId (the recorded user themselves).
            //   2. Skip participants with applicationInstance identity
            //      (the bot itself).
            //   3. Phone identity (PSTN) under AdditionalData["phone"] wins
            //      over Teams user identity — phone is the more specific
            //      caller-id signal for inbound PSTN.
            //   4. First non-empty match sticks; subsequent participants
            //      don't overwrite (avoids late-joining meeting attendees
            //      replacing the original caller).
            // Findings sourced from research summarized in docs/teams-engine-b-runbook.md.
            SottoTryUpdateFromIdentity(args.AddedResources);
        }

        private void SottoTryUpdateFromIdentity(ICollection<IParticipant> added)
        {
            if (_session == null) return;
            // Phone wins: once we have a phone number, we stop overwriting.
            if (!string.IsNullOrEmpty(_session.FromNumber)) return;

            var observedId = _session.ToIdentifier ?? string.Empty;

            foreach (var participant in added)
            {
                try
                {
                    var resource = participant?.Resource;
                    var identity = resource?.Info?.Identity;
                    if (resource == null || identity == null) continue;

                    // Skip the recorded user themselves
                    if (!string.IsNullOrEmpty(observedId) && resource.Id == observedId) continue;

                    // Skip the bot itself
                    if (identity.AdditionalData != null &&
                        identity.AdditionalData.ContainsKey("applicationInstance"))
                    {
                        continue;
                    }

                    // 1) Phone identity (PSTN external caller) — highest priority
                    if (identity.AdditionalData != null &&
                        identity.AdditionalData.TryGetValue("phone", out var phoneObj) &&
                        phoneObj is System.Text.Json.JsonElement phoneEl &&
                        phoneEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var num = phoneEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                        var dn  = phoneEl.TryGetProperty("displayName", out var dnEl) ? dnEl.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrEmpty(num))
                        {
                            _session = _session with
                            {
                                FromNumber  = num,
                                FromDisplay = dn,
                                FromUpn     = string.Empty,
                            };
                            GraphLogger.Info($"Sotto: phone caller extracted from participant — number={num} cnam=\"{dn}\" call={Call.Id}");
                            return;
                        }
                    }

                    // 2) Teams user (only used if we don't already have a User
                    //    set AND no phone has won yet for this call).
                    if (identity.User != null && string.IsNullOrEmpty(_session.FromUpn))
                    {
                        var displayName = identity.User.DisplayName ?? string.Empty;
                        var upn = string.Empty;
                        if (identity.User.AdditionalData != null &&
                            identity.User.AdditionalData.TryGetValue("userPrincipalName", out var upnObj))
                        {
                            upn = upnObj?.ToString() ?? string.Empty;
                        }
                        if (string.IsNullOrEmpty(upn)) upn = identity.User.Id ?? string.Empty;

                        if (!string.IsNullOrEmpty(displayName) || !string.IsNullOrEmpty(upn))
                        {
                            _session = _session with
                            {
                                FromNumber  = string.Empty,
                                FromDisplay = displayName,
                                FromUpn     = upn,
                            };
                            GraphLogger.Info($"Sotto: Teams user extracted from participant — display=\"{displayName}\" upn={upn} call={Call.Id}");
                            // Don't return — keep iterating in case a phone
                            // identity arrives later in the same batch.
                        }
                    }
                }
                catch (Exception ex)
                {
                    GraphLogger.Warn($"SottoTryUpdateFromIdentity: failed on a participant — {ex.Message}");
                }
            }
        }

        private static bool CheckParticipantIsUsable(IParticipant p)
        {
            foreach (var i in p.Resource.Info.Identity.AdditionalData)
            {
                if (i.Key != "applicationInstance" && i.Value is Identity)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
