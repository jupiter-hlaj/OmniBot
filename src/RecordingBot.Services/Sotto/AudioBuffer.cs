using NAudio.Wave;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SottoTeamsBot.Audio;

public sealed class AudioBuffer : IDisposable
{
    private const int SampleRate = 16_000;
    private const int BitsPerSample = 16;

    // 100-ns ticks per PCM sample at 16kHz: 10_000_000 / 16_000 = 625
    public const int TicksPerSample = 625;

    private readonly long _spillThreshold;
    private readonly ConcurrentQueue<(long Timestamp, short[] Samples)>[] _memQueues = { new(), new() };
    private readonly long[] _memBytes = new long[2];
    private readonly string?[] _spillPaths = new string?[2];
    private readonly FileStream?[] _spillStreams = new FileStream?[2];
    private readonly object[] _spillLocks = { new object(), new object() };
    private readonly int[] _spillState = new int[2]; // 0=mem, 1=spilling
    private bool _disposed;

    public AudioBuffer(long spillThresholdBytes = 40L * 1024 * 1024)
    {
        _spillThreshold = spillThresholdBytes;
    }

    public void AppendSamples(int channel, short[] samples, long timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.CompareExchange(ref _spillState[channel], 0, 0) == 0)
        {
            _memQueues[channel].Enqueue((timestamp, samples));
            var total = Interlocked.Add(ref _memBytes[channel], (long)samples.Length * 2);

            if (total >= _spillThreshold)
            {
                lock (_spillLocks[channel])
                {
                    if (Interlocked.CompareExchange(ref _spillState[channel], 0, 0) == 0) // double-check
                    {
                        EnsureSpillFile(channel);
                        while (_memQueues[channel].TryDequeue(out var f))
                            WriteFrameToDisk(channel, f.Timestamp, f.Samples);
                        Interlocked.Exchange(ref _spillState[channel], 1);
                    }
                }
            }
        }
        else
        {
            lock (_spillLocks[channel])
                WriteFrameToDisk(channel, timestamp, samples);
        }
    }

    public MemoryStream BuildStereoWav()
    {
        var ch0 = CollectAllFrames(0);
        var ch1 = CollectAllFrames(1);
        var stereo = AlignAndInterleave(ch0, ch1);

        var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, new WaveFormat(SampleRate, BitsPerSample, 2)))
        {
            if (stereo.Length > 0)
            {
                var bytes = MemoryMarshal.AsBytes(stereo.AsSpan()).ToArray();
                writer.Write(bytes, 0, bytes.Length);
            }
        }
        // MemoryStream.ToArray() works even after the stream is disposed.
        return new MemoryStream(ms.ToArray());
    }

    private List<(long Timestamp, short[] Samples)> CollectAllFrames(int channel)
    {
        var frames = new List<(long Timestamp, short[] Samples)>();

        // Drain in-memory queue first; catches frames stranded during the spill transition.
        while (_memQueues[channel].TryDequeue(out var f))
            frames.Add(f);

        if (_spillStreams[channel] != null)
        {
            _spillStreams[channel]!.Flush();
            _spillStreams[channel]!.Position = 0;
            ReadFramesFromDisk(_spillStreams[channel]!, frames);
        }

        frames.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return frames;
    }

    private void EnsureSpillFile(int channel)
    {
        if (_spillStreams[channel] != null) return;
        var path = Path.GetTempFileName();
        _spillPaths[channel] = path;
        _spillStreams[channel] = new FileStream(path, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 65536, FileOptions.None);
    }

    private void WriteFrameToDisk(int channel, long timestamp, short[] samples)
    {
        var stream = _spillStreams[channel]!;
        // On-disk frame: 8-byte timestamp (little-endian int64) + 4-byte sample count + PCM shorts
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(header[..8], timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], samples.Length);
        stream.Write(header);
        stream.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
    }

    private static void ReadFramesFromDisk(FileStream stream,
        List<(long Timestamp, short[] Samples)> frames)
    {
        Span<byte> header = stackalloc byte[12];
        while (true)
        {
            int read = stream.Read(header);
            if (read == 0) break;
            if (read < 12) throw new IOException("Corrupt spill file: incomplete frame header.");

            var timestamp = BinaryPrimitives.ReadInt64LittleEndian(header[..8]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var samples = new short[count];
            stream.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
            frames.Add((timestamp, samples));
        }
    }

    public static short[] AlignAndInterleave(
        List<(long Timestamp, short[] Samples)> ch0,
        List<(long Timestamp, short[] Samples)> ch1)
    {
        if (ch0.Count == 0 && ch1.Count == 0)
            return Array.Empty<short>();

        long startTick = long.MaxValue;
        long endTick = long.MinValue;

        foreach (var (ts, samples) in ch0.Concat(ch1))
        {
            if (ts < startTick) startTick = ts;
            var frameEnd = ts + (long)samples.Length * TicksPerSample;
            if (frameEnd > endTick) endTick = frameEnd;
        }

        var totalSamples = (int)((endTick - startTick + TicksPerSample - 1) / TicksPerSample);
        var output = new short[totalSamples * 2]; // stereo interleaved: ch0, ch1, ch0, ch1, ...

        static void WriteChannel(List<(long Timestamp, short[] Samples)> frames,
            short[] output, long startTick, int chIdx)
        {
            foreach (var (ts, samples) in frames)
            {
                var offset = (int)((ts - startTick) / TicksPerSample);
                for (int i = 0; i < samples.Length; i++)
                    output[(offset + i) * 2 + chIdx] = samples[i];
            }
        }

        WriteChannel(ch0, output, startTick, chIdx: 0);
        WriteChannel(ch1, output, startTick, chIdx: 1);
        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < 2; i++)
        {
            _spillStreams[i]?.Dispose();
            _spillStreams[i] = null;
            if (_spillPaths[i] != null)
            {
                try { File.Delete(_spillPaths[i]!); } catch { }
                _spillPaths[i] = null;
            }
        }
    }
}
