// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal static class EnglishWritebackSettingsTests
    {
        private sealed class Values : ISettingsValues
        {
            internal readonly Dictionary<string, object> Data = new Dictionary<string, object>();
            internal string FailOnce;
            public object Get(string name) { object value; return Data.TryGetValue(name, out value) ? value : null; }
            public void Set(string name, object value)
            {
                if (FailOnce == name) { FailOnce = null; throw new IOException("fixed-writeback-settings-failure"); }
                Data[name] = value;
            }
            public void Delete(string name) { Data.Remove(name); }
        }
        private static bool Fails(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is IOException || error is FormatException || error is ArgumentException) { return true; }
        }
        private static bool Same(Dictionary<string, object> expected, Dictionary<string, object> actual)
        {
            if (expected.Count != actual.Count) return false;
            foreach (var entry in expected)
            {
                object value;
                if (!actual.TryGetValue(entry.Key, out value) || !Object.Equals(entry.Value, value)) return false;
            }
            return true;
        }
        internal static void Run(Action<bool, string> check)
        {
            var values = new Values(); var store = new SettingsStore(values);
            check(new Preferences().EnglishWritebackMode == 0 && store.Read().EnglishWritebackMode == 0 && values.Data.Count == 0,
                "english-writeback-default-replaces-and-reading-does-not-write");
            foreach (object invalid in new object[] { -1, 2, Int32.MaxValue, "1", true })
            {
                values.Data["EnglishWritebackMode"] = invalid;
                check(store.Read().EnglishWritebackMode == 0 && !SharedSettingsValues.Valid("EnglishWritebackMode", invalid),
                    "english-writeback-invalid-persistent-type-or-range-falls-back-" + invalid.GetType().Name + "-" + invalid);
            }
            values = new Values(); store = new SettingsStore(values);
            var preferences = store.Read(); preferences.EnglishWritebackMode = 2;
            check(Fails(() => store.SavePreferences(preferences)) && values.Data.Count == 0,
                "english-writeback-invalid-save-is-rejected-before-any-write");

            values.Data["UserLexiconPath"] = @"D:\fixed-test\personal.mlex";
            values.Data["LexiconRevision"] = 7; values.Data["ToolbarX"] = -1500; values.Data["ToolbarY"] = 200;
            values.Data["ChineseMode"] = 0;
            preferences = store.Read(); preferences.Theme = 2; preferences.Voice = "bf_emma";
            preferences.SpeedPercent = 85; preferences.AutoRemember = false; store.SavePreferences(preferences);
            var expected = new Dictionary<string, object>(values.Data);
            preferences.EnglishWritebackMode = 1; store.SavePreferences(preferences); expected["EnglishWritebackMode"] = 1;
            check(Same(expected, values.Data), "english-writeback-save-preserves-all-unrelated-preferences-and-live-state");
            preferences.EnglishWritebackMode = 0; preferences.Theme = 1; values.FailOnce = "Voice";
            check(Fails(() => store.SavePreferences(preferences)) && Same(expected, values.Data),
                "english-writeback-later-save-failure-restores-existing-mode-and-earlier-fields");
            values = new Values(); store = new SettingsStore(values); preferences = store.Read();
            preferences.EnglishWritebackMode = 1; values.FailOnce = "Voice";
            check(Fails(() => store.SavePreferences(preferences)) && values.Data.Count == 0,
                "english-writeback-later-save-failure-removes-new-mode-when-original-was-absent");
            values.FailOnce = "EnglishWritebackMode";
            check(Fails(() => store.SavePreferences(preferences)) && values.Data.Count == 0,
                "english-writeback-own-save-failure-rolls-back-earlier-settings");

            string tempBase = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string root = Path.GetFullPath(Path.Combine(tempBase, "Mansur-writeback-settings-test-" + Guid.NewGuid().ToString("N")));
            if (!root.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid isolated test directory.");
            Directory.CreateDirectory(root);
            try
            {
                var isolated = new SharedSettingsValues(root);
                isolated.Set("Theme", 2); isolated.Set("Voice", "bf_emma");
                string ordinaryFile = Path.Combine(root, "preferences.ini");
                string ordinaryBytes = Convert.ToBase64String(File.ReadAllBytes(ordinaryFile));
                isolated.Set("EnglishWritebackMode", 1);
                var ordinary = SharedSettingsValues.Parse(File.ReadAllBytes(ordinaryFile));
                check(Convert.ToBase64String(File.ReadAllBytes(ordinaryFile)) == ordinaryBytes &&
                    !ordinary.ContainsKey("EnglishWritebackMode") && Object.Equals(ordinary["Theme"], 2) && Object.Equals(ordinary["Voice"], "bf_emma"),
                    "english-writeback-uses-separate-file-without-modifying-native-preference-bytes");
                isolated.Delete("EnglishWritebackMode");
                check(Convert.ToBase64String(File.ReadAllBytes(ordinaryFile)) == ordinaryBytes &&
                    new SettingsStore(new SharedSettingsValues(root)).Read().EnglishWritebackMode == 0,
                    "english-writeback-delete-only-touches-learning-file-and-restores-default");
                isolated.ImportMissing(new Dictionary<string, object> { { "Theme", 1 }, { "FontSize", 16 }, { "EnglishWritebackMode", 1 } });
                ordinary = SharedSettingsValues.Parse(File.ReadAllBytes(ordinaryFile));
                check(!ordinary.ContainsKey("EnglishWritebackMode") && Object.Equals(ordinary["Theme"], 2) &&
                    Object.Equals(ordinary["FontSize"], 16) && Object.Equals(new SharedSettingsValues(root).Get("EnglishWritebackMode"), 1),
                    "english-writeback-mixed-import-routes-keys-and-preserves-existing-preferences");
                check(Fails(() => SharedSettingsValues.Parse(Encoding.UTF8.GetBytes("MansurNextSettings=1\nEnglishWritebackMode=1\n"))) &&
                    Fails(() => SharedSettingsValues.Serialize(new Dictionary<string, object> { { "EnglishWritebackMode", 1 } })),
                    "english-writeback-key-is-never-part-of-native-preferences-protocol");
                foreach (int mode in new[] { 0, 1 })
                {
                    store = new SettingsStore(new SharedSettingsValues(root)); preferences = store.Read();
                    preferences.EnglishWritebackMode = mode; store.SavePreferences(preferences);
                    check(new SettingsStore(new SharedSettingsValues(root)).Read().EnglishWritebackMode == mode,
                        "english-writeback-shared-file-persists-and-reopens-mode-" + mode);
                }
                var cachedValues = new SharedSettingsValues(root); store = new SettingsStore(cachedValues);
                check(store.Read().EnglishWritebackMode == 1, "english-writeback-valid-shared-snapshot-is-readable");
                string filename = Path.Combine(root, "learning-preferences.ini");
                File.WriteAllText(filename, "MansurNextLearningSettings=1\nEnglishWritebackMode=99\n", new UTF8Encoding(false));
                check(store.Read().EnglishWritebackMode == 1 && new SettingsStore(new SharedSettingsValues(root)).Read().EnglishWritebackMode == 0,
                    "english-writeback-invalid-shared-snapshot-retains-cache-and-fresh-reader-defaults");
                check(Fails(() => cachedValues.Set("EnglishWritebackMode", 0)),
                    "english-writeback-invalid-shared-file-is-not-silently-overwritten");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
