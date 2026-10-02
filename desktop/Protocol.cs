// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Mansur.Next.Desktop
{
    internal static class Json
    {
        internal static Dictionary<string, object> Parse(string text, int maximum = 262144)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = maximum, RecursionLimit = 12 };
            var value = serializer.DeserializeObject(text) as Dictionary<string, object>;
            if (value == null) throw new FormatException("Expected JSON object.");
            return value;
        }
        internal static string Write(object value)
        { return new JavaScriptSerializer { MaxJsonLength = 262144 }.Serialize(value); }
        internal static string String(Dictionary<string, object> value, string key, int maximum, bool required = true)
        {
            object raw;
            if (!value.TryGetValue(key, out raw) || raw == null)
            {
                if (required) throw new FormatException("Missing string.");
                return null;
            }
            var text = raw as string;
            if (text == null || text.Length > maximum || (required && text.Length == 0))
                throw new FormatException("Invalid string.");
            return text;
        }
        internal static long Integer(Dictionary<string, object> value, string key, long min, long max)
        {
            object raw;
            if (!value.TryGetValue(key, out raw) || !(raw is int || raw is long))
                throw new FormatException("Expected integer.");
            long result = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            if (result < min || result > max) throw new FormatException("Integer out of range.");
            return result;
        }
        internal static double Number(Dictionary<string, object> value, string key, double min, double max)
        {
            object raw;
            if (!value.TryGetValue(key, out raw) || !(raw is int || raw is long || raw is decimal || raw is double))
                throw new FormatException("Expected number.");
            double result = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (Double.IsNaN(result) || Double.IsInfinity(result) || result < min || result > max)
                throw new FormatException("Number out of range.");
            return result;
        }
    }

    internal sealed class LearningRequest
    {
        internal string Operation, Text, Context, Voice, Sender;
        internal long Revision, Id, SentTicks, SentSequence;
        internal int ScalarCount;
        internal bool CanLearn { get { return ScalarCount <= 256; } }
        internal const string LengthNotice = "文字已提交，本次伴读最多256字";
        internal double? Speed;
        internal Rectangle? Anchor;
        internal EnglishWritebackTarget Writeback;
        internal static readonly string[] Voices = { "af_heart", "af_bella", "am_michael", "bf_emma" };
        internal static LearningRequest Parse(string line)
        {
            var value = Json.Parse(line, 65536);
            var result = new LearningRequest {
                Operation = Json.String(value, "op", 12), Context = Json.String(value, "context", 160),
                Sender = Json.String(value, "sender", 120),
                SentTicks = Json.Integer(value, "sent_ticks", 1, Int64.MaxValue),
                SentSequence = Json.Integer(value, "sent_sequence", 1, Int64.MaxValue)
            };
            if (result.Context.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new FormatException("Invalid context.");
            foreach (char ch in result.Sender) if (Char.IsControl(ch)) throw new FormatException("Invalid sender.");
            if (result.Operation == "cancel") return result;
            if (result.Operation != "learn") throw new FormatException("Unsupported operation.");
            result.Text = Json.String(value, "text", 2048);
            // Accept the core's entire UTF-16 draft before deciding whether the model can handle it.
            // Long valid requests must still invalidate older speech and display an explanation.
            int count = 0;
            for (int i = 0; i < result.Text.Length; i++, count++)
            {
                char ch = result.Text[i];
                if (Char.IsHighSurrogate(ch))
                {
                    if (++i >= result.Text.Length || !Char.IsLowSurrogate(result.Text[i])) throw new FormatException("Invalid Unicode.");
                }
                else if (Char.IsLowSurrogate(ch) || ch == '\0') throw new FormatException("Invalid Unicode.");
            }
            if (count < 1 || String.IsNullOrWhiteSpace(result.Text)) throw new FormatException("Text length out of range.");
            result.ScalarCount = count;
            result.Revision = Json.Integer(value, "revision", 0, Int64.MaxValue);
            object rawWriteback;
            if (value.TryGetValue("writeback", out rawWriteback))
                result.Writeback = EnglishWritebackTarget.ParseOptional(rawWriteback, result.Sender);
            result.Voice = Json.String(value, "voice", 32, false);
            if (result.Voice != null && Array.IndexOf(Voices, result.Voice) < 0) throw new FormatException("Unknown voice.");
            if (value.ContainsKey("speed")) result.Speed = Json.Number(value, "speed", 0.75, 1.25);
            object raw;
            if (value.TryGetValue("anchor", out raw) && raw != null)
            {
                var anchor = raw as Dictionary<string, object>;
                if (anchor == null) throw new FormatException("Invalid anchor.");
                int left = (int)Json.Integer(anchor, "left", -100000, 100000);
                int top = (int)Json.Integer(anchor, "top", -100000, 100000);
                int right = (int)Json.Integer(anchor, "right", -100000, 100000);
                int bottom = (int)Json.Integer(anchor, "bottom", -100000, 100000);
                if (right >= left && bottom > top && right - left <= 10000 && bottom - top <= 10000)
                    result.Anchor = Rectangle.FromLTRB(left, top, right, bottom);
            }
            return result;
        }
        internal object WorkerCommand()
        {
            if (!CanLearn) return new { op = "cancel" };
            return new { op = "learn", request_id = Id, text = Text, voice = Voice, speed = Speed.Value };
        }
    }

    // Broker holds its lock around this class. Fixed history bound prevents untrusted IPC growth.
    internal sealed class RequestTracker
    {
        private sealed class History
        {
            internal string Sender;
            internal long Revision = -1, CancelTicks, CancelSequence;
        }
        internal const int HistoryLimit = 4096;
        private readonly Dictionary<string, History> histories = new Dictionary<string, History>(StringComparer.Ordinal);
        private long nextId;
        private LearningRequest newestLearn;
        internal LearningRequest Current { get; private set; }
        internal int HistoryCount { get { return histories.Count; } }
        private History HistoryFor(LearningRequest request)
        {
            History history;
            if (histories.TryGetValue(request.Context, out history))
                return String.Equals(history.Sender, request.Sender, StringComparison.Ordinal) ? history : null;
            // Never evict a cancel tombstone: eviction could revive a delayed request.
            if (histories.Count >= HistoryLimit) return null;
            history = new History { Sender = request.Sender };
            histories.Add(request.Context, history);
            return history;
        }
        private static int CompareStamp(long ticks, long sequence, long otherTicks, long otherSequence)
        {
            int result = ticks.CompareTo(otherTicks);
            return result != 0 ? result : sequence.CompareTo(otherSequence);
        }
        internal bool Accept(LearningRequest request)
        {
            if (request.Operation != "learn") throw new ArgumentException("Expected learn.");
            if (newestLearn != null)
            {
                if (request.SentTicks < newestLearn.SentTicks) return false;
                // Different processes cannot order events inside one QPC tick. Keep the accepted one.
                if (request.SentTicks == newestLearn.SentTicks &&
                    (!String.Equals(request.Sender, newestLearn.Sender, StringComparison.Ordinal) ||
                     request.SentSequence <= newestLearn.SentSequence)) return false;
            }
            var history = HistoryFor(request);
            if (history == null || request.Revision <= history.Revision ||
                CompareStamp(request.SentTicks, request.SentSequence, history.CancelTicks, history.CancelSequence) <= 0) return false;
            if (nextId == Int64.MaxValue) return false;
            request.Id = ++nextId;
            history.Revision = request.Revision;
            newestLearn = request;
            Current = request;
            return true;
        }
        internal bool Cancel(LearningRequest request)
        {
            if (request.Operation != "cancel") throw new ArgumentException("Expected cancel.");
            var history = HistoryFor(request);
            if (history == null) return false;
            if (CompareStamp(request.SentTicks, request.SentSequence, history.CancelTicks, history.CancelSequence) > 0)
            { history.CancelTicks = request.SentTicks; history.CancelSequence = request.SentSequence; }
            if (Current == null || !String.Equals(Current.Context, request.Context, StringComparison.Ordinal) ||
                CompareStamp(request.SentTicks, request.SentSequence, Current.SentTicks, Current.SentSequence) < 0) return false;
            Current = null;
            return true;
        }
        internal void CancelCurrent(long ticks)
        {
            if (Current == null) return;
            Cancel(new LearningRequest { Operation = "cancel", Context = Current.Context, Sender = Current.Sender,
                SentTicks = Math.Max(ticks, Current.SentTicks), SentSequence = Int64.MaxValue });
        }
        internal bool IsCurrent(long id) { return Current != null && Current.Id == id; }
    }

    internal static class Positioning
    {
        internal static Rectangle Place(Rectangle? anchor, Rectangle work, Size desired)
        {
            int width = Math.Max(1, Math.Min(desired.Width, work.Width));
            int height = Math.Max(1, Math.Min(desired.Height, work.Height));
            int x = work.Right - width - 18, y = work.Top + 18;
            if (anchor.HasValue && anchor.Value.Bottom >= work.Top && anchor.Value.Top <= work.Bottom &&
                anchor.Value.Right >= work.Left && anchor.Value.Left <= work.Right)
            {
                x = anchor.Value.Left;
                y = anchor.Value.Bottom + 10;
                if ((long)y + height > work.Bottom) y = anchor.Value.Top - height - 10;
            }
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            return new Rectangle(x, y, width, height);
        }
    }

    internal static class PcmWave
    {
        internal static byte[] Make(byte[] pcm, int sampleRate)
        {
            if (pcm == null || pcm.Length == 0 || (pcm.Length & 1) != 0 || pcm.Length > 8 * 1024 * 1024 || sampleRate < 8000 || sampleRate > 96000)
                throw new FormatException("Invalid PCM.");
            using (var stream = new MemoryStream(pcm.Length + 44))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(pcm.Length + 36);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
                writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
                return stream.ToArray();
            }
        }
    }
}
