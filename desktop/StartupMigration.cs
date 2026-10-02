// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal static class StartupMigration
    {
        internal static string Prepare(string sourcePhysicalConfig, string preferencesSnapshotPath)
        {
            string worker = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "learning", "worker.py"));
            return PrepareAt(sourcePhysicalConfig, preferencesSnapshotPath, UserStateDirectory.Root, worker);
        }
        internal static string PrepareSettings(string sourcePhysicalConfig, string preferencesSnapshotPath)
        {
            string worker = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "learning", "worker.py"));
            return PrepareAt(sourcePhysicalConfig, preferencesSnapshotPath, UserStateDirectory.Root, worker, false);
        }
        // Isolated tests provide only temporary paths. The public entry always uses this package's worker.
        internal static string PrepareAt(string sourcePhysicalConfig, string preferencesSnapshotPath, string directory, string worker, bool requireModels = true)
        {
            if (!SharedSettingsValues.AbsolutePath(directory) || !SharedSettingsValues.AbsolutePath(worker) || !File.Exists(worker))
                throw new FormatException("migration_package_worker_missing");
            var additions = Snapshot(preferencesSnapshotPath);
            string target = Path.Combine(Path.GetFullPath(directory), "local-models.json");
            bool existed = File.Exists(target);
            string source = existed ? target : sourcePhysicalConfig;
            if (!SharedSettingsValues.AbsolutePath(source)) throw new FormatException("migration_source_config_missing");
            byte[] original;
            try { original = !requireModels && !File.Exists(source) ? new UTF8Encoding(false).GetBytes("{}") : SharedSettingsValues.ReadBounded(source, 65536); }
            catch (Exception error) when (!requireModels && SharedSettingsValues.Recoverable(error))
            {
                if (existed)
                {
                    // Oversized or unreadable canonical files remain exactly where they are.
                    // The GUI can show a repairable draft without loading them into memory.
                    TryImportForSettings(directory, additions);
                    return target;
                }
                // Preserve an unusable legacy source in its original location. Bootstrap only
                // this package's worker path; do not copy an unbounded file into shared state.
                original = new UTF8Encoding(false).GetBytes("{}");
            }
            byte[] updated;
            try
            {
                var configuration = Json.Parse(Utf8Json(original), 65536); configuration["worker"] = Path.GetFullPath(worker);
                updated = new UTF8Encoding(false, true).GetBytes(Json.Write(configuration));
            }
            catch (Exception error) when (!requireModels && (error is FormatException || error is ArgumentException || error is InvalidOperationException))
            {
                // Keep malformed settings verbatim for repair in the GUI. Opening settings
                // must not silently replace a user's configuration with guessed defaults.
                updated = original;
            }
            if (updated.Length > 65536) throw new FormatException("migration_config_too_large");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".local-models.migration." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                SharedSettingsValues.WriteNew(temporary, updated);
                if (requireModels) Configuration.Load(temporary); // No processes are started by configuration validation.
                // A valid shared configuration keeps every model selection; only this package's worker changes.
                if (existed && !Equal(original, SharedSettingsValues.ReadBounded(target, 65536))) throw new IOException("migration_config_changed");
                bool published = !existed || !Equal(original, updated);
                string backup = Path.Combine(directory, "local-models.backup." + Guid.NewGuid().ToString("N") + ".json");
                if (published)
                {
                    if (existed) File.Replace(temporary, target, backup); else File.Move(temporary, target);
                }
                // One atomic preference snapshot, adding only absent keys; existing values are never rewritten.
                try { new SharedSettingsValues(directory).ImportMissing(additions); }
                catch (Exception error) when (SharedSettingsValues.Recoverable(error))
                {
                    if (!requireModels) return target; // Keep damaged preferences intact; allow the model settings UI to start.
                    if (published)
                    {
                        try
                        {
                            // Do not undo another writer's newer configuration while recovering this attempt.
                            if (!Equal(updated, SharedSettingsValues.ReadBounded(target, 65536))) throw new IOException("migration_rollback_conflict");
                            if (existed) File.Replace(backup, target, Path.Combine(directory, "local-models.failed." + Guid.NewGuid().ToString("N") + ".json"));
                            else File.Delete(target);
                        }
                        catch (Exception rollback) when (SharedSettingsValues.Recoverable(rollback)) { throw new IOException("migration_rollback_failed"); }
                    }
                    throw;
                }
                return target;
            }
            finally { SharedSettingsValues.TryDelete(temporary); }
        }
        private static bool Equal(byte[] a, byte[] b)
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        private static void TryImportForSettings(string directory, Dictionary<string, object> additions)
        {
            try { new SharedSettingsValues(directory).ImportMissing(additions); }
            catch (Exception error) when (SharedSettingsValues.Recoverable(error)) { }
        }
        private static string Utf8Json(byte[] bytes)
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            return text.StartsWith("\ufeff", StringComparison.Ordinal) ? text.Substring(1) : text;
        }
        private static Dictionary<string, object> Snapshot(string path)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (String.IsNullOrEmpty(path)) return result;
            if (!SharedSettingsValues.AbsolutePath(path)) throw new FormatException("migration_snapshot_path_invalid");
            var source = Json.Parse(Utf8Json(SharedSettingsValues.ReadBounded(path, 32768)), 32768);
            foreach (string name in SharedSettingsValues.Names)
            {
                // Old dictionary pointers may refer to a package-private filesystem view. Leave those files intact.
                if (name == "UserLexiconPath" || name == "LexiconRevision") continue;
                object value; if (!source.TryGetValue(name, out value)) continue;
                if (!SharedSettingsValues.Valid(name, value)) throw new FormatException("invalid_preference_snapshot");
                result.Add(name, value);
            }
            return result;
        }
    }
}
