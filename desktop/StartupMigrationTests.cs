// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal static class StartupMigrationTests
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static bool Fails(Action action)
        { try { action(); return false; } catch (Exception error) when (SharedSettingsValues.Recoverable(error)) { return true; } }
        private static byte[] Bytes(string text) { return Utf8.GetBytes(text); }
        private static Dictionary<string, object> Config(string runtime, string worker, string model, string voice)
        { return new Dictionary<string, object> { { "python", runtime }, { "worker", worker }, { "llama_server", runtime }, { "translation_model", model }, { "voice_model_dir", voice }, { "device", "auto" }, { "gpu_layers", 99 }, { "threads", 4 } }; }
        internal static void Run(Action<bool, string> check)
        {
            var valid = new Dictionary<string, object> { { "Theme", 2 }, { "CandidateLayout", 1 }, { "FontSize", 20 }, { "Abbreviation", 1 },
                { "FuzzyMask", 255 }, { "ToolbarVisible", 0 }, { "Voice", "bf_emma" }, { "SpeedPercent", 75 }, { "ToolbarX", Int32.MinValue },
                { "ToolbarY", Int32.MaxValue }, { "UserLexiconPath", @"D:\测试 词库\a=b.mlex" }, { "LexiconRevision", 4 }, { "AutoRemember", 0 } };
            byte[] encoded = SharedSettingsValues.Serialize(valid); var decoded = SharedSettingsValues.Parse(encoded);
            check(decoded.Count == 13 && decoded.All(p => Object.Equals(valid[p.Key], p.Value)) && encoded[0] == (byte)'M', "shared-settings-strict-13-key-utf8-roundtrip");
            check(Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nAutoRemember=2\n"))) &&
                Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nAutoRemember=true\n"))) &&
                Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nAutoRemember=0\nAutoRemember=1\n"))), "shared-settings-auto-remember-rejects-invalid-and-duplicate-values");
            check(SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\r\n\r\nTheme=1\r\nToolbarX=-0\r\n")).Count == 2, "shared-settings-crlf-empty-lines-and-negative-zero");
            check(Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nTheme=1\nTheme=2\n"))) &&
                Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nChineseMode=0\n"))), "shared-settings-duplicate-and-mode-key-rejected");
            check(Fails(() => SharedSettingsValues.Parse(Bytes("\ufeffMansurNextSettings=1\n"))) && Fails(() => SharedSettingsValues.Parse(new byte[] { 0xff })) &&
                Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nVoice=af_heart\0\n"))), "shared-settings-bom-invalid-utf8-nul-rejected");
            check(Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nTheme=+1\n"))) && Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nTheme= 1\n"))) &&
                Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nFontSize=21\n"))) && Fails(() => SharedSettingsValues.Parse(Bytes("MansurNextSettings=1\nVoice=unknown\n"))), "shared-settings-literal-ranges-and-voice-enforced");
            check(!SharedSettingsValues.AbsolutePath(@"C:relative") && !SharedSettingsValues.AbsolutePath(@"\rootless") && !SharedSettingsValues.AbsolutePath(@"\\server") && !SharedSettingsValues.AbsolutePath(@"\\?\C:\file") && !SharedSettingsValues.AbsolutePath(@"\\.\pipe\name") &&
                SharedSettingsValues.AbsolutePath(@"C:\词库\file") && SharedSettingsValues.AbsolutePath(@"\\server\share\file"), "shared-settings-fully-qualified-paths-only");
            check(Fails(() => SharedSettingsValues.Parse(new byte[32769])), "shared-settings-size-bounded");
            string tempBase = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string temporary = Path.GetFullPath(Path.Combine(tempBase, "Mansur-migration-test-" + Guid.NewGuid().ToString("N")));
            if (!temporary.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid isolated path.");
            Directory.CreateDirectory(temporary);
            try
            {
                string shared = Path.Combine(temporary, "共享 目录"); Directory.CreateDirectory(shared);
                string preferences = Path.Combine(shared, "preferences.ini"); var adapter = new SharedSettingsValues(shared);
                adapter.Set("Theme", 1); adapter.Set("UserLexiconPath", @"D:\测试 词库\a=b.mlex");
                check((int)new SharedSettingsValues(shared).Get("Theme") == 1, "shared-settings-persistent-set");
                File.WriteAllBytes(preferences, SharedSettingsValues.Serialize(new Dictionary<string, object> { { "Theme", 2 } }));
                check((int)adapter.Get("Theme") == 2, "shared-settings-metadata-refresh");
                File.WriteAllText(preferences, "damaged", Utf8);
                check((int)adapter.Get("Theme") == 2 && new SharedSettingsValues(shared).Get("Theme") == null && Fails(() => adapter.Set("FontSize", 18)) && File.ReadAllText(preferences) == "damaged", "shared-settings-bad-file-cached-read-write-preserves-original");
                File.Delete(preferences); check(adapter.Get("Theme") == null, "shared-settings-missing-file-clears-cache");
                adapter.Set("Theme", 1); byte[] before = File.ReadAllBytes(preferences);
                using (var locked = new FileStream(preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
                    check(Fails(() => adapter.Set("Theme", 2)) && File.ReadAllBytes(preferences).SequenceEqual(before), "shared-settings-atomic-replace-failure-keeps-old-file");
                adapter.Set("FontSize", 12); adapter.Delete("FontSize");
                check(adapter.Get("FontSize") == null && (int)adapter.Get("Theme") == 1, "shared-settings-delete-keeps-other-values");
                adapter.Set("AutoRemember", 0);
                check(!new SettingsStore(new SharedSettingsValues(shared)).Read().AutoRemember, "shared-settings-auto-remember-off-survives-reopen");
                before = File.ReadAllBytes(preferences);
                using (var locked = new FileStream(preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
                    check(Fails(() => adapter.Set("AutoRemember", 1)) && File.ReadAllBytes(preferences).SequenceEqual(before), "shared-settings-auto-remember-replace-failure-preserves-off");
                File.WriteAllText(preferences, "MansurNextSettings=1\nAutoRemember=2\n", Utf8);
                check((int)adapter.Get("AutoRemember") == 0 && new SettingsStore(new SharedSettingsValues(shared)).Read().AutoRemember,
                    "shared-settings-auto-remember-corruption-preserves-cache-or-default");
                File.WriteAllBytes(preferences, before);
                string runtime = Path.Combine(temporary, "runtime.exe"), model = Path.Combine(temporary, "model.gguf"), model2 = Path.Combine(temporary, "retained-model.gguf"), worker = Path.Combine(temporary, "worker.py"), voice = Path.Combine(temporary, "voice");
                foreach (string path in new[] { runtime, model, model2, worker }) File.WriteAllBytes(path, new byte[] { 1 }); Directory.CreateDirectory(voice);
                string source = Path.Combine(temporary, "source.json"), snapshot = Path.Combine(temporary, "snapshot.json");
                File.WriteAllText(source, Json.Write(Config(runtime, runtime, model, voice)), new UTF8Encoding(true));
                File.WriteAllText(snapshot, Json.Write(new Dictionary<string, object> { { "Theme", 2 }, { "FontSize", 18 }, { "ChineseMode", 0 }, { "UserLexiconPath", @"C:\not-migrated.mlex" }, { "LexiconRevision", 3 } }), Utf8);
                string target = StartupMigration.PrepareAt(source, snapshot, shared, worker); var loaded = Configuration.Load(target);
                check(loaded.Worker == worker && loaded.TranslationModel == model && loaded.Device == "auto" && loaded.GpuLayers == 99, "migration-physical-source-models-and-package-worker");
                check((int)adapter.Get("Theme") == 1 && (int)adapter.Get("FontSize") == 18 && adapter.Get("ChineseMode") == null && adapter.Get("UserLexiconPath") == null && adapter.Get("LexiconRevision") == null, "migration-only-missing-preferences-no-mode-or-old-lexicon-pointer");
                File.WriteAllText(target, Json.Write(Config(runtime, runtime, model2, voice)), Utf8); before = File.ReadAllBytes(target);
                StartupMigration.PrepareAt(Path.Combine(temporary, "missing-source.json"), snapshot, shared, worker);
                check(Configuration.Load(target).TranslationModel == model2 && Directory.GetFiles(shared, "local-models.backup.*.json").Any(p => File.ReadAllBytes(p).SequenceEqual(before)), "migration-existing-shared-models-win-and-backup-retained");
                int backups = Directory.GetFiles(shared, "local-models.backup.*.json").Length;
                StartupMigration.PrepareAt(source, snapshot, shared, worker);
                check(Directory.GetFiles(shared, "local-models.backup.*.json").Length == backups, "migration-repeat-does-not-rewrite-identical-config");
                before = File.ReadAllBytes(target); byte[] priorPreferences = File.ReadAllBytes(preferences);
                File.WriteAllText(snapshot, "{\"FontSize\":\"18\"}", Utf8);
                check(Fails(() => StartupMigration.PrepareAt(source, snapshot, shared, worker)) && File.ReadAllBytes(target).SequenceEqual(before) && File.ReadAllBytes(preferences).SequenceEqual(priorPreferences), "migration-invalid-snapshot-leaves-existing-state");
                File.WriteAllText(snapshot, "{\"FontSize\":20}", Utf8);
                File.WriteAllText(target, Json.Write(Config(runtime, runtime, model2, voice)), Utf8); before = File.ReadAllBytes(target);
                using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                    check(Fails(() => StartupMigration.PrepareAt(source, snapshot, shared, worker)) && File.ReadAllBytes(target).SequenceEqual(before) && File.ReadAllBytes(preferences).SequenceEqual(priorPreferences), "migration-config-replace-failure-keeps-config-and-preferences");
                File.WriteAllText(snapshot, "{\"ToolbarVisible\":0}", Utf8);
                using (var locked = new FileStream(preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
                    check(Fails(() => StartupMigration.PrepareAt(source, snapshot, shared, worker)) && File.ReadAllBytes(target).SequenceEqual(before) && File.ReadAllBytes(preferences).SequenceEqual(priorPreferences), "migration-preference-publish-failure-restores-configuration");
                File.WriteAllText(target, "broken canonical config", Utf8);
                check(Fails(() => StartupMigration.PrepareAt(source, snapshot, shared, worker)) && File.ReadAllText(target) == "broken canonical config", "migration-invalid-existing-config-never-replaced-from-source");
                check(Fails(() => StartupMigration.PrepareAt(source, snapshot, shared, Path.Combine(temporary, "absent-worker.py"))), "migration-development-package-worker-missing-is-explicit");
                check(Directory.GetFiles(shared, "*.tmp").Length == 0, "migration-failed-staging-cleaned");
                before = File.ReadAllBytes(target);
                check(StartupMigration.PrepareAt(source, snapshot, shared, worker, false) == target && File.ReadAllBytes(target).SequenceEqual(before),
                    "settings-bootstrap-preserves-broken-config-for-gui-repair");
                string empty = Path.Combine(temporary, "first-install");
                string bootstrap = StartupMigration.PrepareAt(Path.Combine(temporary, "absent.json"), snapshot, empty, worker, false);
                check(Json.String(Json.Parse(File.ReadAllText(bootstrap)), "worker", 32760) == worker && Fails(() => Configuration.Load(bootstrap)),
                    "settings-bootstrap-without-models-publishes-editable-config-only");
                File.WriteAllText(bootstrap, Json.Write(Config(Path.Combine(temporary, "missing.exe"), runtime, model2, voice)), Utf8);
                StartupMigration.PrepareAt(source, snapshot, empty, worker, false);
                check(Json.String(Json.Parse(File.ReadAllText(bootstrap)), "python", 32760).EndsWith("missing.exe", StringComparison.Ordinal) &&
                    Json.String(Json.Parse(File.ReadAllText(bootstrap)), "worker", 32760) == worker,
                    "settings-bootstrap-keeps-missing-model-selections-for-repair");
                string damagedLegacy = Path.Combine(temporary, "bad-legacy.json"); File.WriteAllText(damagedLegacy, "not json", Utf8);
                string imported = StartupMigration.PrepareAt(damagedLegacy, snapshot, Path.Combine(temporary, "bad-legacy-shared"), worker, false);
                check(File.ReadAllText(imported) == "not json", "settings-bootstrap-retains-malformed-legacy-bytes");
                File.WriteAllBytes(bootstrap, new byte[65537]);
                check(StartupMigration.PrepareAt(source, snapshot, empty, worker, false) == bootstrap && new FileInfo(bootstrap).Length == 65537,
                    "settings-bootstrap-oversized-canonical-file-kept-for-gui-repair");
                string damagedPreferences = Path.Combine(empty, "preferences.ini"); File.WriteAllText(damagedPreferences, "bad preferences", Utf8);
                File.WriteAllText(bootstrap, "{}", Utf8);
                check(StartupMigration.PrepareAt(source, snapshot, empty, worker, false) == bootstrap && File.ReadAllText(damagedPreferences) == "bad preferences",
                    "settings-bootstrap-invalid-preferences-do-not-prevent-model-repair");
            }
            finally
            {
                // This exact test-created directory was resolved and verified beneath the temp root above.
                Directory.Delete(temporary, true);
            }
        }
    }
}
