// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class SelectionLearningTests
    {
        private static Dictionary<string, object> Meaning()
        { return new Dictionary<string, object> { { "meaning", "维多利亚；此处是名称的一部分" }, { "pronunciation", "" }, { "part_of_speech", "专有名词" }, { "usage", "与 Square 组成地点名称。" } }; }
        private static bool Reject(Action action) { try { action(); return false; } catch (FormatException) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            const string source = "Tonight I will land at Victoria Square.";
            var span = SelectionSpan.Create(source, source.IndexOf("Victoria", StringComparison.Ordinal), 8);
            check(span != null && span.Text == "Victoria" && span.Source == source, "selection-span-belongs-to-current-english-only");
            check(SelectionSpan.Create(source, -1, 3) == null && SelectionSpan.Create(source, Int32.MaxValue, 3) == null && SelectionSpan.Create(new string('a', 1025), 0, 8) == null,
                "selection-span-rejects-invalid-ranges-and-oversized-context");
            var state = new SelectionLearning(); state.Bound(span);
            check(!state.Busy && state.Id == 0 && state.Meaning == null, "selection-alone-never-creates-a-model-request");
            var command = Json.Parse(Json.Write(state.Begin(false, "af_heart", 1)));
            long oldId = state.Id;
            check(Json.String(command, "op", 32) == "selection_study" && Json.String(command, "context", 1024) == source && Json.String(command, "text", 256) == "Victoria",
                "selection-explicit-query-sends-only-owned-english-and-span");
            state.Result(Meaning()); state.Done();
            check(!state.Busy && state.Display.Contains("地") && state.Audio.Length == 0, "selection-definition-completes-without-automatic-speech");
            state.Begin(true, "af_heart", 0.75);
            var audio = new Dictionary<string, object> { { "chunk_index", 0 }, { "sample_rate", 24000 }, { "channels", 1 }, { "pcm_s16le", "AAABAA==" } };
            check(Reject(() => state.ReadAudio(audio)), "selection-audio-before-confirmed-original-is-rejected");
            check(Reject(() => state.ConfirmSpeech("different")), "selection-speech-cannot-substitute-another-word");
            state.ConfirmSpeech("Victoria"); state.ReadAudio(audio); state.Done();
            check(state.Meaning != null && state.CanReplay("af_heart", 0.75) && !state.CanReplay("af_heart", 1), "selection-audio-cache-keeps-meaning-and-respects-speed");
            state.Cancel(); state.PrepareReplay();
            check(state.Audio.Length == 4 && state.Id > oldId && !state.Busy, "selection-explicit-replay-retains-pcm-without-new-request");
            state.Bound(SelectionSpan.Create(source, source.IndexOf("Square", StringComparison.Ordinal), 6));
            check(state.Meaning == null && state.Audio.Length == 0 && !state.IsCurrent(oldId), "selection-change-retires-results-and-audio");
            check(Reject(() => SelectionMeaning.Parse(new Dictionary<string, object> { { "meaning", "" }, { "pronunciation", "" }, { "part_of_speech", "" }, { "usage", "" } })), "selection-empty-definition-is-not-a-valid-result");
            int requests = 0, copies = 0; bool requestedSlow = false;
            using (var form = new FloatingForm(text => copies++)) {
                form.PreparePreview(source, "", new Size(1280, 800));
                form.SelectionRequested += (value, speech, slow) => { requests++; requestedSlow = slow; };
                form.TextView.Select(span.Start, span.Length); form.TextView.NotifySelection();
                check(form.SelectionView.HasSelection && requests == 0 && copies == 0, "selection-ui-reveals-actions-without-calling-or-copying");
                form.SelectionView.Request(false, false);
                check(requests == 1 && form.TextView.SelectedText == "Victoria", "selection-explicit-action-preserves-original-highlight");
                form.UpdateSelectionStudy(span, SelectionMeaning.Parse(Meaning()).Display());
                check(form.SelectionView.DetailView.Text.Contains("专有名词") && form.TextView.Text == source, "selection-card-shows-meaning-without-changing-original");
                form.ApplyPreferences(new Preferences { Theme = 2, FontSize = 20 });
                form.PreparePreview(source, "", new Size(500, 450));
                check(form.SelectionView.DetailView.ForeColor == form.TextView.ForeColor && form.Width <= 500 && form.Height <= 450, "selection-card-follows-theme-font-and-viewport");
                form.TextView.HandleCopyKey(Keys.Control | Keys.C);
                check(copies == 1 && requests == 1, "selection-copy-never-triggers-a-study-request");
                form.TextView.HandleCopyKey(Keys.Control | Keys.D); form.TextView.HandleCopyKey(Keys.Control | Keys.R);
                check(requests == 3 && form.TextView.SelectedText == "Victoria", "selection-keyboard-query-and-speech-keep-owned-highlight");
                form.SelectionView.SlowVoice = true; form.TextView.HandleCopyKey(Keys.Control | Keys.R);
                check(requests == 4 && requestedSlow, "selection-keyboard-speech-follows-slow-checkbox");
                form.TextView.HandleCopyKey(Keys.Escape);
                typeof(SelectableEnglishText).GetMethod("OnKeyUp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(form.TextView, new object[] { new KeyEventArgs(Keys.Escape) });
                check(!form.SelectionView.HasSelection && form.TextView.Text == source, "selection-escape-closes-card-and-keeps-sentence");
                form.SetActions(false, false); form.TextView.NotifySelection();
                check(!form.SelectionView.HasSelection, "selection-incomplete-english-cannot-open-learning-actions");
                form.SetActions(true, false); form.TextView.NotifySelection();
                form.PreparePreview("Hello", "", new Size(1280, 800));
                check(!form.SelectionView.HasSelection, "selection-new-sentence-clears-old-card");
                form.PreparePreview(source, "", new Size(340, 180));
                form.TextView.Select(span.Start, span.Length); form.TextView.NotifySelection();
                form.UpdateSelectionStudy(span, SelectionMeaning.Parse(Meaning()).Display());
                form.PreparePreview("Hello", "", new Size(1280, 800));
                var body = form.TextView.Parent as Panel;
                check(body != null && !body.HorizontalScroll.Visible, "selection-narrow-card-to-short-sentence-removes-stale-scrollbars");
            }
            state.Audio.Dispose();
        }
    }
}
