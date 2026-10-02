// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Mansur.Next.Desktop
{
    internal static class SelfTests
    {
        private static readonly List<string> passed = new List<string>();
        private static void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); passed.Add(label); }
        private static Dictionary<string, object> Message(string context, long ticks, long revision = 1,
            long sequence = 1, string sender = "source", string operation = "learn", string text = "你好")
        {
            return new Dictionary<string, object> { { "op", operation }, { "text", text }, { "context", context },
                { "revision", revision }, { "sender", sender }, { "sent_ticks", ticks }, { "sent_sequence", sequence } };
        }
        private static LearningRequest Request(string context, long ticks, long revision = 1,
            long sequence = 1, string sender = "source", string operation = "learn", string text = "你好")
        { return LearningRequest.Parse(Json.Write(Message(context, ticks, revision, sequence, sender, operation, text))); }
        private static bool Reject(Action action)
        { try { action(); return false; } catch (Exception e) when (e is FormatException || e is ArgumentException || e is DecoderFallbackException) { return true; } }
        internal static int Run()
        {
            try
            {
                var tracker = new RequestTracker();
                var a = Request("a", 100, 3); Check(tracker.Accept(a), "accept-first");
                Check(!tracker.Accept(Request("a", 101, 2)), "reject-older-revision");
                Check(!tracker.Accept(Request("a", 101, 3)), "reject-duplicate-revision");
                var b = Request("b", 200); Check(tracker.Accept(b) && b.Id > a.Id, "monotonic-id");
                Check(!tracker.IsCurrent(a.Id) && tracker.IsCurrent(b.Id), "drop-stale-worker-event");
                Check(!tracker.Accept(Request("c", 150)) && tracker.IsCurrent(b.Id), "late-cross-context-learn-cannot-replace-newer");
                Check(!tracker.Cancel(Request("a", 500, operation: "cancel")) && tracker.IsCurrent(b.Id), "other-context-cancel-does-not-stop-current");
                var c = Request("c", 300); Check(tracker.Accept(c), "other-context-cancel-does-not-advance-global-watermark");
                Check(!tracker.Accept(Request("a", 400, 4)), "noncurrent-cancel-tombstone-rejects-late-learn");
                Check(!tracker.Cancel(Request("c", 250, operation: "cancel")) && tracker.IsCurrent(c.Id), "old-cancel-cannot-stop-new-learn");
                Check(tracker.Cancel(Request("c", 350, operation: "cancel")) && !tracker.IsCurrent(c.Id), "matching-new-cancel-invalidates-worker-events");
                Check(!tracker.Accept(Request("c", 340, 2)), "cancel-before-delayed-next-revision-blocks-it");
                Check(!tracker.Accept(Request("a", 600, 2)), "revision-history-across-focus");
                var d = Request("a", 600, 4); Check(tracker.Accept(d) && d.Id > c.Id, "newer-than-cancel-resumes-with-new-id");
                Check(!tracker.Accept(Request("a", 700, 5, sender: "impostor")), "context-is-bound-to-original-sender");
                var firstCancel = new RequestTracker();
                Check(!firstCancel.Cancel(Request("unseen", 100, operation: "cancel")) &&
                    !firstCancel.Accept(Request("unseen", 90)) && firstCancel.Accept(Request("other", 50)), "cancel-before-any-learn-is-context-local-tombstone");
                var equal = new RequestTracker();
                var sameTick = Request("a", 100, sequence: 3);
                Check(equal.Accept(sameTick) && !equal.Accept(Request("b", 100, sequence: 2)), "same-tick-old-sender-sequence-rejected");
                var sameTickNew = Request("b", 100, sequence: 4);
                Check(equal.Accept(sameTickNew) && !equal.Accept(Request("c", 100, sequence: 9, sender: "other")), "same-tick-same-sender-order-and-cross-sender-first-wins");
                Check(!equal.Cancel(Request("b", 100, sequence: 3, operation: "cancel")) && equal.IsCurrent(sameTickNew.Id), "same-tick-old-cancel-ignored");
                Check(equal.Cancel(Request("b", 100, sequence: 4, operation: "cancel")), "equal-stamp-cancel-wins-over-matching-learn");
                Check(!equal.Accept(Request("b", 100, 2, 4)) && equal.Accept(Request("b", 100, 2, 5)), "strictly-after-equal-tick-cancel-can-resume");
                equal.CancelCurrent(200);
                Check(equal.Current == null && !equal.Accept(Request("b", 150, 3)), "tray-cancel-also-blocks-delayed-current-context-learn");
                var bounded = new RequestTracker();
                for (int i = 0; i < RequestTracker.HistoryLimit; i++) bounded.Cancel(Request("c" + i, 100, operation: "cancel"));
                Check(bounded.HistoryCount == RequestTracker.HistoryLimit && !bounded.Accept(Request("overflow", 300)), "history-and-cancel-tombstones-have-fixed-memory-bound");
                Check(bounded.Accept(Request("c0", 200)) && !bounded.Accept(Request("c1", 90)), "full-history-retains-existing-context-and-tombstone");
                var invalid = Message("a", 1); invalid["revision"] = 1.5;
                Check(Reject(() => LearningRequest.Parse(Json.Write(invalid))), "reject-fractional-revision");
                invalid = Message("a", 0);
                Check(Reject(() => LearningRequest.Parse(Json.Write(invalid))), "reject-zero-event-clock");
                invalid = Message("a", 1); invalid.Remove("sent_sequence");
                Check(Reject(() => LearningRequest.Parse(Json.Write(invalid))), "reject-missing-event-sequence");
                invalid = Message("a", 1); invalid["sender"] = "bad\nidentity";
                Check(Reject(() => LearningRequest.Parse(Json.Write(invalid))), "reject-control-character-sender");
                var shortText = Request("length", 700, text: new string('你', 256)); shortText.Voice = "af_heart"; shortText.Speed = 1;
                Check(shortText.CanLearn && shortText.ScalarCount == 256 && Json.Parse(Json.Write(shortText.WorkerCommand()))["op"].Equals("learn"), "256-scalars-reach-worker");
                var longText = Request("length", 800, 2, text: new string('你', 257));
                Check(tracker.Accept(shortText) && tracker.Accept(longText) && !tracker.IsCurrent(shortText.Id), "257-scalars-are-accepted-and-invalidate-earlier-events");
                var longCommand = Json.Parse(Json.Write(longText.WorkerCommand()));
                Check(!longText.CanLearn && longCommand.Count == 1 && longCommand["op"].Equals("cancel") && LearningRequest.LengthNotice == "文字已提交，本次伴读最多256字", "long-text-cancels-worker-and-has-explicit-notice-without-forwarding-text");
                Check(!tracker.Accept(Request("late-short", 750)), "long-text-advances-order-watermark");
                Check(Request("max", 1, text: new string('你', 2048)).ScalarCount == 2048, "core-2048-utf16-draft-is-accepted");
                Check(Reject(() => Request("oversize", 1, text: new string('你', 2049))), "core-2049-utf16-draft-is-rejected");
                string supplementary = String.Concat(Enumerable.Repeat(Char.ConvertFromUtf32(0x20000), 256));
                Check(Request("surrogate", 1, text: supplementary).CanLearn && !Request("surrogate", 1, text: supplementary + "你").CanLearn, "supplementary-han-counts-as-one-scalar");
                Check(Reject(() => Request("invalid", 1, text: "你\0好")), "reject-nul-in-draft");
                var originalEnglish = Request("english", 1, text: " Hello,  Mansur! "); originalEnglish.Voice = "af_heart"; originalEnglish.Speed = 1;
                Check(originalEnglish.Text == " Hello,  Mansur! " && Json.Parse(Json.Write(originalEnglish.WorkerCommand()))["text"].Equals(originalEnglish.Text), "english-case-and-spacing-reach-worker-unchanged");
                Check(Reject(() => Request("empty", 1, text: "   \t")), "reject-whitespace-only-draft");
                invalid = Message("a", 1); invalid["voice"] = "unknown";
                Check(Reject(() => LearningRequest.Parse(Json.Write(invalid))), "reject-unknown-voice");
                invalid = Message("a", 1); invalid["speed"] = 5.0;
                Check(Reject(() => LearningRequest.Parse(Json.Write(invalid))), "reject-invalid-speed");
                var anchored = Message("a", 1); anchored["anchor"] = new { left = 10, top = 20, right = 10, bottom = 35 };
                var parsed = LearningRequest.Parse(Json.Write(anchored));
                Check(parsed.Anchor.HasValue && parsed.Anchor.Value.Width == 0, "zero-width-caret-valid");
                Rectangle work = new Rectangle(-1920, 0, 1920, 1080);
                Rectangle bottom = Positioning.Place(new Rectangle(-10, 1060, 0, 20), work, new Size(460, 160));
                Check(work.Contains(bottom) && bottom.Bottom <= 1060, "anchor-above-bottom-edge");
                Rectangle fallback = Positioning.Place(null, work, new Size(460, 160));
                Check(work.Contains(fallback) && fallback.X < 0, "negative-monitor-fallback");
                Check(Positioning.Place(null, new Rectangle(0, 0, 80, 60), new Size(460, 160)) == new Rectangle(0, 0, 80, 60), "tiny-workarea-clamp");
                Check(Positioning.Place(new Rectangle(50000, 50000, 1, 20), work, new Size(460, 160)) == fallback, "invalid-anchor-fallback");
                byte[] wave = PcmWave.Make(new byte[] { 0, 0, 255, 127, 0, 128 }, 24000);
                Check(wave.Length == 50 && Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wave, 8, 8) == "WAVEfmt ", "wave-header");
                Check(BitConverter.ToInt32(wave, 24) == 24000 && BitConverter.ToInt16(wave, 22) == 1 && BitConverter.ToInt16(wave, 34) == 16, "wave-mono-pcm16-format");
                Check(BitConverter.ToInt32(wave, 40) == 6 && wave.Skip(44).SequenceEqual(new byte[] { 0, 0, 255, 127, 0, 128 }), "wave-payload-unchanged");
                Check(Reject(() => PcmWave.Make(new byte[3], 24000)), "reject-odd-pcm");
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"text\":\"你好\"}\r\nignored")))
                    Check(PipeListener.ReadLine(stream, 65536, 1000) == "{\"text\":\"你好\"}", "pipe-utf8-one-line");
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 65537) + "\n")))
                    Check(Reject(() => PipeListener.ReadLine(stream, 65536, 1000)), "pipe-size-limit");
                using (var stream = new MemoryStream(new byte[] { 0xff, 10 }))
                    Check(Reject(() => PipeListener.ReadLine(stream, 65536, 1000)), "pipe-invalid-utf8");
                Check(WorkerProcess.ReadBounded(new StringReader("{\"event\":\"ready\"}\n"), 100) == "{\"event\":\"ready\"}", "worker-line-read");
                Check(Reject(() => WorkerProcess.ReadBounded(new StringReader("truncated"), 100)), "worker-truncated-line-rejected");
                Check(WorkerProcess.Quote("C:\\model dir\\") == "\"C:\\model dir\\\\\"", "windows-argument-trailing-backslash");
                Check(WorkerProcess.Quote("a\"b") == "\"a\\\"b\"", "windows-argument-quote");
                var sid = WindowsIdentity.GetCurrent().User;
                var rules = PipeListener.Security(sid).GetAccessRules(true, false, typeof(SecurityIdentifier));
                Check(rules.Count == 2 && rules.Cast<PipeAccessRule>().Any(r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow) &&
                    rules.Cast<PipeAccessRule>().Any(r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) && r.AccessControlType == AccessControlType.Deny), "pipe-only-user-deny-network");
                Check((FloatingForm.NoActivate | FloatingForm.ToolWindow) == 0x08000080, "nonactivating-toolwindow-style");
                var runtime = new RuntimeStatus(12345);
                var startup = Json.Parse(runtime.Snapshot());
                Check(startup["stage"].Equals("startup") && startup["pid"].Equals(12345) && startup["request_id"] == null, "runtime-startup-has-pid-without-request-body");
                runtime.Ready("Vulkan1");
                var ready = Json.Parse(runtime.Snapshot());
                Check(ready["stage"].Equals("ready") && ready["device"].Equals("Vulkan1") && ready["model_ready"].Equals(true), "runtime-ready-retains-actual-approved-device");
                runtime.Ready("none", "not_started");
                var voiceOnly = Json.Parse(runtime.Snapshot());
                Check(voiceOnly["voice_ready"].Equals(true) && voiceOnly["translation_state"].Equals("not_started") && voiceOnly["device"].Equals("none"), "voice-ready-does-not-claim-translator-loaded");
                runtime.Models("Vulkan0", "ready");
                Check(Json.Parse(runtime.Snapshot())["translation_state"].Equals("ready") && Json.Parse(runtime.Snapshot())["device"].Equals("Vulkan0"), "first-translation-updates-actual-runtime-device");
                runtime.Accepted(17); runtime.Translation(41); runtime.AudioQueued(2, 48000);
                var queued = Json.Parse(runtime.Snapshot());
                Check(queued["stage"].Equals("audio_queued") && queued["request_id"].Equals(17) && queued["english_characters"].Equals(41) &&
                    queued["audio_queued_packets"].Equals(2) && queued["audio_queued_bytes"].Equals(48000), "runtime-audio-status-is-queue-evidence-not-heard-evidence");
                runtime.Done(); Check(Json.Parse(runtime.Snapshot())["stage"].Equals("done"), "runtime-completion-stage");
                runtime.Window(new FloatingPresentation { Visible = true, EnglishVisible = true, Topmost = true, OnScreen = true });
                var displayed = Json.Parse(runtime.Snapshot()); long displayedVersion = runtime.Version;
                Check(displayed["window_visible"].Equals(true) && displayed["window_english_visible"].Equals(true) && displayed["window_topmost"].Equals(true) &&
                    displayed["window_on_screen"].Equals(true) && displayed["window_cloaked"].Equals(false) && displayed["stage"].Equals("done"), "runtime-window-state-is-distinct-from-generation");
                runtime.Window(new FloatingPresentation { Visible = true, EnglishVisible = true, Topmost = true, OnScreen = true });
                Check(runtime.Version == displayedVersion, "runtime-unchanged-window-does-not-trigger-file-writes");
                runtime.Cancel(); Check(Json.Parse(runtime.Snapshot())["stage"].Equals("cancel"), "runtime-cancel-stage");
                runtime.Accepted(18);
                var reset = Json.Parse(runtime.Snapshot());
                Check(reset["stage"].Equals("learn_accepted") && reset["english_characters"].Equals(0) && reset["audio_queued_bytes"].Equals(0), "runtime-new-request-clears-old-counters");
                runtime.Error("voice_model_missing");
                Check(Json.Parse(runtime.Snapshot())["error_code"].Equals("voice_model_missing"), "runtime-known-error-code-is-useful");
                runtime.Error("api_unauthorized");
                Check(Json.Parse(runtime.Snapshot())["error_code"].Equals("api_unauthorized"), "runtime-api-failure-keeps-fixed-actionable-code");
                runtime.Error("api_forbidden");
                Check(Json.Parse(runtime.Snapshot())["error_code"].Equals("api_forbidden"), "runtime-api-forbidden-is-distinct-from-invalid-key");
                Check(LearningMessages.ForError("api_forbidden").Contains("代理") && !LearningMessages.ForError("api_forbidden").Contains("密钥"), "api-forbidden-message-does-not-claim-key-is-invalid");
                runtime.Ready("secret Chinese 中文 English payload API_KEY application_context");
                runtime.Error("secret Chinese 中文 English payload API_KEY application_context"); runtime.Exit();
                string diagnostic = runtime.Snapshot(); var safe = Json.Parse(diagnostic);
                string[] allowedFields = { "format_version", "pid", "updated_utc", "stage", "request_id", "english_characters", "audio_queued_packets", "audio_queued_bytes", "model_ready", "voice_ready", "translation_state", "device", "error_code", "worker_epoch", "worker_state", "automatic_restarts", "writeback_clicks", "writeback_state", "writeback_check", "writeback_target_pid", "writeback_foreground_pid", "window_visible", "window_english_visible", "window_topmost", "window_on_screen", "window_cloaked" };
                Check(safe.Count == allowedFields.Length && allowedFields.All(safe.ContainsKey) && safe["stage"].Equals("exit") &&
                    safe["device"].Equals("unknown") && safe["error_code"].Equals("learning_failed") && !diagnostic.Contains("secret") && !diagnostic.Contains("中文") &&
                    !diagnostic.Contains("payload") && !diagnostic.Contains("API_KEY") && !diagnostic.Contains("application_context"), "runtime-fixed-schema-sanitizes-device-errors-and-never-accepts-user-text");
                DesktopFeatureTests.Run(Check);
                StabilityTests.Run(Check);
                StartupMigrationTests.Run(Check);
                ModelProviderTests.Run(Check);
                ApiServiceTests.Run(Check);
                ModelSettingsUiTests.Run(Check);
                StartupServiceTests.Run(Check);
                StartupSettingsUiTests.Run(Check);
                ToolbarIconTests.Run(Check);
                LearningWindowLifetimeTests.Run(Check);
                EnglishWritebackTests.Run(Check);
                EnglishWritebackSettingsTests.Run(Check);
                EnglishCopyTests.Run(Check);
                LearningEnhancementTests.Run(Check);
                SelectionLearningTests.Run(Check);
                string startupStateRoot = Path.Combine(Path.GetTempPath(), "MansurStartupResultTests");
                string startupResultName = "startup-operation-" + Guid.NewGuid().ToString("N") + ".json";
                Check(StartupCommand.ValidResultPath(Path.Combine(startupStateRoot, startupResultName), startupStateRoot), "startup-result-accepts-only-own-nonce-file");
                Check(!StartupCommand.ValidResultPath(Path.Combine(startupStateRoot, "local-models.json"), startupStateRoot) &&
                    !StartupCommand.ValidResultPath(Path.Combine(startupStateRoot, "nested", startupResultName), startupStateRoot) &&
                    !StartupCommand.ValidResultPath(Path.Combine(Path.GetTempPath(), startupResultName), startupStateRoot) &&
                    !StartupCommand.ValidResultPath(startupResultName, startupStateRoot), "startup-result-rejects-user-config-and-other-directory");
                Console.WriteLine(Json.Write(new { status = "PASS", checks = passed.Count, names = passed.ToArray(), scope = "Headless protocol/data/settings/lexicon and lifecycle tests; isolated temporary files, in-memory settings, disposable fake child processes. No shown UI, model, registry writes, real-mode query/set, pipe listener or audio playback started." }));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(Json.Write(new { status = "FAIL", checks = passed.Count, failure = error.Message }));
                return 1;
            }
        }
    }
}
