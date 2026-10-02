// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal static class UserStateDirectory
    {
        internal static string Root
        {
            get
            {
                string profile = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!SharedSettingsValues.AbsolutePath(profile)) throw new FormatException("user_state_directory_unavailable");
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(profile, ".mansur-next"));
            }
        }
    }

    // The one desktop broker writes this file. Native readers consume complete atomic snapshots.
    // No registry fallback: packaged and ordinary hosts must observe the same physical file.
    internal sealed class SharedSettingsValues : ISettingsValues
    {
        internal const int MaximumBytes = 32768;
        private const string Header = "MansurNextSettings=1";
        private const string LearningHeader = "MansurNextLearningSettings=1";
        private const string WritebackKey = "EnglishWritebackMode";
        private static readonly string[] LearningNames = { WritebackKey, "SystemSpeechFallback", "EnglishSuggestions" };
        internal static readonly string[] Names = { "Theme", "CandidateLayout", "FontSize", "Abbreviation", "FuzzyMask", "ToolbarVisible",
            "Voice", "SpeedPercent", "ToolbarX", "ToolbarY", "UserLexiconPath", "LexiconRevision", "AutoRemember" };
        private readonly string filename;
        private readonly bool learningOnly;
        private readonly SharedSettingsValues learning;
        private string[] AllowedNames { get { return learningOnly ? LearningNames : Names; } }
        private readonly object gate = new object();
        private Dictionary<string, object> cache = Empty();
        private bool observed;
        private DateTime lastWrite;
        private long lastLength;
        internal SharedSettingsValues() : this(UserStateDirectory.Root) { }
        internal SharedSettingsValues(string directory) : this(directory, false) { }
        private SharedSettingsValues(string directory, bool learningFile)
        {
            if (!AbsolutePath(directory)) throw new ArgumentException("Absolute settings directory required.");
            learningOnly = learningFile;
            filename = Path.Combine(Path.GetFullPath(directory), learningOnly ? "learning-preferences.ini" : "preferences.ini");
            // Older native DLLs share preferences.ini and reject unknown keys. Desktop-only
            // learning options therefore live in a separate atomic file.
            if (!learningOnly) learning = new SharedSettingsValues(directory, true);
        }
        private static Dictionary<string, object> Empty() { return new Dictionary<string, object>(StringComparer.Ordinal); }
        public object Get(string name)
        {
            if (!learningOnly && Array.IndexOf(LearningNames, name) >= 0) return learning.Get(name);
            if (Array.IndexOf(AllowedNames, name) < 0) return null;
            lock (gate)
            {
                try
                {
                    var info = new FileInfo(filename);
                    if (!info.Exists) { cache = Empty(); observed = false; return null; }
                    if (!observed || lastWrite != info.LastWriteTimeUtc || lastLength != info.Length)
                    {
                        // A damaged new file never replaces the last successfully parsed snapshot.
                        cache = Parse(ReadBounded(filename, MaximumBytes), learningOnly);
                        lastWrite = info.LastWriteTimeUtc; lastLength = info.Length; observed = true;
                    }
                }
                catch (Exception error) when (Recoverable(error)) { }
                object value; return cache.TryGetValue(name, out value) ? value : null;
            }
        }
        public void Set(string name, object value)
        {
            if (!learningOnly && Array.IndexOf(LearningNames, name) >= 0) { learning.Set(name, value); return; }
            if (Array.IndexOf(AllowedNames, name) < 0 || !Valid(name, value)) throw new ArgumentException("Invalid shared preference.");
            lock (gate) { var values = ReadForWrite(); values[name] = value; Publish(values); }
        }
        public void Delete(string name)
        {
            if (!learningOnly && Array.IndexOf(LearningNames, name) >= 0) { learning.Delete(name); return; }
            if (Array.IndexOf(AllowedNames, name) < 0) throw new ArgumentException("Unknown shared preference.");
            lock (gate) { var values = ReadForWrite(); if (values.Remove(name)) Publish(values); }
        }
        internal void ImportMissing(Dictionary<string, object> additions)
        {
            var own = Empty(); var separate = Empty();
            foreach (var entry in additions)
            {
                if (!Valid(entry.Key, entry.Value)) throw new FormatException("invalid_preference_snapshot");
                if (!learningOnly && Array.IndexOf(LearningNames, entry.Key) >= 0) separate.Add(entry.Key, entry.Value);
                else if (Array.IndexOf(AllowedNames, entry.Key) >= 0) own.Add(entry.Key, entry.Value);
                else throw new FormatException("invalid_preference_snapshot");
            }
            lock (gate)
            {
                if (own.Count != 0)
                {
                    var values = ReadForWrite(); bool changed = false;
                    foreach (var entry in own) if (!values.ContainsKey(entry.Key)) { values.Add(entry.Key, entry.Value); changed = true; }
                    if (changed) Publish(values);
                }
            }
            if (separate.Count != 0) learning.ImportMissing(separate);
        }
        private Dictionary<string, object> ReadForWrite()
        {
            // Writes deliberately do not fall back to cache when the existing file is corrupt.
            return File.Exists(filename) ? Parse(ReadBounded(filename, MaximumBytes), learningOnly) : Empty();
        }
        private void Publish(Dictionary<string, object> values)
        {
            byte[] bytes = Serialize(values, learningOnly); Directory.CreateDirectory(Path.GetDirectoryName(filename));
            string temporary = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteNew(temporary, bytes);
                if (File.Exists(filename)) File.Replace(temporary, filename, null); else File.Move(temporary, filename);
                cache = new Dictionary<string, object>(values, StringComparer.Ordinal); observed = false;
            }
            finally { TryDelete(temporary); }
        }
        internal static bool Recoverable(Exception error)
        { return error is IOException || error is UnauthorizedAccessException || error is FormatException || error is ArgumentException || error is System.Security.SecurityException; }
        internal static byte[] ReadBounded(string path, int maximum)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                byte[] buffer = new byte[maximum + 1]; int count = 0, read;
                while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) != 0) count += read;
                if (count > maximum) throw new FormatException("state_file_too_large");
                Array.Resize(ref buffer, count); return buffer;
            }
        }
        internal static void WriteNew(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        internal static void TryDelete(string path)
        { try { if (File.Exists(path)) File.Delete(path); } catch (Exception error) when (Recoverable(error)) { } }
        internal static Dictionary<string, object> Parse(byte[] bytes)
        { return Parse(bytes, false); }
        private static Dictionary<string, object> Parse(byte[] bytes, bool learningFile)
        {
            if (bytes.Length > MaximumBytes) throw new FormatException("state_file_too_large");
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.IndexOf('\0') >= 0 || text.StartsWith("\ufeff", StringComparison.Ordinal)) throw new FormatException("invalid_preference_file");
            string[] lines = text.Split('\n');
            if (Line(lines[0]) != (learningFile ? LearningHeader : Header)) throw new FormatException("invalid_preference_header");
            string[] names = learningFile ? LearningNames : Names;
            var result = Empty();
            for (int i = 1; i < lines.Length; i++)
            {
                string line = Line(lines[i]); if (line.Length == 0) continue;
                int separator = line.IndexOf('=');
                if (separator < 1) throw new FormatException("invalid_preference_line");
                string name = line.Substring(0, separator), raw = line.Substring(separator + 1);
                if (Array.IndexOf(names, name) < 0 || result.ContainsKey(name)) throw new FormatException("invalid_preference_key");
                object value = raw;
                if (name != "Voice" && name != "UserLexiconPath")
                {
                    int number;
                    if (!IntegerLiteral(raw) || !Int32.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number)) throw new FormatException("invalid_preference_integer");
                    value = number;
                }
                if (!Valid(name, value)) throw new FormatException("invalid_preference_value");
                result.Add(name, value);
            }
            return result;
        }
        private static string Line(string line)
        {
            if (line.EndsWith("\r", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 1);
            if (line.IndexOf('\r') >= 0) throw new FormatException("invalid_preference_line"); return line;
        }
        private static bool IntegerLiteral(string value)
        {
            int start = value.Length > 0 && value[0] == '-' ? 1 : 0;
            if (start == value.Length) return false;
            for (int i = start; i < value.Length; i++) if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }
        internal static bool Valid(string name, object value)
        {
            if (name == "Voice") return value is string && Array.IndexOf(LearningRequest.Voices, (string)value) >= 0;
            if (name == "UserLexiconPath")
            {
                string path = value as string;
                return path != null && path.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0 && (path.Length == 0 || AbsolutePath(path));
            }
            if (!(value is int)) return false; int number = (int)value;
            switch (name)
            {
                case "Theme": return number >= 0 && number <= 2;
                case "CandidateLayout": case "Abbreviation": case "AutoRemember": case "ToolbarVisible": case "EnglishWritebackMode": case "SystemSpeechFallback": case "EnglishSuggestions": return number == 0 || number == 1;
                case "FontSize": return number >= 10 && number <= 20;
                case "FuzzyMask": return number >= 0 && number <= 255;
                case "SpeedPercent": return number >= 75 && number <= 125;
                case "ToolbarX": case "ToolbarY": return true;
                case "LexiconRevision": return number >= 0;
                default: return false;
            }
        }
        internal static bool AbsolutePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return false;
            if (path.Length >= 4 && Slash(path[0]) && Slash(path[1]) && (path[2] == '?' || path[2] == '.') && Slash(path[3])) return false;
            if (path.Length >= 3 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':' && Slash(path[2])) return true;
            if (path.Length < 5 || !Slash(path[0]) || !Slash(path[1]) || Slash(path[2])) return false;
            int serverEnd = 2; while (serverEnd < path.Length && !Slash(path[serverEnd])) serverEnd++;
            return serverEnd + 1 < path.Length && !Slash(path[serverEnd + 1]);
        }
        private static bool Slash(char value) { return value == '\\' || value == '/'; }
        internal static byte[] Serialize(Dictionary<string, object> values)
        { return Serialize(values, false); }
        private static byte[] Serialize(Dictionary<string, object> values, bool learningFile)
        {
            string[] names = learningFile ? LearningNames : Names;
            foreach (var entry in values) if (Array.IndexOf(names, entry.Key) < 0 || !Valid(entry.Key, entry.Value)) throw new FormatException("invalid_preference_value");
            var text = new StringBuilder((learningFile ? LearningHeader : Header) + "\n");
            foreach (string name in names)
            {
                object value; if (!values.TryGetValue(name, out value)) continue;
                text.Append(name).Append('=').Append(value is int ? ((int)value).ToString(CultureInfo.InvariantCulture) : (string)value).Append('\n');
            }
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(text.ToString());
            if (bytes.Length > MaximumBytes) throw new FormatException("state_file_too_large"); return bytes;
        }
    }
}
