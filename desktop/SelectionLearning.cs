// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal sealed class SelectionSpan
    {
        internal readonly string Source, Text;
        internal readonly int Start, Length;
        private SelectionSpan(string source, int start, int length)
        { Source = source; Start = start; Length = length; Text = source.Substring(start, length); }
        internal static SelectionSpan Create(string source, int start, int length)
        {
            if (String.IsNullOrEmpty(source) || source.Length > 1024 || start < 0 || length <= 0 || length > 256 || start > source.Length - length) return null;
            string value = source.Substring(start, length);
            if (String.IsNullOrWhiteSpace(value)) return null;
            bool english = false;
            foreach (char c in value) { if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z') english = true; }
            return english ? new SelectionSpan(source, start, length) : null;
        }
        internal bool Same(SelectionSpan other)
        { return other != null && Source == other.Source && Start == other.Start && Length == other.Length; }
    }
    internal sealed class SelectionMeaning
    {
        internal string Meaning, Pronunciation, PartOfSpeech, Usage;
        internal static SelectionMeaning Parse(Dictionary<string, object> value)
        {
            if (value.Count != 4) throw new FormatException("Selection meaning fields.");
            foreach (string key in new[] { "meaning", "pronunciation", "part_of_speech", "usage" }) {
                object raw; if (!value.TryGetValue(key, out raw) || !(raw is string)) throw new FormatException("Selection meaning field type.");
            }
            var result = new SelectionMeaning { Meaning = Json.String(value, "meaning", 400), Pronunciation = Json.String(value, "pronunciation", 120, false),
                PartOfSpeech = Json.String(value, "part_of_speech", 60, false), Usage = Json.String(value, "usage", 400, false) };
            if (String.IsNullOrWhiteSpace(result.Meaning)) throw new FormatException("Empty selection meaning.");
            foreach (string field in new[] { result.Meaning, result.Pronunciation, result.PartOfSpeech, result.Usage })
                foreach (char c in field) if (Char.IsControl(c) || Char.IsSurrogate(c)) throw new FormatException("Invalid selection meaning.");
            return result;
        }
        internal string Display()
        {
            var result = new StringBuilder();
            if (!String.IsNullOrEmpty(Pronunciation)) result.Append("参考读音  ").Append(Pronunciation).AppendLine();
            if (!String.IsNullOrEmpty(PartOfSpeech)) result.Append(PartOfSpeech).Append(" · ");
            result.Append(Meaning);
            if (!String.IsNullOrEmpty(Usage) && Usage != Meaning) result.AppendLine().AppendLine().Append(Usage);
            return result.ToString();
        }
    }
    // One selected span and its result in memory. No editor/clipboard reads or persistent history.
    internal sealed class SelectionLearning
    {
        private long serial = 4503599627370496L;
        internal SelectionSpan Span { get; private set; }
        internal SelectionMeaning Meaning { get; private set; }
        internal long Id { get; private set; }
        internal bool Busy { get; private set; }
        internal bool Speech { get; private set; }
        internal bool SpeechConfirmed { get; private set; }
        internal string Status { get; private set; }
        internal int Rate, Chunks;
        internal readonly MemoryStream Audio = new MemoryStream();
        private bool completeAudio;
        private string audioVoice;
        private double audioSpeed;
        internal double SpeechSpeed { get { return audioSpeed; } }
        internal bool Bound(SelectionSpan value)
        {
            if (Span != null && Span.Same(value) || Span == null && value == null) return false;
            Span = value; Meaning = null; Status = ""; Busy = false; Id = 0; Speech = false;
            Audio.SetLength(0); completeAudio = false; Chunks = Rate = 0; return true;
        }
        internal bool IsCurrent(long id) { return Span != null && Id == id && id > 0; }
        internal object Begin(bool speech, string voice, double speed)
        {
            if (Span == null) return null;
            Id = ++serial; Busy = true; Speech = speech; SpeechConfirmed = false;
            Status = speech ? "正在准备朗读…" : "正在查词…";
            if (speech) { Audio.SetLength(0); completeAudio = false; Chunks = Rate = 0; audioVoice = voice; audioSpeed = speed; }
            return new { op = speech ? "selection_speak" : "selection_study", request_id = Id, text = Span.Text, context = Span.Source, start = Span.Start, voice = voice, speed = speed };
        }
        internal bool CanReplay(string voice, double speed)
        { return completeAudio && Audio.Length > 0 && audioVoice == voice && Math.Abs(audioSpeed - speed) < 0.001; }
        internal void PrepareReplay() { Id = ++serial; Busy = false; Speech = true; Status = ""; }
        internal void Result(Dictionary<string, object> value) { Meaning = SelectionMeaning.Parse(value); }
        internal void ConfirmSpeech(string text)
        { if (!Speech || Span == null || text != Span.Text) throw new FormatException("Selection speech text."); SpeechConfirmed = true; }
        internal void Done()
        {
            if (Speech && Audio.Length == 0 || !Speech && Meaning == null) throw new FormatException("Incomplete selection learning.");
            Busy = false; Status = ""; if (Speech) completeAudio = true;
        }
        internal void Fail(string message) { Busy = false; Status = message; if (Speech) completeAudio = false; }
        internal byte[] ReadAudio(Dictionary<string, object> value)
        {
            int index = (int)Json.Integer(value, "chunk_index", 0, Int32.MaxValue);
            int rate = (int)Json.Integer(value, "sample_rate", 8000, 96000);
            if (!Speech || !SpeechConfirmed || !Busy || index != Chunks || Json.Integer(value, "channels", 1, 1) != 1 || Rate != 0 && Rate != rate) throw new FormatException("Selection audio order.");
            byte[] pcm = Convert.FromBase64String(Json.String(value, "pcm_s16le", 128000));
            if (pcm.Length == 0 || pcm.Length > 96000 || (pcm.Length & 1) != 0 || Audio.Length > 2 * 1024 * 1024 - pcm.Length) throw new FormatException("Selection audio size.");
            Audio.Write(pcm, 0, pcm.Length); Rate = rate; Chunks++; Status = ""; return pcm;
        }
        internal void Cancel() { Busy = false; Id = 0; Status = ""; }
        internal string Display { get { return (Meaning == null ? "" : Meaning.Display()) + (String.IsNullOrEmpty(Status) ? "" : (Meaning == null ? "" : "\r\n\r\n") + Status); } }
    }
}
