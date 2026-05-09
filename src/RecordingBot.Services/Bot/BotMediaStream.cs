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

        // Maps each unique ActiveSpeakerId to a stereo channel (0 or 1).
        // In compliance recording mode buffer.Data is always silence; real audio
        // arrives in UnmixedAudioBuffers, one entry per active speaker.
        private readonly ConcurrentDictionary<uint, int> _speakerChannelMap = new();
        private int _nextChannel;

        /// <summary>
        /// Sotto disclosure playback: tracks whether the audio socket is ready to
        /// accept outbound frames via IAudioSocket.Send. Set true when the SDK raises
        /// AudioSendStatusChanged with MediaSendStatus.Active, false otherwise. The
        /// send loop in PlayWavAsync waits on this before sending the first frame.
        /// </summary>
        private volatile bool _canSendAudio;

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

            // Sotto disclosure playback (Phase 1 PoC): subscribe to outbound send
            // status so PlayWavAsync only calls Send after MediaSendStatus reaches
            // Active. No-op when StreamDirections is Recvonly (the SDK never raises
            // Active in that case, and PlayWavAsync's 5-second wait will time out).
            _audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;
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
                SottoAudioBuffer.Dispose();
            }
        }

        private async void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
            GraphLogger.Info($"Received Audio: [AudioMediaReceivedEventArgs(Data=<{e.Buffer.Data}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp})]");

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
        /// this once StreamDirections=Sendrecv has negotiated outbound RTP and the
        /// platform is ready to accept Send() calls. Inactive status is also
        /// expected at end-of-call or when send is paused; the send loop checks
        /// this flag before each frame.
        /// </summary>
        private void OnAudioSendStatusChanged(object sender, AudioSendStatusChangedEventArgs e)
        {
            GraphLogger.Info($"Sotto: AudioSendStatusChanged for call {_callId}: {e.MediaSendStatus}");
            _canSendAudio = e.MediaSendStatus == MediaSendStatus.Active;
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
            const int FrameBytes = 640;          // 20 ms of PCM 16 kHz mono 16-bit = 320 samples * 2 bytes
            const long FrameTicks = 200_000;     // 20 ms expressed in 100 ns DateTime ticks
            const int WavHeaderBytes = 44;       // standard RIFF/WAVE header for our Polly-generated PCM

            // Wait for outbound to become active. If AudioSendStatusChanged(Active)
            // hasn't fired within 5 seconds, the call is in a state where Send
            // would be rejected; bail rather than queue forever.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!_canSendAudio)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException(
                        $"AudioSendStatus did not reach Active within 5s for call {_callId}");
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            var wav = await File.ReadAllBytesAsync(wavPath, ct).ConfigureAwait(false);
            if (wav.Length <= WavHeaderBytes)
            {
                throw new InvalidOperationException(
                    $"WAV file too small ({wav.Length} bytes), expected RIFF header + PCM data: {wavPath}");
            }

            var pcmBytes = wav.Length - WavHeaderBytes;
            var frameCount = pcmBytes / FrameBytes;
            GraphLogger.Info(
                $"Sotto: PlayWavAsync starting -- file={wavPath} pcm_bytes={pcmBytes} frames={frameCount} call={_callId}");

            var startTicks = DateTime.UtcNow.Ticks;
            for (int i = 0; i < frameCount; i++)
            {
                if (ct.IsCancellationRequested) break;

                var frame = new byte[FrameBytes];
                System.Buffer.BlockCopy(wav, WavHeaderBytes + i * FrameBytes, frame, 0, FrameBytes);

                var ts = startTicks + i * FrameTicks;
                var buffer = new SottoOutboundAudioBuffer(frame, ts);
                try
                {
                    _audioSocket.Send(buffer);
                }
                catch (Exception ex)
                {
                    buffer.Dispose();
                    GraphLogger.Error(ex,
                        $"Sotto: IAudioSocket.Send failed at frame {i}/{frameCount} for call {_callId}");
                    throw;
                }

                // Pace the next send to land at the next 20 ms boundary relative
                // to start, so per-iteration drift doesn't accumulate. Task.Delay
                // rounds up to ~15 ms granularity on Windows, but the SDK's
                // jitter buffer absorbs that.
                var nextSendTicks = startTicks + (i + 1) * FrameTicks;
                var sleepTicks = nextSendTicks - DateTime.UtcNow.Ticks;
                if (sleepTicks > 0)
                {
                    await Task.Delay(TimeSpan.FromTicks(sleepTicks), ct).ConfigureAwait(false);
                }
            }

            GraphLogger.Info(
                $"Sotto: PlayWavAsync complete -- frames_sent={frameCount} call={_callId}");
        }
    }
}
