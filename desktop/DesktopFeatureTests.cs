// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal static class DesktopFeatureTests
    {
        private sealed class MemoryValues : ISettingsValues
        {
            internal readonly Dictionary<string, object> Data = new Dictionary<string, object>();
            internal string FailOnce;
            public object Get(string name) { object value; return Data.TryGetValue(name, out value) ? value : null; }
            public void Set(string name, object value) { if (FailOnce == name) { FailOnce = null; throw new IOException("fixed test failure"); } Data[name] = value; }
            public void Delete(string name) { Data.Remove(name); }
        }
        private static bool Fails(Action action)
        { try { action(); return false; } catch (Exception error) when (error is IOException || error is FormatException || error is ArgumentException) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            var values = new MemoryValues(); var store = new SettingsStore(values); var p = store.Read();
            check(p.Theme == 0 && p.CandidateLayout == 0 && p.FontSize == 12 && p.Abbreviation && p.AutoRemember && p.ToolbarVisible && p.FuzzyMask == 0 && p.Voice == "af_heart" && p.SpeedPercent == 100, "settings-default-contract");
            p.Theme = 2; p.CandidateLayout = 1; p.FontSize = 20; p.FuzzyMask = 255; p.Voice = "bf_emma"; p.SpeedPercent = 85; p.Abbreviation = false; p.AutoRemember = false; p.ToolbarVisible = false;
            store.SavePreferences(p); var reloaded = new SettingsStore(values).Read();
            check(reloaded.Theme == 2 && reloaded.CandidateLayout == 1 && reloaded.FontSize == 20 && !reloaded.Abbreviation && !reloaded.AutoRemember && reloaded.FuzzyMask == 255 && reloaded.Voice == "bf_emma" && reloaded.SpeedPercent == 85 && !reloaded.ToolbarVisible, "settings-persist-all-dialog-fields");
            values.Data["ChineseMode"] = 0; store.SetToolbarPosition(new Point(-1700, 300)); store.SavePreferences(p);
            check(Object.Equals(values.Data["ChineseMode"], 0) && store.Read().ToolbarX == -1700 && store.Read().ToolbarY == 300, "dialog-preserves-legacy-mode-value-without-using-it");
            values.Data["FontSize"] = 300; values.Data["Voice"] = "unknown"; values.Data["SpeedPercent"] = -1;
            check(store.Read().FontSize == 12 && store.Read().Voice == "af_heart" && store.Read().SpeedPercent == 100, "invalid-persistent-values-fall-back");
            p = store.Read(); values.FailOnce = "FontSize"; p.Theme = 1;
            check(Fails(() => store.SavePreferences(p)) && store.Read().Theme == 2, "settings-partial-write-rolls-back");
            values.Data["UserLexiconPath"] = @"D:\existing\merged.mlex"; values.Data["LexiconRevision"] = 5;
            p = store.Read(); p.AutoRemember = false; store.SavePreferences(p);
            check(!store.Read().AutoRemember && store.Read().UserLexiconPath == @"D:\existing\merged.mlex" && store.Read().LexiconRevision == 5,
                "auto-remember-disable-keeps-existing-personal-settings");
            p.AutoRemember = true; values.FailOnce = "Voice";
            check(Fails(() => store.SavePreferences(p)) && !store.Read().AutoRemember, "auto-remember-later-save-failure-rolls-back-toggle");
            values.Data["AutoRemember"] = 2;
            check(store.Read().AutoRemember, "auto-remember-invalid-persistent-value-uses-enabled-default");
            check(ControlChannel.Valid("shutdown") && ControlChannel.Valid("show-settings") && !ControlChannel.Valid("learn") && !ControlChannel.Valid("shutdown\nextra"), "separate-control-command-allowlist");
            var currentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            check(ControlChannel.Name(currentSid).EndsWith(".Session." + System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                ControlChannel.Name(currentSid, 1) != ControlChannel.Name(currentSid, 2), "control-pipe-is-isolated-to-user-and-windows-session");
            Rectangle negative = new Rectangle(-1920, -200, 1920, 1080);
            check(negative.Contains(ToolbarLayout.Clamp(new Point(-9000, 9000), new Size(244, 40), negative)), "toolbar-negative-monitor-clamp");
            check(ToolbarLayout.Clamp(new Point(100, 100), new Size(244, 40), new Rectangle(0, 0, 100, 20)) == new Rectangle(0, 0, 100, 20), "toolbar-tiny-monitor-clamp");
            var areas = ToolbarLayout.Areas(new[] { new Size(28, 30), new Size(56, 30), new Size(56, 30), new Size(28, 30) }, 20, 48, 70);
            check(ToolbarLayout.Hit(new Point(10, 10), areas) == -1 && Enumerable.Range(0, 4).All(i => ToolbarLayout.Hit(new Point(areas[i].Left + 1, 20), areas) == i), "toolbar-measured-paint-and-hit-zones-agree");
            check(areas[1].Width == 96 && areas[2].Left == areas[1].Right && areas[3].Height == 70, "toolbar-large-font-dpi-layout-has-measured-padding");
            var tsv = LexiconParser.Parse("# format\nnihao\t你好\t10000\tni hao\nnihao\t你好\t9000\tni hao\nwrong\n\t缺拼音\t10\n", false);
            check(tsv.Entries.Count == 2 && tsv.Rejected == 2 && tsv.MissingPinyin == 1, "tsv-reports-valid-rejected-and-missing-pinyin");
            var merged = LexiconParser.Merge(new LexiconEntry[0], tsv.Entries);
            check(merged.Count == 1 && merged[0].Frequency == 10000 && merged[0].Syllables == "ni hao", "lexicon-dedup-retains-maximum-weight");
            var rime = LexiconParser.Parse("---\nname: demo\nimport_tables:\n  - ignored\n...\n你好\tni hao\t75%\n学习\txue xi\n缺拼音\n", true);
            check(rime.Entries.Count == 2 && rime.Entries[0].Pinyin == "nihao" && rime.Entries[0].Frequency == 7500 && rime.Rejected == 1 && rime.MissingPinyin == 1, "rime-default-data-weights-and-missing-code");
            var custom = LexiconParser.Parse("---\ncolumns: [code, text, weight]\n...\nni hao\t你好\t100\n", true);
            check(custom.Entries.Count == 1 && custom.Entries[0].Text == "你好", "rime-inline-columns-reorder");
            custom = LexiconParser.Parse("---\ncolumns:\n  - text\n  - weight\n  - code\n...\n你好\t100\tni hao\n", true);
            check(custom.Entries.Count == 1 && custom.Entries[0].Pinyin == "nihao", "rime-block-columns-reorder");
            var inert = LexiconParser.Parse("---\nname: !!python/object/apply:ignored\nimport_tables: [no-load]\n...\n你好\tni hao\n", true);
            check(inert.Entries.Count == 1, "rime-header-tags-imports-are-inert-text");
            check(Fails(() => LexiconParser.Parse("你好\tni hao\n", true)), "rime-missing-data-delimiter-rejected");
            var invalid = LexiconParser.Parse("nihao\t你好\tNaN\tni hao\nnihao\t你好\t10\tni ma\nni3hao3\t你好\t10\nnihao\t你好\t-1\n", false);
            check(invalid.Entries.Count == 0 && invalid.Rejected == 4, "tsv-invalid-frequency-code-and-syllables-rejected");
            check(PinyinSyllables.All.Count == 418 && LexiconParser.Parse("abcdef\t坏音节\t100\tabc def\n", false).Rejected == 1, "illegal-explicit-syllables-rejected-before-compiler");
            check(LexiconParser.Parse("---\n...\n你好\tnihao\t100\n", true).Entries.Single().Syllables.Length == 0, "compact-rime-code-leaves-syllable-inference-to-compiler");
            check(LexiconParser.Parse(LexiconParser.Serialize(merged), false).Entries.Single().Row == merged.Single().Row, "canonical-tsv-roundtrip");
            string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Mansur-desktop-test-" + Guid.NewGuid().ToString("N")));
            if (!tempRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
            Directory.CreateDirectory(tempRoot);
            try
            {
                values = new MemoryValues(); store = new SettingsStore(values);
                string source = Path.Combine(tempRoot, "测试词库.tsv"); File.WriteAllText(source, "nihao\t你好\t10000\tni hao\n", new UTF8Encoding(false));
                string lexiconRoot = Path.Combine(tempRoot, "词库");
                Func<string, string, string, bool> succeed = (a, b, output) => { File.WriteAllBytes(output, new byte[32]); return true; };
                using (var manager = new LexiconManager(store, lexiconRoot, tempRoot, succeed))
                {
                    var result = manager.Import(new[] { source }); string original = store.Read().UserLexiconPath;
                    check(result.Accepted == 1 && result.Added == 1 && result.Total == 1 && File.Exists(original) && store.Read().LexiconRevision == 1, "lexicon-success-publishes-wide-path-and-revision");
                    string export = Path.Combine(tempRoot, "导出.tsv"); manager.Export(export);
                    check(LexiconParser.ReadFile(export).Entries.Count == 1, "user-lexicon-export-readable-canonical-tsv");
                    result = manager.Import(new[] { source });
                    check(result.Added == 0 && result.Total == 1 && File.Exists(original) && store.Read().UserLexiconPath != original && store.Read().LexiconRevision == 2, "reimport-deduplicates-and-preserves-old-version");
                    string current = store.Read().UserLexiconPath;
                    using (var failed = new LexiconManager(store, lexiconRoot, tempRoot, (a, b, c) => false))
                        check(Fails(() => failed.Import(new[] { source })) && store.Read().UserLexiconPath == current && store.Read().LexiconRevision == 2, "compiler-failure-preserves-active-lexicon");
                    values.FailOnce = "LexiconRevision";
                    check(Fails(() => manager.Import(new[] { source })) && store.Read().UserLexiconPath == current && store.Read().LexiconRevision == 2, "lexicon-publish-failure-restores-old-pointer");
                    File.WriteAllBytes(source, new byte[] { 0xff, 0xfe, 0x80 });
                    check(Fails(() => manager.Import(new[] { source })) && store.Read().UserLexiconPath == current, "invalid-utf8-import-preserves-active-lexicon");
                    File.WriteAllText(source, "broken row", new UTF8Encoding(false));
                    check(Fails(() => manager.Import(new[] { source })) && store.Read().LexiconRevision == 2, "all-invalid-import-does-not-publish");
                }
            }
            finally { Directory.Delete(tempRoot, true); }
        }
    }
}
