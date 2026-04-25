namespace SottoTeamsBot.Audio;

/// <summary>
/// Output audio format used when finalizing a call recording.
/// All defaults match the recommended setting -- stereo MP3 at 16 kHz, 64 kbps --
/// which keeps perfect speaker separation (left = first speaker, right = second
/// speaker) and produces files ~5 MB per 10-minute call.
///
/// Override via environment variables:
///   Sotto__AudioFormat__Codec        ("mp3" | "wav")
///   Sotto__AudioFormat__SampleRate   (Hz, e.g. 16000, 8000)
///   Sotto__AudioFormat__BitrateKbps  (kbps, MP3 only)
///   Sotto__AudioFormat__Channels     (1 = mono, 2 = stereo)
/// </summary>
public sealed class AudioFormatOptions
{
    public string Codec { get; set; } = "mp3";
    public int SampleRate { get; set; } = 16_000;
    public int BitrateKbps { get; set; } = 64;
    public int Channels { get; set; } = 2;

    public bool IsMp3 => Codec.Equals("mp3", StringComparison.OrdinalIgnoreCase);
    public string ContentType => IsMp3 ? "audio/mpeg" : "audio/wav";
    public string FileExtension => IsMp3 ? "mp3" : "wav";
}
