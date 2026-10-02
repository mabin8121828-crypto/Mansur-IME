// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Mansur.Next.Desktop
{
    // Only counters and allowlisted labels enter this state. No request, text or context is retained.
    // Caller holds the broker gate; all filesystem work is deferred to its existing UI timer.
    internal sealed class RuntimeStatus
    {
        private static readonly HashSet<string> ErrorCodes = new HashSet<string>(StringComparer.Ordinal) {
            "startup_failed", "worker_unavailable", "worker_exited", "worker_protocol_error", "audio_device_error",
            "text_limit_exceeded", "learning_failed", "translation_ownership_failed", "translation_runtime_missing",
            "translation_model_missing", "translation_start_failed", "translation_start_timeout", "translation_timeout",
            "translation_http_error", "translation_response_invalid", "translation_connection_failed", "voice_runtime_missing",
            "voice_model_missing", "voice_styles_missing", "voice_load_failed", "voice_audio_invalid", "voice_synthesis_failed",
            "voice_audio_empty", "invalid_request", "invalid_request_id", "invalid_text", "invalid_voice", "invalid_speed",
            "invalid_operation", "request_id_not_increasing", "request_wait_timeout", "worker_start_timeout", "worker_request_timeout", "worker_restarted",
            "configuration_unavailable", "api_model_missing", "api_key_missing", "api_key_unreadable", "api_unauthorized", "api_credit_required",
            "api_rate_limited", "api_model_not_found", "api_request_invalid", "api_provider_unavailable", "api_http_error", "api_forbidden",
            "api_redirect_rejected", "api_timeout", "api_connection_failed", "api_proxy_auth_required",
            "api_proxy_unsupported", "api_proxy_resolution_failed", "api_proxy_connect_failed", "api_base_url_invalid",
            "api_service_invalid", "api_voice_missing", "api_audio_format_invalid", "system_voice_unavailable"
        };
        private readonly int pid;
        private string stage = "startup", utc, device = "unknown", errorCode;
        private long? requestId;
        private int characters, packets;
        private long bytes;
        private bool ready, voiceReady;
        private long workerEpoch;
        private int automaticRestarts;
        private int writebackClicks;
        private string writebackState = "not_requested";
        private string writebackCheck = "None";
        private uint writebackTargetPid, writebackForegroundPid;
        private string workerState = "starting", translationState = "not_started";
        private bool windowVisible, windowEnglishVisible, windowTopmost, windowOnScreen, windowCloaked;
        internal long Version { get; private set; }
        internal RuntimeStatus(int processId) { pid = processId; Touch("startup"); }
        private void Touch(string value)
        { stage = value; utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); Version++; }
        internal void Ready(string actualDevice, string translation = "ready", bool speech = true)
        {
            Models(actualDevice, translation);
            ready = true; voiceReady = speech; workerState = speech ? "ready" : "degraded"; errorCode = null; Touch("ready");
        }
        internal void Models(string actualDevice, string translation)
        {
            // Device identifiers are runtime backend tokens, never arbitrary worker strings.
            device = actualDevice != null && actualDevice.Length <= 16 &&
                Regex.IsMatch(actualDevice, @"\A(?:none|CPU|Vulkan[0-9]{1,3}|CUDA[0-9]{1,3}|SYCL[0-9]{1,3}|Metal)\z")
                ? actualDevice : "unknown";
            translationState = translation == "ready" ? "ready" : "not_started";
            Version++;
        }
        internal void Starting(long epoch = 0) { ready = voiceReady = false; device = "unknown"; translationState = "not_started"; workerState = "starting"; if (epoch > workerEpoch) workerEpoch = epoch; Touch("starting"); }
        internal void Unavailable(string code, long epoch = 0, bool recovering = false)
        {
            ready = voiceReady = false; translationState = "not_started"; workerState = recovering ? "recovering" : "unavailable";
            if (epoch > workerEpoch) workerEpoch = epoch;
            if (recovering && automaticRestarts < Int32.MaxValue) automaticRestarts++;
            Error(code);
        }
        internal void Accepted(long id)
        { requestId = id; characters = 0; packets = 0; bytes = 0; errorCode = null; Touch("learn_accepted"); }
        internal void Translation(int count) { characters = Math.Max(0, count); Touch("translation"); }
        internal void TranslationPartial(int count) { characters = Math.Max(0, count); Touch("translation_partial"); }
        internal void SystemVoice() { Touch("system_voice"); }
        internal void AudioQueued(int packetCount, long byteCount)
        { packets = Math.Max(0, packetCount); bytes = Math.Max(0, byteCount); Touch("audio_queued"); }
        internal void Done() { Touch("done"); }
        internal void Window(FloatingPresentation state)
        {
            if (windowVisible == state.Visible && windowEnglishVisible == state.EnglishVisible && windowTopmost == state.Topmost &&
                windowOnScreen == state.OnScreen && windowCloaked == state.Cloaked) return;
            windowVisible = state.Visible; windowEnglishVisible = state.EnglishVisible; windowTopmost = state.Topmost;
            windowOnScreen = state.OnScreen; windowCloaked = state.Cloaked; Version++;
        }
        internal void Cancel() { Touch("cancel"); }
        internal void WritebackClick()
        { if (writebackClicks < Int32.MaxValue) writebackClicks++; writebackState = "checking"; Version++; }
        internal void WritebackFinished(EnglishWritebackResult result)
        {
            writebackState = result == EnglishWritebackResult.Written ? "written" :
                result == EnglishWritebackResult.Rejected ? "rejected" :
                result == EnglishWritebackResult.Unavailable ? "unavailable" : "unknown";
            Version++;
        }
        internal void WritebackTrace(WritebackCheck check, uint target, uint foreground)
        {
            writebackCheck = Enum.IsDefined(typeof(WritebackCheck), check) ? check.ToString() : "NativeUnknown";
            writebackTargetPid = target; writebackForegroundPid = foreground; Version++;
        }
        internal void Error(string code)
        { errorCode = code != null && ErrorCodes.Contains(code) ? code : "learning_failed"; Touch("error"); }
        internal void Exit() { ready = voiceReady = false; workerState = "stopped"; Touch("exit"); }
        internal string Snapshot()
        {
            return Json.Write(new { format_version = 1, pid, updated_utc = utc, stage, request_id = requestId,
                english_characters = characters, audio_queued_packets = packets, audio_queued_bytes = bytes,
                model_ready = ready, voice_ready = voiceReady, translation_state = translationState, device, error_code = errorCode, worker_epoch = workerEpoch, worker_state = workerState, automatic_restarts = automaticRestarts,
                writeback_clicks = writebackClicks, writeback_state = writebackState,
                writeback_check = writebackCheck, writeback_target_pid = writebackTargetPid, writeback_foreground_pid = writebackForegroundPid,
                window_visible = windowVisible, window_english_visible = windowEnglishVisible, window_topmost = windowTopmost,
                window_on_screen = windowOnScreen, window_cloaked = windowCloaked });
        }
    }

    internal sealed class RuntimeStatusWriter
    {
        private readonly string directory = UserStateDirectory.Root;
        private long savedVersion = -1, nextAttempt;
        internal void TrySave(string snapshot, long version, bool force)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force && (version == savedVersion || now < nextAttempt)) return;
            nextAttempt = now + Stopwatch.Frequency / 2;
            string temporary = null;
            try
            {
                Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, "runtime-status.json");
                temporary = Path.Combine(directory, "runtime-status." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp");
                File.WriteAllText(temporary, snapshot, new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                savedVersion = version;
            }
            catch (Exception) { /* Diagnostics must never stop input, model work or playback. */ }
            finally
            {
                if (temporary != null) try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
            }
        }
    }
}
