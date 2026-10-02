// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
namespace Mansur.Next.Desktop
{
    internal static class LearningEnhancementTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var runtime = new RuntimeStatus(42); runtime.Ready("none", "not_started", false);
            var status = Json.Parse(runtime.Snapshot());
            check(status["model_ready"].Equals(true) && status["voice_ready"].Equals(false) && status["worker_state"].Equals("degraded"), "speech-failure-does-not-label-translation-backend-unavailable");
            runtime.Accepted(1); runtime.TranslationPartial(3);
            check(Json.Parse(runtime.Snapshot())["stage"].Equals("translation_partial"), "streaming-runtime-distinguishes-preview-from-complete-generation");
            string root = Path.Combine(Path.GetTempPath(), "MansurLearningOptions-" + Guid.NewGuid().ToString("N"));
            try {
                var store = new SettingsStore(new SharedSettingsValues(root));
                check(!store.Read().EnglishSuggestions && !store.Read().SystemSpeechFallback, "learning-enhancements-default-to-explicit-opt-in");
                var preferences = store.Read(); preferences.EnglishSuggestions = preferences.SystemSpeechFallback = true; store.SavePreferences(preferences);
                var read = new SettingsStore(new SharedSettingsValues(root)).Read();
                check(read.EnglishSuggestions && read.SystemSpeechFallback, "learning-enhancements-survive-settings-reopen");
                string native = File.ReadAllText(Path.Combine(root, "preferences.ini"));
                check(!native.Contains("EnglishSuggestions") && !native.Contains("SystemSpeechFallback"), "learning-enhancements-preserve-older-native-settings-readers");
                preferences.EnglishSuggestions = preferences.SystemSpeechFallback = false; store.SavePreferences(preferences);
                read = new SettingsStore(new SharedSettingsValues(root)).Read();
                check(!read.EnglishSuggestions && !read.SystemSpeechFallback, "learning-enhancements-can-be-turned-off-independently");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
