using System;
using System.Text.Json;
using NUnit.Framework;
using SottoTeamsBot.Calls;
using SottoTeamsBot.Models;

namespace RecordingBot.Tests.Sotto
{
    [TestFixture]
    public class SqsCallEventTests
    {
        private static CallSession MakeSession()
        {
            return new CallSession
            {
                CallId = "call-0000-1111-2222-3333",
                MsCallId = "ms-call-abc123",
                TenantId = "tenant-xyz",
                AgentId = "agent-456",
                Direction = "inbound",
                FromNumber = "+15551234567",
                FromDisplay = "Jane Caller",
                FromUpn = string.Empty,
                ToIdentifier = "agent@example.com",
                StartedAt = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc),
                EndedAt = new DateTime(2026, 4, 21, 12, 1, 0, DateTimeKind.Utc),
                DurationSec = 60,
                Partial = false,
                RecordingS3Key = "tenant-xyz/recordings/2026/04/call-0000-1111-2222-3333.wav"
            };
        }

        // ── FromSession (call_ended, backwards-compatible) ──────────────

        [Test]
        public void FromSession_DefaultsToCallEndedEventType()
        {
            var evt = SqsCallEvent.FromSession(MakeSession());
            Assert.That(evt.EventType, Is.EqualTo("call_ended"));
        }

        [Test]
        public void FromSession_RecordingAlreadyUploadedIsTrue()
        {
            var evt = SqsCallEvent.FromSession(MakeSession());
            Assert.That(evt.RecordingAlreadyUploaded, Is.True);
        }

        [Test]
        public void FromSession_RecordingFormatIsWav()
        {
            var evt = SqsCallEvent.FromSession(MakeSession());
            Assert.That(evt.RecordingFormat, Is.EqualTo("wav"));
        }

        [Test]
        public void FromSession_StartedAtPopulatedFromSession()
        {
            var session = MakeSession();
            var evt = SqsCallEvent.FromSession(session);
            Assert.That(evt.StartedAt, Is.EqualTo(session.StartedAt.ToString("O")));
        }

        [Test]
        public void FromSession_MapsAllSessionFields()
        {
            var session = MakeSession();
            var evt = SqsCallEvent.FromSession(session);

            Assert.That(evt.TenantId, Is.EqualTo(session.TenantId));
            Assert.That(evt.AgentId, Is.EqualTo(session.AgentId));
            Assert.That(evt.CallId, Is.EqualTo(session.CallId));
            Assert.That(evt.MsCallId, Is.EqualTo(session.MsCallId));
            Assert.That(evt.ProviderCallId, Is.EqualTo(session.MsCallId));
            Assert.That(evt.Direction, Is.EqualTo(session.Direction));
            Assert.That(evt.FromNumber, Is.EqualTo(session.FromNumber));
            Assert.That(evt.FromDisplay, Is.EqualTo(session.FromDisplay));
            Assert.That(evt.ToIdentifier, Is.EqualTo(session.ToIdentifier));
            Assert.That(evt.DurationSec, Is.EqualTo(session.DurationSec));
            Assert.That(evt.RecordingS3Key, Is.EqualTo(session.RecordingS3Key));
        }

        // ── FromSessionStarted (call_started) ───────────────────────────

        [Test]
        public void FromSessionStarted_EventTypeIsCallStarted()
        {
            var evt = SqsCallEvent.FromSessionStarted(MakeSession());
            Assert.That(evt.EventType, Is.EqualTo("call_started"));
        }

        [Test]
        public void FromSessionStarted_RecordingFieldsAreEmpty()
        {
            var evt = SqsCallEvent.FromSessionStarted(MakeSession());
            Assert.That(evt.RecordingAlreadyUploaded, Is.False);
            Assert.That(evt.RecordingS3Key, Is.Empty);
            Assert.That(evt.RecordingUrl, Is.Empty);
            Assert.That(evt.RecordingFormat, Is.Empty);
            Assert.That(evt.DurationSec, Is.EqualTo(0));
            // EndedAt MUST be null (not empty string) — Pydantic Optional[datetime]
            // rejects "" but accepts null. See SqsCallEvent.cs commentary.
            Assert.That(evt.EndedAt, Is.Null);
        }

        [Test]
        public void FromSessionStarted_StartedAtIsPopulated()
        {
            var session = MakeSession();
            var evt = SqsCallEvent.FromSessionStarted(session);
            Assert.That(evt.StartedAt, Is.EqualTo(session.StartedAt.ToString("O")));
        }

        [Test]
        public void FromSessionStarted_CarriesIdentificationAndRoutingFields()
        {
            var session = MakeSession();
            var evt = SqsCallEvent.FromSessionStarted(session);

            Assert.That(evt.Provider, Is.EqualTo("teams"));
            Assert.That(evt.TenantId, Is.EqualTo(session.TenantId));
            Assert.That(evt.AgentId, Is.EqualTo(session.AgentId));
            Assert.That(evt.CallId, Is.EqualTo(session.CallId));
            Assert.That(evt.MsCallId, Is.EqualTo(session.MsCallId));
            Assert.That(evt.ProviderCallId, Is.EqualTo(session.MsCallId));
            Assert.That(evt.Direction, Is.EqualTo(session.Direction));
            Assert.That(evt.ToIdentifier, Is.EqualTo(session.ToIdentifier));
            Assert.That(evt.FromNumber, Is.EqualTo(session.FromNumber));
            Assert.That(evt.FromDisplay, Is.EqualTo(session.FromDisplay));
        }

        // ── FromSessionCallerIdentified (call_caller_identified) ────────

        [Test]
        public void FromSessionCallerIdentified_EventTypeIsCallCallerIdentified()
        {
            var evt = SqsCallEvent.FromSessionCallerIdentified(MakeSession());
            Assert.That(evt.EventType, Is.EqualTo("call_caller_identified"));
        }

        [Test]
        public void FromSessionCallerIdentified_CarriesIdentificationFields()
        {
            var session = MakeSession();
            var evt = SqsCallEvent.FromSessionCallerIdentified(session);

            Assert.That(evt.FromNumber, Is.EqualTo(session.FromNumber));
            Assert.That(evt.FromDisplay, Is.EqualTo(session.FromDisplay));
            Assert.That(evt.AgentId, Is.EqualTo(session.AgentId));
            Assert.That(evt.CallId, Is.EqualTo(session.CallId));
            Assert.That(evt.MsCallId, Is.EqualTo(session.MsCallId));
            Assert.That(evt.ProviderCallId, Is.EqualTo(session.MsCallId));
            Assert.That(evt.TenantId, Is.EqualTo(session.TenantId));
        }

        [Test]
        public void FromSessionCallerIdentified_NonIdentificationFieldsAreDefault()
        {
            var evt = SqsCallEvent.FromSessionCallerIdentified(MakeSession());
            Assert.That(evt.RecordingAlreadyUploaded, Is.False);
            Assert.That(evt.DurationSec, Is.EqualTo(0));
            Assert.That(evt.RecordingUrl, Is.Empty);
            Assert.That(evt.RecordingFormat, Is.Empty);
            // StartedAt + EndedAt MUST be null (not empty strings) for the
            // Python consumer's Pydantic Optional[datetime] to accept them.
            Assert.That(evt.StartedAt, Is.Null);
            Assert.That(evt.EndedAt, Is.Null);
        }

        // ── JSON serialization (cross-language contract with Python) ────

        [Test]
        public void Serialize_CallEnded_ProducesSnakeCaseAndCorrectEventType()
        {
            var evt = SqsCallEvent.FromSession(MakeSession());
            var json = JsonSerializer.Serialize(evt, SqsCallEvent.SerializerOptions);

            Assert.That(json, Does.Contain("\"event_type\":\"call_ended\""));
            Assert.That(json, Does.Contain("\"recording_already_uploaded\":true"));
            Assert.That(json, Does.Contain("\"recording_format\":\"wav\""));
        }

        [Test]
        public void Serialize_CallStarted_ProducesSnakeCaseAndCorrectEventType()
        {
            var evt = SqsCallEvent.FromSessionStarted(MakeSession());
            var json = JsonSerializer.Serialize(evt, SqsCallEvent.SerializerOptions);

            Assert.That(json, Does.Contain("\"event_type\":\"call_started\""));
            Assert.That(json, Does.Contain("\"started_at\":"));
            Assert.That(json, Does.Contain("\"recording_already_uploaded\":false"));
        }

        [Test]
        public void Serialize_CallerIdentified_ProducesSnakeCaseAndCorrectEventType()
        {
            var evt = SqsCallEvent.FromSessionCallerIdentified(MakeSession());
            var json = JsonSerializer.Serialize(evt, SqsCallEvent.SerializerOptions);

            Assert.That(json, Does.Contain("\"event_type\":\"call_caller_identified\""));
            Assert.That(json, Does.Contain("\"from_number\":\"+15551234567\""));
            Assert.That(json, Does.Contain("\"from_display\":\"Jane Caller\""));
        }

        [Test]
        public void Serialize_AllEventTypes_NoPascalCasePropertyNames()
        {
            var ended = JsonSerializer.Serialize(SqsCallEvent.FromSession(MakeSession()), SqsCallEvent.SerializerOptions);
            var started = JsonSerializer.Serialize(SqsCallEvent.FromSessionStarted(MakeSession()), SqsCallEvent.SerializerOptions);
            var ident = JsonSerializer.Serialize(SqsCallEvent.FromSessionCallerIdentified(MakeSession()), SqsCallEvent.SerializerOptions);

            foreach (var json in new[] { ended, started, ident })
            {
                Assert.That(json, Does.Not.Contain("\"TenantId\""));
                Assert.That(json, Does.Not.Contain("\"EventType\""));
                Assert.That(json, Does.Not.Contain("\"FromNumber\""));
                Assert.That(json, Does.Not.Contain("\"FromDisplay\""));
                Assert.That(json, Does.Not.Contain("\"StartedAt\""));
                Assert.That(json, Does.Not.Contain("\"RecordingAlreadyUploaded\""));
            }
        }

        [Test]
        public void Serialize_CallerIdentified_StartedAtAndEndedAtAreJsonNullNotEmptyString()
        {
            // Regression guard: the first end-to-end C-5b test failed because
            // these fields serialized as "" (empty string), which Pydantic's
            // Optional[datetime] rejects with "Input should be a valid
            // datetime or date" — the SQS message dead-lettered after 3
            // retries. Must serialize as JSON null.
            var evt = SqsCallEvent.FromSessionCallerIdentified(MakeSession());
            var json = JsonSerializer.Serialize(evt, SqsCallEvent.SerializerOptions);

            Assert.That(json, Does.Contain("\"started_at\":null"));
            Assert.That(json, Does.Contain("\"ended_at\":null"));
            Assert.That(json, Does.Not.Contain("\"started_at\":\"\""));
            Assert.That(json, Does.Not.Contain("\"ended_at\":\"\""));
        }

        [Test]
        public void Serialize_CallStarted_EndedAtIsJsonNullStartedAtIsIsoString()
        {
            // call_started event: StartedAt populated (call has started),
            // EndedAt null (call hasn't ended).
            var evt = SqsCallEvent.FromSessionStarted(MakeSession());
            var json = JsonSerializer.Serialize(evt, SqsCallEvent.SerializerOptions);

            Assert.That(json, Does.Contain("\"ended_at\":null"));
            Assert.That(json, Does.Not.Contain("\"ended_at\":\"\""));
            Assert.That(json, Does.Match("\"started_at\":\"[0-9]{4}-[0-9]{2}-[0-9]{2}T"));
        }

        [Test]
        public void Serialize_CallStarted_HasAllFieldsRequiredByPythonConsumer()
        {
            // Pydantic NormalizedCallEvent requires these fields to deserialize a
            // call_started event correctly. Schema contract test.
            var evt = SqsCallEvent.FromSessionStarted(MakeSession());
            var json = JsonSerializer.Serialize(evt, SqsCallEvent.SerializerOptions);

            Assert.That(json, Does.Contain("\"tenant_id\""));
            Assert.That(json, Does.Contain("\"provider\""));
            Assert.That(json, Does.Contain("\"provider_call_id\""));
            Assert.That(json, Does.Contain("\"call_id\""));
            Assert.That(json, Does.Contain("\"agent_id\""));
            Assert.That(json, Does.Contain("\"direction\""));
            Assert.That(json, Does.Contain("\"to_identifier\""));
            Assert.That(json, Does.Contain("\"started_at\""));
            Assert.That(json, Does.Contain("\"event_type\""));
        }
    }
}
