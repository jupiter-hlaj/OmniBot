using Microsoft.Skype.Bots.Media;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Sotto disclosure playback: concrete AudioMediaBuffer for sending PCM frames
    /// out via IAudioSocket.Send. Owns an unmanaged byte buffer; the SDK keeps the
    /// buffer alive after Send returns and calls Dispose when it has finished
    /// consuming the frame, at which point we free the unmanaged memory.
    /// </summary>
    internal sealed class SottoOutboundAudioBuffer : AudioMediaBuffer
    {
        private int _disposed;

        public SottoOutboundAudioBuffer(byte[] frame, long timestamp100ns)
        {
            var ptr = Marshal.AllocHGlobal(frame.Length);
            Marshal.Copy(frame, 0, ptr, frame.Length);

            Data = ptr;
            Length = frame.Length;
            Timestamp = timestamp100ns;
            AudioFormat = AudioFormat.Pcm16K;
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }
        }
    }
}
