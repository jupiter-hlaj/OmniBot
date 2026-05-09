using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Common.Telemetry;
using System;

namespace RecordingBot.Services.Http.Controllers
{
    /// <summary>
    /// Sotto disclosure fanout: GET /sotto/pods returns the StatefulSet
    /// replica count this bot is part of. The count is sourced from the
    /// chart-injected BOT_REPLICAS env var, which is wired to the same
    /// Values.scale.replicaCount that drives the StatefulSet's
    /// spec.replicas. Single source of truth in deploy/teams-recording-bot/
    /// values.yaml; no hardcoded count in Sotto's Lambda.
    ///
    /// The agent-disclosure-trigger Lambda calls this once at cold start
    /// to learn how many ordinals to walk for /{N}/sotto/announce
    /// fanout, then caches the result for the duration of the Lambda
    /// container's lifetime. No auth: the count is non-sensitive metadata
    /// (the StatefulSet replica count is already visible from any pod's
    /// own DNS resolution).
    /// </summary>
    [ApiController]
    [Route("sotto")]
    public class SottoPodsController : ControllerBase
    {
        private readonly IGraphLogger _logger;

        public SottoPodsController(IGraphLogger logger)
        {
            _logger = logger;
        }

        public class PodsResponse
        {
            public int PodCount { get; set; }
        }

        [HttpGet("pods")]
        public IActionResult Pods()
        {
            var raw = Environment.GetEnvironmentVariable("BOT_REPLICAS");
            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var count) || count <= 0)
            {
                _logger.Warn($"Sotto: /sotto/pods called but BOT_REPLICAS env var is missing or invalid: \"{raw}\"; returning 503");
                return StatusCode(503, new { error = "BOT_REPLICAS not configured" });
            }
            return Ok(new PodsResponse { PodCount = count });
        }
    }
}
