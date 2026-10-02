// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Mansur.Next.Desktop
{
    internal sealed partial class BrokerContext
    {
        private void SelectionChanged(SelectionSpan span)
        {
            lock (gate) {
                if (closing || span != null && (span.Source != english || !translationFinal)) return;
                if (selection.Span != null && selection.Span.Same(span)) return;
                CancelSelectionLocked(false); selection.Bound(span); changed = true;
            }
        }
        private void CancelSelectionLocked(bool clear)
        {
            if (selection.Id > 0) {
                if (worker != null && selection.Busy) worker.Send(new { op = "cancel", request_id = selection.Id, channel = "selection" }, null);
                player.Reset(0);
                if (selectionSystemVoice) systemVoice.Cancel();
            }
            selectionSystemVoice = false; selection.Cancel();
            if (clear) selection.Bound(null);
        }
        private void RequestSelection(SelectionSpan span, bool speech, bool slow)
        {
            lock (gate) {
                if (closing || !visible || !translationFinal || span == null || span.Source != english || !span.Same(selection.Span)) return;
                if (selection.Busy && selection.Speech == speech) return;
                CancelSelectionLocked(false);
                // An explicit selection action stops sentence playback. An incomplete
                // recording never becomes a replayable whole sentence.
                if (!finished) receivedAudio.SetLength(0);
                finished = true; player.Reset(0); windowLifetime.Reset(); showFailure = false;
                if (!speech && selection.Meaning != null) { changed = true; return; }
                double speed = slow ? 0.75 : selectedSpeed;
                if (speech && selection.CanReplay(selectedVoice, speed)) {
                    selection.PrepareReplay(); PlaySelectionAudio(); changed = true; return;
                }
                object command = selection.Begin(speech, selectedVoice, speed);
                player.Reset(selection.Id); systemVoice.Cancel(); usingSystemVoice = false;
                var accepted = worker.Send(command, selection.Id);
                if (accepted == WorkerSendResult.Unavailable) selection.Fail("学习后台暂不可用，请在设置中检查模型与 API。");
                changed = true;
            }
        }
        private void PlaySelectionAudio()
        {
            // Only an explicit replay, from retained in-memory PCM, avoids another API call.
            long id = selection.Id; byte[] audio = selection.Audio.ToArray();
            player.Reset(id);
            for (int offset = 0; offset < audio.Length; offset += 96000) {
                int count = Math.Min(96000, audio.Length - offset); var part = new byte[count]; Buffer.BlockCopy(audio, offset, part, 0, count);
                if (!player.Enqueue(id, part, selection.Rate)) { selection.Fail("音频设备暂不可用，释义已保留。"); break; }
            }
        }
        private void SelectionEvent(long id, string kind, Dictionary<string, object> value)
        {
            if (!selection.IsCurrent(id) || !selection.Busy) return;
            try {
                if (kind == "selection_result") {
                    object raw; var result = value.TryGetValue("result", out raw) ? raw as Dictionary<string, object> : null;
                    if (selection.Speech || result == null) throw new FormatException("Selection result mismatch.");
                    selection.Result(result);
                    object details, actual;
                    var models = value.TryGetValue("runtime", out details) ? details as Dictionary<string, object> : null;
                    if (models != null) {
                        string device = models.TryGetValue("device", out actual) ? actual as string : null;
                        string translation = models.TryGetValue("translation_state", out actual) ? actual as string : null;
                        runtime.Models(device, translation); modelSettings.NotifyReady(translation == "ready", workerVoiceReady);
                        RefreshModelSettingsView();
                    }
                } else if (kind == "translation") {
                    selection.ConfirmSpeech(Json.String(value, "text", 256));
                } else if (kind == "audio") {
                    if (selectionSystemVoice) return;
                    byte[] pcm = selection.ReadAudio(value);
                    if (!player.Enqueue(id, pcm, selection.Rate)) throw new FormatException("Selection audio queue unavailable.");
                } else if (kind == "done") selection.Done();
                else if (kind == "error") {
                    if (selection.Speech && selection.SpeechConfirmed && selection.Audio.Length == 0 && allowSystemVoice) {
                        selectionSystemVoice = true; systemVoice.Start(id, selection.Span.Text, selection.SpeechSpeed);
                    } else selection.Fail(SelectionError(ErrorCode(value), selection.Speech));
                }
            } catch (Exception error) when (error is FormatException || error is ArgumentException || error is InvalidCastException || error is OverflowException) {
                selection.Fail("本次学习结果不完整，请重试。"); player.Reset(0);
            }
            changed = true;
        }
        private bool SelectionVoiceResult(long id, byte[] pcm, string error)
        {
            if (!selection.IsCurrent(id) || !selectionSystemVoice) return false;
            selectionSystemVoice = false;
            if (closing) return true;
            if (error != null || pcm == null || pcm.Length == 0 || pcm.Length > 2 * 1024 * 1024) selection.Fail("Windows 英语声音暂不可用，请检查系统英语语音包。");
            else {
                selection.Rate = SystemVoiceFallback.SampleRate;
                selection.Audio.Write(pcm, 0, pcm.Length);
                selection.Done(); PlaySelectionAudio();
            }
            changed = true; return true;
        }
        private static string SelectionError(string code, bool speech)
        {
            if (code == "api_forbidden" || code == "api_unauthorized") return LearningMessages.ForError(code);
            if (code == "selection_response_invalid") return "模型未返回完整释义，请重试或更换翻译模型。";
            if (code == "api_timeout" || code == "translation_timeout") return "本次学习等待超时，请稍后重试。";
            return speech ? "朗读暂不可用，请检查语音模型与 API。" : "查词暂不可用，请检查翻译模型与 API。";
        }
    }
}
