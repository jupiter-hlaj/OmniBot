using System.Collections.Concurrent;

namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Sotto disclosure playback: per-pod registry of live calls keyed by the
    /// Microsoft call id. CallHandler registers itself on construction and
    /// deregisters on Dispose. SottoAnnounceController looks up the
    /// BotMediaStream for a given call id and invokes PlayWavAsync on it.
    ///
    /// Pod-level fanout: each pod returns 404 if the call id is not in its
    /// registry, and the owner pod serves the request. Cheaper than a stateful
    /// routing service for the three-pod scale we run today.
    /// </summary>
    public sealed class CallRegistry
    {
        private readonly ConcurrentDictionary<string, BotMediaStream> _streams = new();

        public void Register(string callId, BotMediaStream stream)
        {
            if (string.IsNullOrEmpty(callId) || stream is null) return;
            _streams[callId] = stream;
        }

        public void Deregister(string callId)
        {
            if (string.IsNullOrEmpty(callId)) return;
            _streams.TryRemove(callId, out _);
        }

        public bool TryGet(string callId, out BotMediaStream stream)
        {
            stream = null;
            if (string.IsNullOrEmpty(callId)) return false;
            return _streams.TryGetValue(callId, out stream);
        }
    }
}
