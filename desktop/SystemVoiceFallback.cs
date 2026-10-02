// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;

namespace Mansur.Next.Desktop
{
    // Explicitly enabled fallback. Its single owned thread receives only the final
    // English from a current confirmed request, never an editor or clipboard.
    internal sealed class SystemVoiceFallback : IDisposable
    {
        internal const int SampleRate = 24000;
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Action<long, byte[], string> result;
        private readonly Thread thread;
        private Work pending;
        private CancellationTokenSource current;
        private bool stopping;
        private sealed class Work { internal long Id; internal string Text; internal double Speed; internal CancellationTokenSource Cancel; }
        internal SystemVoiceFallback(Action<long, byte[], string> completed)
        {
            result = completed;
            thread = new Thread(Run) { IsBackground = true, Name = "Mansur Windows voice" };
            thread.Start();
        }
        internal void Start(long id, string text, double speed)
        {
            if (String.IsNullOrWhiteSpace(text) || text.Length > 4096) throw new ArgumentException();
            lock (gate)
            {
                if (stopping) return;
                if (current != null) current.Cancel();
                if (pending != null) pending.Cancel.Dispose();
                current = new CancellationTokenSource();
                pending = new Work { Id = id, Text = text, Speed = speed, Cancel = current };
            }
            wake.Set();
        }
        internal void Cancel()
        {
            lock (gate)
            {
                if (current != null) current.Cancel();
                if (pending != null) { pending.Cancel.Dispose(); pending = null; current = null; }
            }
        }
        private void Run()
        {
            try
            {
                while (true)
                {
                    wake.WaitOne(); Work work;
                    lock (gate) { if (stopping) return; work = pending; pending = null; }
                    if (work == null) continue;
                    byte[] pcm = null; string error = null;
                    try { pcm = Render(work.Text, work.Speed, work.Cancel.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception) { error = "system_voice_unavailable"; }
                    if (!work.Cancel.IsCancellationRequested) result(work.Id, pcm, error);
                    lock (gate) { if (ReferenceEquals(current, work.Cancel)) current = null; work.Cancel.Dispose(); }
                }
            }
            finally { wake.Dispose(); }
        }
        internal static byte[] Render(string text, double speed, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            using (var synth = new SpeechSynthesizer())
            using (var audio = new BoundedAudio())
            using (var completed = new ManualResetEvent(false))
            {
                var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
                if (voice == null) throw new InvalidOperationException("system_voice_unavailable");
                synth.SelectVoice(voice.VoiceInfo.Name);
                synth.Rate = Math.Max(-10, Math.Min(10, (int)Math.Round(Math.Log(Math.Max(.75, Math.Min(1.25, speed)), 2) * 10)));
                synth.SetOutputToAudioStream(audio, new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                Exception failure = null; bool canceled = false;
                EventHandler<SpeakCompletedEventArgs> handler = delegate(object sender, SpeakCompletedEventArgs e) { failure = e.Error; canceled = e.Cancelled; completed.Set(); };
                synth.SpeakCompleted += handler;
                try {
                    synth.SpeakAsync(text);
                    int which = WaitHandle.WaitAny(new[] { completed, cancel.WaitHandle }, 30000);
                    if (which != 0 || cancel.IsCancellationRequested) { synth.SpeakAsyncCancelAll(); completed.WaitOne(1000); cancel.ThrowIfCancellationRequested(); throw new TimeoutException(); }
                    if (failure != null || canceled || audio.Length == 0 || audio.Length % 2 != 0) throw new InvalidOperationException("system_voice_unavailable");
                    return audio.ToArray();
                }
                finally { synth.SpeakCompleted -= handler; }
            }
        }
        private sealed class BoundedAudio : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            { if (count < 0 || Position > 8 * 1024 * 1024 - count) throw new IOException("voice_audio_limit"); base.Write(buffer, offset, count); }
        }
        public void Dispose()
        { lock (gate) { if (stopping) return; stopping = true; if (current != null) current.Cancel(); if (pending != null) { pending.Cancel.Dispose(); pending = null; current = null; } wake.Set(); } }
    }
}
