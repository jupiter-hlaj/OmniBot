using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Common.Telemetry;
using RecordingBot.Services.Bot;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.Services.Http.Controllers
{
    /// <summary>
    /// Sotto disclosure playback (Phase 1 PoC): POST /sotto/announce triggers
    /// IAudioSocket.Send playback of a hardcoded test WAV into the live Teams
    /// call identified by ms_call_id. Gated behind SOTTO_ENABLE_DISCLOSURE_TEST
    /// to keep prod pods from accepting requests on this endpoint at all.
    ///
    /// Pod-level fanout from the routing service: each pod returns 404 if the
    /// call is not in its CallRegistry; the owner pod serves the request.
    ///
    /// Phase 2 will replace the hardcoded WAV with a presigned S3 URL and add
    /// a callback URL for playback status events.
    /// </summary>
    [ApiController]
    [Route("sotto")]
    public class SottoAnnounceController : ControllerBase
    {
        private const string TestWavPath = @"C:\bot\disclosure-test.wav";

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
        public IActionResult Announce([FromBody] AnnounceRequest body)
        {
            // Defense in depth alongside the StreamDirection gate in
            // BotService.CreateLocalMediaSession: even if a prod pod is
            // somehow reachable, the env-var check refuses every request.
            var enabled = string.Equals(
                Environment.GetEnvironmentVariable("SOTTO_ENABLE_DISCLOSURE_TEST"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            if (!enabled)
            {
                _logger.Warn("Sotto: /sotto/announce called but SOTTO_ENABLE_DISCLOSURE_TEST is not set; returning 503");
                return StatusCode(503, new { error = "disclosure_test_disabled" });
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
            // threads and risk the routing-service fanout caller timing out.
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
