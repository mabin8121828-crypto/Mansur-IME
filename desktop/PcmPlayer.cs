// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mansur.Next.Desktop
{
    // Dedicated, bounded waveOut queue. No audio is written to disk.
    internal sealed class PcmPlayer : IDisposable
    {
        private const int MaximumBytes = 8 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Queue<AudioChunk> queued = new Queue<AudioChunk>();
        private readonly List<NativeBuffer> active = new List<NativeBuffer>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread thread;
        private readonly Action<long, string> failure;
        private IntPtr device;
        private int rate, queuedBytes;
        private long current;
        private bool stopping;
        private string pendingError;
        private sealed class AudioChunk { internal byte[] Pcm; internal int Rate; internal long Id; }
        private sealed class NativeBuffer
        {
            internal GCHandle Pin;
            internal IntPtr Header;
            internal void Free() { if (Header != IntPtr.Zero) Marshal.FreeHGlobal(Header); if (Pin.IsAllocated) Pin.Free(); }
        }
        internal PcmPlayer(Action<long, string> onFailure)
        {
            failure = onFailure;
            thread = new Thread(Loop) { IsBackground = true, Name = "Mansur PCM output" };
            thread.Start();
        }
        internal void Reset(long requestId)
        {
            lock (gate)
            {
                current = requestId; queued.Clear(); queuedBytes = 0;
                if (device != IntPtr.Zero && Native.waveOutReset(device) != 0) pendingError = "audio_stop_failed";
            }
            wake.Set();
        }
        internal bool Enqueue(long requestId, byte[] pcm, int sampleRate)
        {
            if (pcm == null || pcm.Length == 0 || pcm.Length > 96000 || (pcm.Length & 1) != 0 || sampleRate < 8000 || sampleRate > 96000)
                return false;
            lock (gate)
            {
                if (stopping || requestId != current || queuedBytes > MaximumBytes - pcm.Length) return false;
                queued.Enqueue(new AudioChunk { Id = requestId, Pcm = pcm, Rate = sampleRate });
                queuedBytes += pcm.Length;
            }
            wake.Set(); return true;
        }
        // A synthesizer's done event does not mean the queued sound has played.
        // Query on the UI timer instead of posting a callback: replay may reuse
        // the same request ID, and an old callback must not hide a new playback.
        internal bool IsDrained(long requestId)
        {
            lock (gate) return !stopping && requestId == current && pendingError == null &&
                queued.Count == 0 && active.Count == 0;
        }
        private void Loop()
        {
            while (true)
            {
                string error = null;
                long errorId = 0;
                lock (gate)
                {
                    errorId = current;
                    CollectFinished();
                    if (stopping)
                    {
                        CloseDevice();
                        return;
                    }
                    try
                    {
                        while (active.Count < 4 && queued.Count > 0)
                        {
                            var chunk = queued.Peek();
                            if (chunk.Id != current) { queued.Dequeue(); queuedBytes -= chunk.Pcm.Length; continue; }
                            if (device != IntPtr.Zero && rate != chunk.Rate)
                            {
                                if (active.Count != 0) break;
                                CloseDevice();
                                if (device != IntPtr.Zero) throw new InvalidOperationException("Output device remains busy.");
                            }
                            if (device == IntPtr.Zero) OpenDevice(chunk.Rate);
                            queued.Dequeue(); queuedBytes -= chunk.Pcm.Length;
                            Submit(chunk.Pcm);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        queued.Clear(); queuedBytes = 0; current = 0;
                        if (device != IntPtr.Zero) Native.waveOutReset(device);
                        error = "audio_device_failed";
                    }
                    if (pendingError != null) { error = pendingError; pendingError = null; }
                }
                if (error != null) failure(errorId, error);
                wake.WaitOne(200);
            }
        }
        private void OpenDevice(int sampleRate)
        {
            var format = new Native.WaveFormat {
                FormatTag = 1, Channels = 1, SamplesPerSecond = (uint)sampleRate,
                AverageBytesPerSecond = (uint)(sampleRate * 2), BlockAlign = 2, BitsPerSample = 16, ExtraSize = 0
            };
            IntPtr opened;
            if (Native.waveOutOpen(out opened, UInt32.MaxValue, ref format, wake.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x00050000) != 0)
                throw new InvalidOperationException("Unable to open output.");
            device = opened; rate = sampleRate;
        }
        private void Submit(byte[] pcm)
        {
            var buffer = new NativeBuffer { Pin = GCHandle.Alloc(pcm, GCHandleType.Pinned), Header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.WaveHeader))) };
            var header = new Native.WaveHeader { Data = buffer.Pin.AddrOfPinnedObject(), BufferLength = (uint)pcm.Length };
            Marshal.StructureToPtr(header, buffer.Header, false);
            uint size = (uint)Marshal.SizeOf(typeof(Native.WaveHeader));
            if (Native.waveOutPrepareHeader(device, buffer.Header, size) != 0) { buffer.Free(); throw new InvalidOperationException(); }
            if (Native.waveOutWrite(device, buffer.Header, size) != 0)
            {
                if (Native.waveOutUnprepareHeader(device, buffer.Header, size) == 0) buffer.Free();
                else active.Add(buffer); // Retain memory while driver may still own it.
                throw new InvalidOperationException();
            }
            active.Add(buffer);
        }
        private void CollectFinished()
        {
            if (device == IntPtr.Zero) return;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var header = (Native.WaveHeader)Marshal.PtrToStructure(active[i].Header, typeof(Native.WaveHeader));
                if ((header.Flags & 1) != 0 && Native.waveOutUnprepareHeader(device, active[i].Header, (uint)Marshal.SizeOf(typeof(Native.WaveHeader))) == 0)
                { active[i].Free(); active.RemoveAt(i); }
            }
        }
        private void CloseDevice()
        {
            if (device == IntPtr.Zero) return;
            Native.waveOutReset(device);
            CollectFinished();
            if (active.Count == 0 && Native.waveOutClose(device) == 0) { device = IntPtr.Zero; rate = 0; }
            // Do not free pinned memory when an abnormal driver still owns a buffer.
        }
        public void Dispose()
        {
            lock (gate)
            {
                stopping = true; current = 0; queued.Clear(); queuedBytes = 0;
                if (device != IntPtr.Zero) Native.waveOutReset(device);
            }
            wake.Set(); thread.Join(1500);
            // OS reclaims any abnormal driver state at process exit. Callback event remains alive.
        }
        private static class Native
        {
            [StructLayout(LayoutKind.Sequential, Pack = 2)] internal struct WaveFormat
            { internal ushort FormatTag, Channels; internal uint SamplesPerSecond, AverageBytesPerSecond; internal ushort BlockAlign, BitsPerSample, ExtraSize; }
            [StructLayout(LayoutKind.Sequential)] internal struct WaveHeader
            { internal IntPtr Data; internal uint BufferLength, BytesRecorded; internal UIntPtr User; internal uint Flags, Loops; internal IntPtr Next; internal UIntPtr Reserved; }
            [DllImport("winmm.dll")] internal static extern uint waveOutOpen(out IntPtr handle, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
            [DllImport("winmm.dll")] internal static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] internal static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] internal static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] internal static extern uint waveOutReset(IntPtr handle);
            [DllImport("winmm.dll")] internal static extern uint waveOutClose(IntPtr handle);
        }
    }
}
