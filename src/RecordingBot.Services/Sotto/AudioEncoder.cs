using Microsoft.Extensions.Options;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace SottoTeamsBot.Audio;

/// <summary>
/// Converts the raw 16 kHz stereo PCM produced by <see cref="AudioBuffer"/> into
/// the configured output format (MP3 or WAV) at the configured sample rate,
/// channel count, and bitrate. Only one default is shipped: stereo MP3 at
/// 16 kHz / 64 kbps. Anything else is opt-in via environment variables.
/// </summary>
public sealed class AudioEncoder
{
    private readonly AudioFormatOptions _opts;

    public AudioEncoder(IOptions<AudioFormatOptions> opts)
    {
        _opts = opts.Value;
    }

    public AudioFormatOptions Options => _opts;

    /// <summary>
    /// Builds the encoded audio stream from the given buffer.
    /// Returns an empty MemoryStream if no audio was captured.
    /// </summary>
    public MemoryStream Encode(AudioBuffer buffer)
    {
        var pcm = buffer.BuildStereoPcm16k();
        if (pcm.Length == 0) return new MemoryStream();

        var sourceFormat = new WaveFormat(AudioBuffer.NativeSampleRate, AudioBuffer.NativeBitsPerSample, 2);
        var pcmBytes = MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();

        using var source = new RawSourceWaveStream(new MemoryStream(pcmBytes), sourceFormat);
        ISampleProvider provider = source.ToSampleProvider();

        if (_opts.Channels == 1)
        {
            // Average L+R into mono. Loses per-channel speaker labeling.
            provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        if (_opts.SampleRate != AudioBuffer.NativeSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, _opts.SampleRate);
        }

        var targetFormat = new WaveFormat(_opts.SampleRate, 16, _opts.Channels);
        var output = new MemoryStream();

        if (_opts.IsMp3)
        {
            using var writer = new LameMP3FileWriter(output, targetFormat, _opts.BitrateKbps);
            CopyPcm(provider, writer);
        }
        else
        {
            using var writer = new WaveFileWriter(output, targetFormat);
            CopyPcm(provider, writer);
        }

        // Writers must be disposed before reading the underlying stream.
        return new MemoryStream(output.ToArray());
    }

    private static void CopyPcm(ISampleProvider provider, Stream sink)
    {
        var waveProvider = provider.ToWaveProvider16();
        var buf = new byte[8192];
        int read;
        while ((read = waveProvider.Read(buf, 0, buf.Length)) > 0)
        {
            sink.Write(buf, 0, read);
        }
    }
}
