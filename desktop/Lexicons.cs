// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Mansur.Next.Desktop
{
    internal sealed class LexiconEntry
    {
        internal string Pinyin, Text, Syllables;
        internal double Frequency;
        internal string Key { get { return Pinyin + "\t" + Text; } }
        internal string Row { get { return Pinyin + "\t" + Text + "\t" + Frequency.ToString("G17", CultureInfo.InvariantCulture) + "\t" + Syllables; } }
    }
    internal sealed class LexiconParseResult
    {
        internal readonly List<LexiconEntry> Entries = new List<LexiconEntry>();
        internal int Rejected, MissingPinyin;
    }
    internal static class LexiconParser
    {
        internal const int MaximumBytes = 4 * 1024 * 1024, MaximumEntries = 10000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static LexiconParseResult ReadFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) throw new FormatException("词库文件超过 4 MiB 或无法读取。");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumBytes) throw new FormatException("词库文件超过 4 MiB。");
            return Parse(Utf8.GetString(bytes).TrimStart('\uFEFF'), path.EndsWith(".dict.yaml", StringComparison.OrdinalIgnoreCase));
        }
        internal static LexiconParseResult Parse(string content, bool rime)
        {
            var result = new LexiconParseResult();
            bool data = !rime, headerSeen = false, columns = false;
            var names = new List<string>();
            using (var reader = new StringReader(content))
            {
                string line; int lineCount = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (++lineCount > 100000) throw new FormatException("词库行数超过上限。");
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (!data)
                    {
                        if (trimmed == "---") { headerSeen = true; continue; }
                        if (trimmed == "...") { data = true; continue; }
                        if (trimmed.StartsWith("columns:", StringComparison.Ordinal))
                        {
                            columns = true; names.Clear();
                            string inline = trimmed.Substring(8).Trim();
                            if (inline.Length > 0)
                            {
                                if (!inline.StartsWith("[", StringComparison.Ordinal) || !inline.EndsWith("]", StringComparison.Ordinal)) throw new FormatException("不支持此 Rime columns 写法。");
                                foreach (var name in inline.Substring(1, inline.Length - 2).Split(',')) names.Add(name.Trim().Trim('\'', '"'));
                                columns = false;
                            }
                            continue;
                        }
                        if (columns && trimmed.StartsWith("- ", StringComparison.Ordinal)) { names.Add(trimmed.Substring(2).Trim().Trim('\'', '"')); continue; }
                        columns = false;
                        // Header is inert text. No YAML parser, tag evaluation or import recursion is used.
                        continue;
                    }
                    string[] fields = line.Split('\t');
                    string spelling = null, word = null, weight = "10000", syllables = "";
                    if (rime)
                    {
                        int textIndex = names.Count == 0 ? 0 : names.IndexOf("text");
                        int codeIndex = names.Count == 0 ? 1 : names.IndexOf("code");
                        int weightIndex = names.Count == 0 ? 2 : names.IndexOf("weight");
                        if (codeIndex < 0 || codeIndex >= fields.Length || String.IsNullOrWhiteSpace(fields[codeIndex])) { result.Rejected++; result.MissingPinyin++; continue; }
                        if (textIndex < 0 || textIndex >= fields.Length) { result.Rejected++; continue; }
                        word = fields[textIndex].Trim(); spelling = fields[codeIndex].Trim();
                        if (weightIndex >= 0 && weightIndex < fields.Length && fields[weightIndex].Trim().Length > 0) weight = fields[weightIndex].Trim();
                        syllables = spelling.Replace('\'', ' ');
                        spelling = Regex.Replace(syllables, @"\s+", "");
                        if (syllables.IndexOf(' ') < 0) syllables = "";
                    }
                    else
                    {
                        if (fields.Length < 3 || fields.Length > 4) { result.Rejected++; continue; }
                        spelling = fields[0].Trim(); word = fields[1].Trim(); weight = fields[2].Trim();
                        if (fields.Length == 4) syllables = fields[3].Trim();
                        if (spelling.Length == 0) { result.Rejected++; result.MissingPinyin++; continue; }
                    }
                    LexiconEntry entry;
                    if (!TryEntry(spelling, word, weight, syllables, rime, out entry)) { result.Rejected++; continue; }
                    result.Entries.Add(entry);
                    if (result.Entries.Count > 50000) throw new FormatException("词库有效行数超过上限。");
                }
            }
            if (rime && (!headerSeen || !data)) throw new FormatException("Rime 词库需包含 --- 头部和 ... 后的数据区。");
            return result;
        }
        private static bool TryEntry(string pinyin, string text, string weight, string syllables, bool percentage, out LexiconEntry entry)
        {
            entry = null;
            pinyin = pinyin.ToLowerInvariant().Replace('ü', 'v');
            syllables = Regex.Replace(syllables.ToLowerInvariant().Replace('ü', 'v').Trim(), @" +", " ");
            if (!Regex.IsMatch(pinyin, "^[a-z]{1,64}$") || text.Length == 0 || text.Any(Char.IsControl)) return false;
            try { if (Utf8.GetByteCount(text) > 768) return false; } catch (EncoderFallbackException) { return false; }
            if (syllables.Length > 0 && (!Regex.IsMatch(syllables, "^[a-z]+( [a-z]+)*$") || syllables.Replace(" ", "") != pinyin || syllables.Split(' ').Any(s => !PinyinSyllables.All.Contains(s)))) return false;
            bool percent = percentage && weight.EndsWith("%", StringComparison.Ordinal);
            if (percent) weight = weight.Substring(0, weight.Length - 1);
            double frequency;
            if (!Double.TryParse(weight, NumberStyles.Float, CultureInfo.InvariantCulture, out frequency)) return false;
            if (percent) frequency *= 100;
            if (Double.IsNaN(frequency) || Double.IsInfinity(frequency) || frequency <= 0 || frequency > 1e12) return false;
            entry = new LexiconEntry { Pinyin = pinyin, Text = text, Frequency = frequency, Syllables = syllables };
            return true;
        }
        internal static List<LexiconEntry> Merge(IEnumerable<LexiconEntry> old, IEnumerable<LexiconEntry> incoming)
        {
            var map = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);
            foreach (var entry in old.Concat(incoming))
            {
                LexiconEntry existing;
                if (!map.TryGetValue(entry.Key, out existing)) map.Add(entry.Key, entry);
                else map[entry.Key] = new LexiconEntry { Pinyin = entry.Pinyin, Text = entry.Text,
                    Frequency = Math.Max(existing.Frequency, entry.Frequency), Syllables = entry.Syllables.Length > 0 ? entry.Syllables : existing.Syllables };
                if (map.Count > MaximumEntries) throw new FormatException("合并后超过 10,000 条用户词条，本次未更改词库。");
            }
            return map.Values.OrderBy(e => e.Pinyin, StringComparer.Ordinal).ThenBy(e => e.Text, StringComparer.Ordinal).ToList();
        }
        internal static string Serialize(IEnumerable<LexiconEntry> entries)
        {
            string text = "# Mansur UTF-8 TSV: compact_pinyin<TAB>text<TAB>frequency<TAB>syllables\n" + String.Join("\n", entries.Select(e => e.Row)) + "\n";
            if (Utf8.GetByteCount(text) > MaximumBytes) throw new FormatException("合并后的用户词库超过 4 MiB，本次未更改词库。");
            return text;
        }
    }
    internal sealed class LexiconImportResult
    {
        internal int Accepted, Rejected, MissingPinyin, Added, Total;
    }
    internal sealed class LexiconManager : IDisposable
    {
        private readonly SettingsStore settings;
        private readonly string root, baseDictionary, compiler;
        private readonly Func<string, string, string, bool> compile;
        private readonly object processGate = new object();
        private Process activeCompiler;
        private bool disposed;
        internal LexiconManager(SettingsStore store, string dataRoot, string binaryRoot, Func<string, string, string, bool> compileOverride = null)
        {
            settings = store; root = Path.GetFullPath(dataRoot);
            baseDictionary = Path.Combine(binaryRoot, "x64", "data", "base.mlex");
            compiler = Path.Combine(binaryRoot, "x64", "mansur_lexicon_compile.exe");
            compile = compileOverride ?? Compile;
        }
        private List<LexiconEntry> Existing()
        {
            string active = settings.Read().UserLexiconPath;
            if (active.Length == 0) return new List<LexiconEntry>();
            string full = Path.GetFullPath(active);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("当前用户词库不在管理目录，本次保留原词库。");
            var parsed = LexiconParser.ReadFile(Path.Combine(Path.GetDirectoryName(full), "user.tsv"));
            if (parsed.Rejected != 0) throw new FormatException("已有用户词库校验失败，本次保留原词库。");
            return parsed.Entries;
        }
        internal LexiconImportResult Import(string[] files)
        {
            var incoming = new List<LexiconEntry>(); var result = new LexiconImportResult();
            foreach (string file in files)
            {
                var parsed = LexiconParser.ReadFile(file); incoming = LexiconParser.Merge(incoming, parsed.Entries);
                result.Accepted += parsed.Entries.Count; result.Rejected += parsed.Rejected; result.MissingPinyin += parsed.MissingPinyin;
            }
            if (incoming.Count == 0) throw new FormatException("未找到有效词条；需包含拼音、文字和词频。缺拼音的 Rime 词条不能导入。");
            var old = Existing(); var merged = LexiconParser.Merge(old, incoming);
            result.Added = merged.Count - LexiconParser.Merge(old, new LexiconEntry[0]).Count; result.Total = merged.Count;
            string serialized = LexiconParser.Serialize(merged);
            string version = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(version);
            string source = Path.Combine(version, "user.tsv"), output = Path.Combine(version, "merged.mlex");
            File.WriteAllText(source, serialized, new UTF8Encoding(false));
            // Unreferenced failed versions are retained for recovery; no previously active file is overwritten.
            if (!compile(baseDictionary, source, output) || !File.Exists(output) || new FileInfo(output).Length < 16)
                throw new IOException("词库编译失败，原生效词库保持不变。");
            lock (processGate) { if (disposed) throw new OperationCanceledException(); settings.PublishLexicon(output); }
            return result;
        }
        internal void Export(string path)
        {
            var entries = Existing();
            if (entries.Count == 0) throw new FormatException("尚未导入用户词库。");
            string target = Path.GetFullPath(path), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            if (target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("请导出到词库管理目录之外，以保留已有版本。");
            try
            {
                File.WriteAllText(temp, LexiconParser.Serialize(entries), new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private bool Compile(string basePath, string source, string output)
        {
            if (!File.Exists(compiler) || !File.Exists(basePath)) throw new IOException("此安装包缺少词库编译组件，请更新完整版本。");
            var start = new ProcessStartInfo {
                FileName = compiler, Arguments = "--merge " + WorkerProcess.Quote(basePath) + " " + WorkerProcess.Quote(source) + " " + WorkerProcess.Quote(output),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = start })
            {
                lock (processGate) { if (disposed) throw new OperationCanceledException(); process.Start(); activeCompiler = process; }
                var stdout = System.Threading.Tasks.Task.Run(() => Drain(process.StandardOutput));
                var stderr = System.Threading.Tasks.Task.Run(() => Drain(process.StandardError));
                try
                {
                    if (!process.WaitForExit(60000)) { process.Kill(); process.WaitForExit(2000); return false; }
                    System.Threading.Tasks.Task.WaitAll(new[] { stdout, stderr }, 2000);
                    return process.ExitCode == 0;
                }
                finally { lock (processGate) activeCompiler = null; }
            }
        }
        private static void Drain(TextReader reader)
        { try { var buffer = new char[1024]; while (reader.Read(buffer, 0, buffer.Length) != 0) { } } catch (IOException) { } catch (ObjectDisposedException) { } }
        public void Dispose()
        {
            lock (processGate)
            {
                disposed = true;
                if (activeCompiler != null) try { if (!activeCompiler.HasExited) activeCompiler.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
