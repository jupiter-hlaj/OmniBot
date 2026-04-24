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
    }
}
