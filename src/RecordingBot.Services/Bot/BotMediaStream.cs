using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using RecordingBot.Services.Contract;
using RecordingBot.Services.Media;
using SottoTeamsBot.Audio;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Class responsible for streaming audio and video.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        internal List<IParticipant> participants;
        private readonly IAudioSocket _audioSocket;
        private readonly MediaStream _mediaStream;
        private readonly IEventPublisher _eventPublisher;
        private readonly string _callId;
        public SerializableAudioQualityOfExperienceData AudioQualityOfExperienceData { get; private set; }

        /// <summary>
        /// Sotto integration: channel-aware PCM buffer that captures every audio frame
        /// received from Teams. Read by CallHandler on CallState.Terminated to build
        /// the stereo WAV uploaded to S3.
        /// </summary>
        public AudioBuffer SottoAudioBuffer { get; } = new();

        /// <summary>
        /// Sotto integration: fires once per call, the first time a non-silent
        /// participant audio buffer lands. CallState.Established only signals
        /// that the bot's media plumbing is ready (auto-answer happens
        /// sub-second after invite); the agent picking up is what actually
        /// produces audio frames. CallHandler subscribes to this and emits
        /// the call_answered SQS event so the Cockpit's live-card flips from
        /// "Ringing" (amber) to "On Call" (emerald) at the real pickup moment.
        /// One-shot, guarded by Interlocked.CompareExchange so concurrent
        /// frame deliveries cannot double-fire.
        /// </summary>
        public event EventHandler FirstParticipantAudioReceived;
        private int _firstAudioFired;

        // Maps each unique ActiveSpeakerId to a stereo channel (0 or 1).
        // In compliance recording mode buffer.Data is always silence; real audio
        // arrives in UnmixedAudioBuffers, one entry per active speaker.
        private readonly ConcurrentDictionary<uint, int> _speakerChannelMap = new();
        private int _nextChannel;

        /// <summary>
        /// Sotto disclosure playback: most recent inbound buffer timestamp from
        /// AudioMediaReceived, used as the base tick for appending bot-injected
        /// audio (PlayWavAsync) to SottoAudioBuffer at the right point on the
        /// recording timeline. The SDK's inbound clock is not DateTime ticks; we
        /// must echo the inbound clock for AlignAndInterleave to place our
        /// injected samples in the correct time slot.
        /// </summary>
        private long _lastInboundTimestamp;

        /// <summary>
        /// Sotto disclosure playback: AudioVideoFramePlayer state. Adapted from
        /// Microsoft's EchoBot sample (Samples/PublicSamples/EchoBot), we drive
        /// outbound audio via AudioVideoFramePlayer.EnqueueBuffersAsync rather
        /// than IAudioSocket.Send directly. The VideoSocket parameter is null
        /// because our media session has VideoSocketSettings.Inactive and
        /// EchoBot's audio-only path passes null successfully.
        /// </summary>
        private AudioVideoFramePlayer audioVideoFramePlayer;
        private AudioVideoFramePlayerSettings audioVideoFramePlayerSettings;
        private readonly TaskCompletionSource<bool> audioSendStatusActive = new TaskCompletionSource<bool>();
        private readonly TaskCompletionSource<bool> startVideoPlayerCompleted = new TaskCompletionSource<bool>();
        private readonly List<AudioMediaBuffer> audioMediaBuffers = new List<AudioMediaBuffer>();

        public BotMediaStream(
            ILocalMediaSession mediaSession,
            string callId,
            IGraphLogger logger,
            IEventPublisher eventPublisher,
            IAzureSettings settings) : base(logger)
        {
            ArgumentNullException.ThrowIfNull(mediaSession, nameof(mediaSession));
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            ArgumentNullException.ThrowIfNull(settings, nameof(settings));

            participants = [];

            _eventPublisher = eventPublisher;
            _callId = callId;
            _mediaStream = new MediaStream(settings, logger, mediaSession.MediaSessionId.ToString());

            // Subscribe to the audio media.
            _audioSocket = mediaSession.AudioSocket;
            if (_audioSocket == null)
            {
                throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            }

            _audioSocket.AudioMediaReceived += OnAudioMediaReceived;

            // Sotto disclosure playback: subscribe to outbound send status. The
            // AudioVideoFramePlayer is created once status becomes Active (in
            // StartAudioVideoFramePlayerAsync). Pattern from EchoBot.
            _audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;

            _ = StartAudioVideoFramePlayerAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    GraphLogger.Error(t.Exception, $"Sotto: StartAudioVideoFramePlayerAsync faulted for call {_callId}");
                }
            }, TaskScheduler.Default);
        }

        public List<IParticipant> GetParticipants()
        {
            return participants;
        }

        public SerializableAudioQualityOfExperienceData GetAudioQualityOfExperienceData()
        {
            AudioQualityOfExperienceData = new SerializableAudioQualityOfExperienceData(_callId, _audioSocket.GetQualityOfExperienceData());
            return AudioQualityOfExperienceData;
        }

        public async Task StopMedia()
        {
            await _mediaStream.End();
            // Event - Stop media occurs when the call stops recording
            _eventPublisher.Publish("StopMediaStream", "Call stopped recording");
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            // Event Dispose of the bot media stream object
            _eventPublisher.Publish("MediaStreamDispose", disposing.ToString());

            base.Dispose(disposing);

            _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;
            _audioSocket.AudioSendStatusChanged -= OnAudioSendStatusChanged;

            if (disposing)
            {
                // Sotto disclosure playback: shut down the AudioVideoFramePlayer
                // and dispose any buffers that were built. ShutdownAsync is
                // fire-and-forget because Dispose is sync; the SDK reaps it.
                var player = audioVideoFramePlayer;
                if (player != null)
                {
                    _ = player.ShutdownAsync().ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            GraphLogger.Error(t.Exception, $"Sotto: AudioVideoFramePlayer shutdown failed for call {_callId}");
                        }
                    }, TaskScheduler.Default);
                }
                foreach (var b in audioMediaBuffers)
                {
                    b.Dispose();
                }
                audioMediaBuffers.Clear();

                SottoAudioBuffer.Dispose();
            }
        }

        private async void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
            GraphLogger.Info($"Received Audio: [AudioMediaReceivedEventArgs(Data=<{e.Buffer.Data}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp})]");

            // Track the SDK's inbound clock so PlayWavAsync can timestamp
            // bot-injected audio in the same reference frame for the recording.
            _lastInboundTimestamp = e.Buffer.Timestamp;

            // Sotto integration: in Teams compliance recording mode, buffer.Data is always
            // silence. Real audio arrives in UnmixedAudioBuffers — one entry per active
            // speaker. First two distinct ActiveSpeakerIds map to channel 0 and 1.
            // Use e.Buffer.Timestamp (arrival clock) for all entries so AlignAndInterleave
            // can build a coherent stereo timeline across both channels.
            try
            {
                if (e.Buffer.UnmixedAudioBuffers != null)
                {
                    foreach (var unmixed in e.Buffer.UnmixedAudioBuffers)
                    {
                        if (unmixed.Length <= 0) continue;

                        // First real participant audio = agent (or peer) picked up.
                        // Fire the one-shot pickup signal exactly once.
                        if (Interlocked.CompareExchange(ref _firstAudioFired, 1, 0) == 0)
                        {
                            try
                            {
                                FirstParticipantAudioReceived?.Invoke(this, EventArgs.Empty);
                            }
                            catch (Exception subEx)
                            {
                                GraphLogger.Error(subEx, $"Sotto: FirstParticipantAudioReceived subscriber threw for call {_callId}");
                            }
                        }

                        var channel = _speakerChannelMap.GetOrAdd(
                            unmixed.ActiveSpeakerId,
                            _ => Math.Min(Interlocked.Increment(ref _nextChannel) - 1, 1));

                        var length = (int)unmixed.Length;
                        var bytes = new byte[length];
                        Marshal.Copy(unmixed.Data, bytes, 0, length);
                        var samples = new short[length / 2];
                        System.Buffer.BlockCopy(bytes, 0, samples, 0, length);
                        SottoAudioBuffer.AppendSamples(channel, samples, e.Buffer.Timestamp);
                    }
                }
            }
            catch (Exception sottoEx)
            {
                GraphLogger.Error(sottoEx, "Sotto audio buffer append failed");
            }

            try
            {
                await _mediaStream.AppendAudioBuffer(e.Buffer, participants);
                e.Buffer.Dispose();
            }
            catch (Exception ex)
            {
                GraphLogger.Error(ex);
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        /// <summary>
        /// Sotto disclosure playback: outbound send-status callback. The SDK raises
        /// MediaSendStatus.Active once StreamDirections=Sendrecv has negotiated
        /// outbound RTP and the platform is ready. We complete audioSendStatusActive
        /// so StartAudioVideoFramePlayerAsync (running concurrently from the ctor)
        /// can proceed to create the AudioVideoFramePlayer and enqueue the
        /// diagnostic WAV. Pattern from EchoBot.
        /// </summary>
        private void OnAudioSendStatusChanged(object sender, AudioSendStatusChangedEventArgs e)
        {
            GraphLogger.Info($"Sotto: AudioSendStatusChanged for call {_callId}: {e.MediaSendStatus}");
            if (e.MediaSendStatus == MediaSendStatus.Active)
            {
                audioSendStatusActive.TrySetResult(true);
            }
        }

        /// <summary>
        /// Sotto disclosure playback (Phase 1 PoC): loads a 16 kHz mono 16-bit PCM
        /// WAV from disk and streams it into the live call by pacing 20 ms frames
        /// into IAudioSocket.Send at 50 frames per second. The SDK takes ownership
        /// of each SottoOutboundAudioBuffer and disposes it when finished.
        ///
        /// Requires the audio socket to be Sendrecv (gated on
        /// SOTTO_ENABLE_DISCLOSURE_TEST in BotService.CreateLocalMediaSession) and
        /// to have published an AudioSendStatusChanged event with
        /// MediaSendStatus.Active. Recording continues uninterrupted alongside,
        /// since AudioMediaReceived is unaffected by enabling the send direction
        /// on the same socket.
        /// </summary>
        public async Task PlayWavAsync(string wavPath, CancellationToken ct = default)
        {
            // Wait for the AudioVideoFramePlayer to be created. StartAudioVideoFramePlayerAsync
            // sets startVideoPlayerCompleted in its finally block whether it succeeded or
            // failed; if it failed, audioVideoFramePlayer remains null and we throw
            // with that context rather than NRE inside EnqueueBuffersAsync.
            await startVideoPlayerCompleted.Task.ConfigureAwait(false);
            if (audioVideoFramePlayer == null)
            {
                throw new InvalidOperationException(
                    $"AudioVideoFramePlayer was not created for call {_callId}; see prior errors");
            }

            var wav = await File.ReadAllBytesAsync(wavPath, ct).ConfigureAwait(false);
            var startTick = DateTime.Now.Ticks;
            var buffers = CreateAudioMediaBuffers(wav, startTick);
            if (buffers.Count == 0)
            {
                GraphLogger.Warn($"Sotto: PlayWavAsync produced 0 frames from {wavPath} for call {_callId}");
                return;
            }

            // Track buffers so Dispose can free unmanaged memory if the call ends
            // before all frames are consumed.
            audioMediaBuffers.AddRange(buffers);

            // Append the same PCM samples to the recording buffer so the
            // disclosure shows up in the audit-trail MP3 and the transcript.
            // Microsoft does not loop the bot's outbound back via
            // AudioMediaReceived, so without this the recording would only
            // have participant audio. We use the most recent inbound
            // timestamp as the base so AlignAndInterleave places the
            // disclosure at the correct point on the recording timeline.
            // Append to both channels so it's audible in the stereo mix
            // regardless of which speaker channel ends up active.
            // AlignAndInterleave was changed in this commit to mix-with-clamp,
            // so this overlay does not destroy participant audio at the
            // same timestamps.
            var recordingBaseTick = _lastInboundTimestamp;
            if (recordingBaseTick > 0)
            {
                const int FrameBytesLocal = 640;
                const int WavHeaderBytesLocal = 44;
                const long FrameTicksLocal = 20 * 10000;
                var pcmLen = wav.Length - WavHeaderBytesLocal;
                var frameCount = pcmLen / FrameBytesLocal;
                for (int i = 0; i < frameCount; i++)
                {
                    var samples0 = new short[FrameBytesLocal / 2];
                    System.Buffer.BlockCopy(wav, WavHeaderBytesLocal + i * FrameBytesLocal, samples0, 0, FrameBytesLocal);
                    // Separate copy for channel 1; AudioBuffer holds the
                    // array reference and we must not share it across channels.
                    var samples1 = new short[samples0.Length];
                    Array.Copy(samples0, samples1, samples0.Length);
                    var ts = recordingBaseTick + i * FrameTicksLocal;
                    SottoAudioBuffer.AppendSamples(0, samples0, ts);
                    SottoAudioBuffer.AppendSamples(1, samples1, ts);
                }
                GraphLogger.Info(
                    $"Sotto: appended {frameCount} disclosure frames to recording (base_tick={recordingBaseTick}) for call {_callId}");
            }
            else
            {
                GraphLogger.Warn(
                    $"Sotto: no inbound timestamp seen yet for call {_callId}; disclosure NOT appended to recording");
            }

            GraphLogger.Info(
                $"Sotto: PlayWavAsync enqueueing -- file={wavPath} frames={buffers.Count} call={_callId}");
            await audioVideoFramePlayer.EnqueueBuffersAsync(buffers, new List<VideoMediaBuffer>()).ConfigureAwait(false);
            GraphLogger.Info(
                $"Sotto: PlayWavAsync EnqueueBuffersAsync returned -- frames={buffers.Count} call={_callId}");
        }

        /// <summary>
        /// Sotto disclosure playback: build a list of AudioSendBuffer instances from a
        /// 16 kHz mono 16-bit PCM WAV's bytes, with timestamps starting at startTick
        /// and incrementing by 20 ms (200000 ticks) per frame. Direct port of
        /// Microsoft's EchoBot/AudioVideoPlaybackBot Utilities.CreateAudioMediaBuffers.
        /// AudioSendBuffer is the SDK's concrete AudioMediaBuffer subclass shipping in
        /// the Microsoft.Graph.Communications.Calls.Media NuGet; it frees the unmanaged
        /// buffer in its own Dispose, so the caller only needs to hold a list and call
        /// Dispose on each entry at end-of-call.
        /// </summary>
        private static List<AudioMediaBuffer> CreateAudioMediaBuffers(byte[] wavBytes, long startTick)
        {
            const int FrameBytes = 640;            // 20 ms of PCM 16 kHz mono 16-bit
            const long FrameTicks = 20 * 10000;    // 20 ms in 100 ns DateTime ticks
            const int WavHeaderBytes = 44;         // standard RIFF/WAVE header

            var buffers = new List<AudioMediaBuffer>();
            if (wavBytes == null || wavBytes.Length <= WavHeaderBytes) return buffers;

            var pcmLen = wavBytes.Length - WavHeaderBytes;
            var frameCount = pcmLen / FrameBytes;
            var refTime = startTick;

            for (int i = 0; i < frameCount; i++)
            {
                IntPtr unmanagedBuffer = Marshal.AllocHGlobal(FrameBytes);
                Marshal.Copy(wavBytes, WavHeaderBytes + i * FrameBytes, unmanagedBuffer, FrameBytes);
                buffers.Add(new AudioSendBuffer(unmanagedBuffer, FrameBytes, AudioFormat.Pcm16K, refTime));
                refTime += FrameTicks;
            }

            return buffers;
        }

        /// <summary>
        /// Sotto disclosure playback: wait for outbound audio to become Active, create
        /// the AudioVideoFramePlayer with a null VideoSocket (audio-only path proven
        /// by EchoBot for VideoSocketSettings.Inactive), then (Phase 1 diagnostic)
        /// auto-fire the test WAV. The auto-trigger is removed in the follow-up
        /// commit when the announce endpoint becomes the trigger.
        /// </summary>
        private async Task StartAudioVideoFramePlayerAsync()
        {
            try
            {
                // Bound the wait so a Microsoft-side rejection of Sendrecv does not
                // leave external PlayWavAsync callers blocked forever.
                var winner = await Task.WhenAny(
                    audioSendStatusActive.Task,
                    Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
                if (winner != audioSendStatusActive.Task)
                {
                    GraphLogger.Warn($"Sotto: AudioSendStatus did not reach Active within 15s for call {_callId}; outbound playback unavailable");
                    return;
                }

                GraphLogger.Info($"Sotto: audio send is Active for call {_callId}, creating AudioVideoFramePlayer");

                audioVideoFramePlayerSettings = new AudioVideoFramePlayerSettings(
                    new AudioSettings(20), new VideoSettings(), 1000);
                audioVideoFramePlayer = new AudioVideoFramePlayer(
                    (AudioSocket)_audioSocket,
                    null,
                    audioVideoFramePlayerSettings);
                GraphLogger.Info($"Sotto: AudioVideoFramePlayer created for call {_callId}");

                // Player is ready; external callers (SottoAnnounceController)
                // can now invoke PlayWavAsync. The diagnostic auto-fire that
                // played the test WAV at call start has been removed; the
                // announce endpoint is the only trigger.
                startVideoPlayerCompleted.TrySetResult(true);
            }
            catch (Exception ex)
            {
                GraphLogger.Error(ex, $"Sotto: StartAudioVideoFramePlayerAsync failed for call {_callId}");
            }
            finally
            {
                // Failsafe: if we returned early or threw before setting it above.
                startVideoPlayerCompleted.TrySetResult(true);
            }
        }
    }
}
