using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Common.Telemetry;
using RecordingBot.Services.Bot;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.Services.Http.Controllers
{
    /// <summary>
    /// Sotto disclosure playback: POST /sotto/announce. Authenticates incoming
    /// requests via HMAC-SHA256 over "{unix_ts}.{raw_body}" using the shared
    /// secret SOTTO_ANNOUNCE_SHARED_SECRET (sourced from K8s Secret
    /// sotto-announce-secret/secret via the Helm chart). Pattern mirrors the
    /// Sotto backend's existing 8x8 adapter (backend/src/layers/common/python/
    /// sotto/adapters/eightbyeight.py): header X-Sotto-Signature with format
    /// "t=&lt;unix_epoch&gt;,v1=&lt;base64-hmac-sha256&gt;", 5-minute clock-skew
    /// window, constant-time compare. Same shared secret lives in AWS Secrets
    /// Manager at sotto/disclosure/announce-shared-secret so the
    /// agent-disclosure-trigger Lambda can sign the requests it forwards from
    /// the Cockpit.
    /// </summary>
    [ApiController]
    [Route("sotto")]
    public class SottoAnnounceController : ControllerBase
    {
        private const string TestWavPath = @"C:\bot\disclosure-test.wav";
        private const int MaxClockSkewSeconds = 300;

        private readonly IGraphLogger _logger;
        private readonly CallRegistry _registry;

        public SottoAnnounceController(IGraphLogger logger, CallRegistry registry)
        {
            _logger = logger;
            _registry = registry;
        }

        public class AnnounceRequest
        {
            public string MsCallId { get; set; }
        }

        [HttpPost("announce")]
        public async Task<IActionResult> Announce(CancellationToken ct)
        {
            var sharedSecret = Environment.GetEnvironmentVariable("SOTTO_ANNOUNCE_SHARED_SECRET") ?? string.Empty;
            if (string.IsNullOrEmpty(sharedSecret))
            {
                _logger.Warn("Sotto: /sotto/announce called but SOTTO_ANNOUNCE_SHARED_SECRET is not configured; returning 503");
                return StatusCode(503, new { error = "announce_secret_not_configured" });
            }

            // Read the raw body once for HMAC verification, then deserialize
            // from the same string. We do not let the model binder consume the
            // stream because the HMAC is computed over the exact bytes the
            // client sent.
            string rawBody;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: false))
            {
                rawBody = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            var sigHeader = Request.Headers["X-Sotto-Signature"].ToString();
            if (string.IsNullOrEmpty(sigHeader))
            {
                _logger.Warn("Sotto: /sotto/announce missing X-Sotto-Signature header");
                return Unauthorized(new { error = "missing_signature" });
            }

            long ts = 0;
            string presentedBase64 = null;
            foreach (var part in sigHeader.Split(','))
            {
                var kv = part.Trim().Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "t" && long.TryParse(kv[1], out var tsParsed)) ts = tsParsed;
                else if (kv[0] == "v1") presentedBase64 = kv[1];
            }
            if (ts == 0 || string.IsNullOrEmpty(presentedBase64))
            {
                _logger.Warn($"Sotto: /sotto/announce malformed signature header: '{sigHeader}'");
                return Unauthorized(new { error = "malformed_signature" });
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(nowUnix - ts) > MaxClockSkewSeconds)
            {
                _logger.Warn($"Sotto: /sotto/announce signature outside clock-skew window (skew={nowUnix - ts}s, max={MaxClockSkewSeconds}s)");
                return Unauthorized(new { error = "signature_expired" });
            }

            var signedPayload = $"{ts}.{rawBody}";
            byte[] expected;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret)))
            {
                expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
            }
            byte[] presented;
            try
            {
                presented = Convert.FromBase64String(presentedBase64);
            }
            catch (FormatException)
            {
                _logger.Warn("Sotto: /sotto/announce v1 segment is not valid base64");
                return Unauthorized(new { error = "malformed_signature_base64" });
            }
            if (!CryptographicOperations.FixedTimeEquals(expected, presented))
            {
                _logger.Warn("Sotto: /sotto/announce HMAC mismatch");
                return Unauthorized(new { error = "invalid_signature" });
            }

            AnnounceRequest body;
            try
            {
                body = JsonSerializer.Deserialize<AnnounceRequest>(rawBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.Warn($"Sotto: /sotto/announce body JSON parse failed: {ex.Message}");
                return BadRequest(new { error = "malformed_json" });
            }
            if (body == null || string.IsNullOrWhiteSpace(body.MsCallId))
            {
                return BadRequest(new { error = "ms_call_id_required" });
            }

            if (!_registry.TryGet(body.MsCallId, out var stream))
            {
                _logger.Info($"Sotto: /sotto/announce 404 -- call {body.MsCallId} not on this pod");
                return NotFound(new { error = "call_not_on_this_pod", ms_call_id = body.MsCallId });
            }

            if (!System.IO.File.Exists(TestWavPath))
            {
                _logger.Error(new FileNotFoundException("test WAV missing", TestWavPath),
                    $"Sotto: /sotto/announce 500 -- test WAV missing at {TestWavPath}");
                return StatusCode(500, new { error = "test_wav_missing", path = TestWavPath });
            }

            // Fire-and-forget the playback so the HTTP response returns 202
            // immediately. PlayWavAsync paces sends across the playback
            // duration of the WAV file (about 5 seconds for the test clip);
            // blocking the HTTP request that long would tie up Kestrel
            // threads and risk the routing-service caller timing out.
            _ = Task.Run(async () =>
            {
                try
                {
                    await stream.PlayWavAsync(TestWavPath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Sotto: PlayWavAsync background task failed for call {body.MsCallId}");
                }
            });

            _logger.Info($"Sotto: /sotto/announce accepted -- call={body.MsCallId} wav={TestWavPath}");
            return Accepted(new { ms_call_id = body.MsCallId, status = "playing" });
        }
    }
}
