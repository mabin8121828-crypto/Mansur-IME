// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal sealed partial class BrokerContext : ApplicationContext
    {
        private readonly object gate = new object();
        private readonly RequestTracker tracker = new RequestTracker();
        private readonly RuntimeStatus runtime = new RuntimeStatus(Process.GetCurrentProcess().Id);
        private readonly RuntimeStatusWriter runtimeWriter = new RuntimeStatusWriter();
        private readonly FloatingForm floating = new FloatingForm();
        private readonly LearningWindowLifetime windowLifetime = new LearningWindowLifetime();
        private readonly SettingsStore settings = new SettingsStore(new SharedSettingsValues());
        private readonly Control dispatcher = new Control();
        private readonly ToolbarForm toolbar;
        private readonly LexiconManager lexicons;
        private readonly string configurationPath;
        private readonly ModelSettingsService modelSettings;
        private ControlChannel control;
        private SettingsForm settingsForm;
        private int preferencesTick;
        private readonly NotifyIcon tray;
        private readonly Icon productIcon = ProductIcon.Create(SystemInformation.SmallIconSize);
        private readonly Timer refresh;
        private readonly PcmPlayer player;
        private readonly SystemVoiceFallback systemVoice;
        private readonly ToolStripMenuItem replay, stop;
        private readonly ToolStripMenuItem[] voiceItems = new ToolStripMenuItem[4];
        private readonly ToolStripMenuItem[] speedItems = new ToolStripMenuItem[3];
        private readonly MemoryStream receivedAudio = new MemoryStream();
        private readonly SelectionLearning selection = new SelectionLearning();
        private bool selectionSystemVoice;
        private WorkerSupervisor worker;
        private readonly IInputModeTransport inputMode = new NativeInputMode();
        private bool modeBusy, togglePending, disposed;
        private volatile bool modelSetupNeeded;
        private PipeListener listener;
        private string selectedVoice = "af_heart", english = "", status = "正在准备学习服务…";
        private double selectedSpeed = 1.0;
        private bool changed, visible, finished, closing, showFailure;
        private bool translationFinal, allowSystemVoice, usingSystemVoice, workerVoiceReady = true;
        private int nextChunk, audioRate;
        private Rectangle? anchor;
        internal BrokerContext(string configPath, SecurityIdentifier user, bool showSettings = false)
        {
            configurationPath = Path.GetFullPath(configPath);
            modelSettings = new ModelSettingsService(configurationPath);
            var dispatcherHandle = dispatcher.Handle;
            lexicons = new LexiconManager(settings, Path.Combine(UserStateDirectory.Root, "lexicons"), AppDomain.CurrentDomain.BaseDirectory);
            toolbar = new ToolbarForm(ToolbarAction, point => TrySetting(() => settings.SetToolbarPosition(point)));
            player = new PcmPlayer(AudioFailure);
            systemVoice = new SystemVoiceFallback(SystemVoiceResult);
            floating.DismissRequested += DismissLearningWindow;
            floating.ReplayRequested += Replay;
            floating.SelectionChanged += SelectionChanged;
            floating.SelectionRequested += RequestSelection;
            var menu = new ContextMenuStrip();
            var voices = new ToolStripMenuItem("朗读声音");
            string[] labels = { "Heart · 美式女声", "Bella · 美式女声", "Michael · 美式男声", "Emma · 英式女声" };
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                voiceItems[i] = new ToolStripMenuItem(labels[i], null, delegate { SelectVoice(index); });
                voices.DropDownItems.Add(voiceItems[i]);
            }
            var speeds = new ToolStripMenuItem("朗读速度");
            double[] values = { 0.75, 1.0, 1.25 };
            string[] speedLabels = { "慢速 · 0.75×", "正常 · 1.0×", "快速 · 1.25×" };
            for (int i = 0; i < values.Length; i++)
            {
                double value = values[i];
                speedItems[i] = new ToolStripMenuItem(speedLabels[i], null, delegate { SelectSpeed(value); });
                speeds.DropDownItems.Add(speedItems[i]);
            }
            replay = new ToolStripMenuItem("重播本句", null, delegate { Replay(); }) { Enabled = false };
            stop = new ToolStripMenuItem("停止并隐藏", null, delegate { CancelCurrent(); });
            menu.Items.Add(voices); menu.Items.Add(speeds); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(replay); menu.Items.Add(stop); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("设置…", null, delegate { ShowSettings(); }));
            menu.Items.Add(new ToolStripMenuItem("显示悬浮状态栏", null, delegate { TrySetting(() => settings.SetToolbarVisible(true)); ReloadPreferences(); }));
            menu.Items.Add(new ToolStripMenuItem("重启学习服务", null, delegate { RestartLearning(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出 Mansur 学习后台", null, delegate { ExitThread(); }));
            tray = new NotifyIcon { Icon = productIcon, Text = "Mansur 学习后台 · 准备中", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { Replay(); };
            ReloadPreferences();
            refresh = new Timer { Interval = 50 };
            refresh.Tick += delegate { RefreshView(); };
            refresh.Start();
            try
            {
                worker = new WorkerSupervisor((events, failed) => {
                    var configuration = Configuration.Load(configurationPath);
                    modelSettings.NotifyStarting(configuration);
                    RefreshModelSettingsView();
                    return new WorkerProcess(configuration, events, failed);
                }, WorkerEvent,
                    WorkerFailure, WorkerRequestFailure, WorkerState);
                worker.Start();
                listener = new PipeListener(user, PipeMessage);
                control = new ControlChannel(user, command => {
                    try { dispatcher.BeginInvoke((Action)(() => {
                        if (closing) return;
                        if (command == "shutdown") ExitThread();
                        else if (command == "show-settings") ShowSettings();
                        else if (command == "show-toolbar") { TrySetting(() => settings.SetToolbarVisible(true)); ReloadPreferences(); }
                    })); } catch (InvalidOperationException) { }
                });
                if (showSettings) dispatcher.BeginInvoke((Action)ShowSettings);
            }
            catch
            {
                lock (gate) runtime.Error("startup_failed");
                Dispose(true);
                throw;
            }
        }
        private void SelectVoice(int index)
        {
            TrySetting(() => settings.SetVoice(LearningRequest.Voices[index])); ReloadPreferences();
        }
        private void SelectSpeed(double value)
        {
            TrySetting(() => settings.SetSpeed((int)Math.Round(value * 100))); ReloadPreferences();
        }
        private void TrySetting(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException || error is ArgumentException || error is FormatException)
            { tray.ShowBalloonTip(3000, "Mansur", "设置暂时无法保存，请在设置窗口重试。", ToolTipIcon.Warning); }
        }
        private void ReloadPreferences()
        {
            try
            {
                var p = settings.Read();
                lock (gate) { selectedVoice = p.Voice; selectedSpeed = p.SpeedPercent / 100.0; allowSystemVoice = p.SystemSpeechFallback; }
                for (int i = 0; i < voiceItems.Length; i++) voiceItems[i].Checked = LearningRequest.Voices[i] == p.Voice;
                for (int i = 0; i < speedItems.Length; i++) speedItems[i].Checked = new[] { 75, 100, 125 }[i] == p.SpeedPercent;
                if (floating.ApplyPreferences(p)) lock (gate) changed = true;
                toolbar.Apply(p);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException) { }
        }
        private void ToolbarAction(int action)
        {
            if (action == 0) RefreshMode(true);
            else if (action == 1)
            {
                lock (gate) { visible = true; changed = true; if (tracker.Current == null) status = "完成选词后快速按三次空格，即可翻译朗读。"; }
                Replay();
            }
            else if (action == 2) ShowSettings();
            else if (action == 3) TrySetting(() => settings.SetToolbarVisible(false));
            ReloadPreferences();
        }
        private void RefreshMode(bool toggle = false)
        {
            if (closing) return;
            if (modeBusy) { if (toggle) togglePending = true; return; }
            modeBusy = true;
            Task.Run(() => {
                InputModeSnapshot snapshot = null;
                try { snapshot = toggle ? InputModeCommands.Toggle(inputMode) : inputMode.Query(); } catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception) { }
                try { dispatcher.BeginInvoke((Action)(() => {
                    modeBusy = false;
                    if (closing) return;
                    if (togglePending) { togglePending = false; RefreshMode(true); return; }
                    toolbar.UpdateMode(snapshot == null ? (bool?)null : snapshot.Chinese);
                    if (toggle && snapshot == null) tray.ShowBalloonTip(2500, "Mansur", "请先将光标放在使用新版输入法的输入框，再切换中英文。", ToolTipIcon.Info);
                })); } catch (InvalidOperationException) { }
            });
        }
        private void ShowSettings()
        {
            TrySetting(() => {
                if (settingsForm == null || settingsForm.IsDisposed)
                    settingsForm = new SettingsForm(settings, lexicons, ReloadPreferences, modelSettings, RestartLearning, StartupService.CreateForCurrentUser());
                if (modelSetupNeeded) settingsForm.SelectModelsPage();
                settingsForm.Show(); settingsForm.Activate();
            });
        }
        private void RefreshModelSettingsView()
        {
            try { dispatcher.BeginInvoke((Action)(() => {
                if (!closing && settingsForm != null && !settingsForm.IsDisposed)
                    settingsForm.RefreshModelRuntimeState();
            })); } catch (InvalidOperationException) { }
        }
        private void PipeMessage(string line)
        {
            LearningRequest request;
            try { request = LearningRequest.Parse(line); }
            catch (Exception error) when (error is FormatException || error is ArgumentException || error is InvalidOperationException) { return; }
            lock (gate)
            {
                if (closing) return;
                if (request.Operation == "cancel")
                {
                    if (!tracker.Cancel(request)) return;
                    runtime.Cancel();
                    bool keepSelection = floating.CopyInteraction && visible && !String.IsNullOrEmpty(english);
                    windowLifetime.Reset();
                    if (!keepSelection) CancelSelectionLocked(true);
                    if (!keepSelection || selection.Id == 0) { player.Reset(0); systemVoice.Cancel(); }
                    usingSystemVoice = false;
                    receivedAudio.SetLength(0); visible = keepSelection; finished = keepSelection; changed = true;
                    worker.Send(keepSelection ? (object)new { op = "cancel", channel = "sentence" } : new { op = "cancel" }, null);
                    return;
                }
                if (!tracker.Accept(request)) return;
                CancelSelectionLocked(true);
                runtime.Accepted(request.Id);
                request.Voice = request.Voice ?? selectedVoice;
                request.Speed = request.Speed ?? selectedSpeed;
                player.Reset(request.Id);
                systemVoice.Cancel(); usingSystemVoice = false; translationFinal = false;
                windowLifetime.Reset(); showFailure = false;
                receivedAudio.SetLength(0); nextChunk = 0; audioRate = 0; finished = false;
                english = ""; status = request.CanLearn ? "正在准备英文…" : LearningRequest.LengthNotice;
                finished = !request.CanLearn; anchor = request.Anchor; visible = true; changed = true;
                if (!request.CanLearn) runtime.Error("text_limit_exceeded");
                var accepted = worker.Send(request.WorkerCommand(), request.CanLearn ? (long?)request.Id : null);
                if (accepted == WorkerSendResult.Unavailable) SetFailure("学习后台暂不可用；文字已提交。可从托盘重启本地学习后台。", "worker_unavailable");
                else if (request.CanLearn && accepted == WorkerSendResult.Waiting) status = "正在准备本地模型，最多等待30秒…";
            }
        }
        private void WorkerEvent(long epoch, Dictionary<string, object> value)
        {
            try
            {
                string kind = Json.String(value, "event", 32);
                object rawId;
                lock (gate)
                {
                    if (closing || worker == null || !worker.IsCurrent(epoch)) return;
                    if (!value.TryGetValue("request_id", out rawId) || rawId == null)
                    {
                        if (kind == "ready")
                        {
                            object details, actual;
                            var detailsObject = value.TryGetValue("runtime", out details) ? details as Dictionary<string, object> : null;
                            string actualDevice = detailsObject != null && detailsObject.TryGetValue("device", out actual) ? actual as string : null;
                            string translation = detailsObject != null && detailsObject.TryGetValue("translation_state", out actual) ? actual as string : null;
                            workerVoiceReady = detailsObject == null || !detailsObject.TryGetValue("voice_ready", out actual) || !(actual is bool) || (bool)actual;
                            runtime.Ready(actualDevice, translation, workerVoiceReady);
                            modelSettings.NotifyReady(translation == "ready", workerVoiceReady);
                            modelSetupNeeded = false;
                            RefreshModelSettingsView();
                            if (tracker.Current == null) status = "学习服务已就绪"; changed = true;
                        }
                        else if (kind == "error") SetFailure(LearningMessages.ForError(ErrorCode(value)), ErrorCode(value));
                        return;
                    }
                    long id = Json.Integer(value, "request_id", 0, Int64.MaxValue);
                    object channel;
                    if (value.TryGetValue("channel", out channel) && Object.Equals(channel, "selection") || selection.IsCurrent(id))
                    { SelectionEvent(id, kind, value); return; }
                    if (!tracker.IsCurrent(id) || finished) return;
                    if (kind == "translation_partial")
                    {
                        if (translationFinal) throw new FormatException("Late partial translation.");
                        english = Json.String(value, "text", 4096); status = "正在生成…"; changed = true;
                        runtime.TranslationPartial(english.Length);
                    }
                    else if (kind == "translation")
                    {
                        translationFinal = true;
                        english = Json.String(value, "text", 4096); status = "正在准备英文朗读…"; changed = true;
                        object details, actual;
                        var models = value.TryGetValue("runtime", out details) ? details as Dictionary<string, object> : null;
                        if (models != null)
                        {
                            string device = models.TryGetValue("device", out actual) ? actual as string : null;
                            string translation = models.TryGetValue("translation_state", out actual) ? actual as string : null;
                            runtime.Models(device, translation);
                            modelSettings.NotifyReady(translation == "ready", workerVoiceReady);
                            RefreshModelSettingsView();
                        }
                        int characters = 0;
                        for (int i = 0; i < english.Length; i++, characters++)
                            if (Char.IsHighSurrogate(english[i]) && i + 1 < english.Length && Char.IsLowSurrogate(english[i + 1])) i++;
                        runtime.Translation(characters);
                    }
                    else if (kind == "audio")
                    {
                        if (!translationFinal || usingSystemVoice) throw new FormatException("Audio before final translation.");
                        int index = (int)Json.Integer(value, "chunk_index", 0, Int32.MaxValue);
                        int rate = (int)Json.Integer(value, "sample_rate", 8000, 96000);
                        if (Json.Integer(value, "channels", 1, 1) != 1 || index != nextChunk || (audioRate != 0 && audioRate != rate))
                            throw new FormatException("Audio order mismatch.");
                        byte[] pcm = Convert.FromBase64String(Json.String(value, "pcm_s16le", 128000));
                        if (pcm.Length == 0 || pcm.Length > 96000 || (pcm.Length & 1) != 0 || receivedAudio.Length > 8 * 1024 * 1024 - pcm.Length)
                            throw new FormatException("Audio limit exceeded.");
                        if (!player.Enqueue(id, pcm, rate)) throw new FormatException("Audio queue unavailable.");
                        receivedAudio.Write(pcm, 0, pcm.Length); nextChunk++; audioRate = rate;
                        runtime.AudioQueued(nextChunk, receivedAudio.Length);
                        status = ""; changed = true;
                    }
                    else if (kind == "done")
                    {
                        if (!translationFinal || receivedAudio.Length == 0) throw new FormatException("Incomplete learning result.");
                        finished = true;
                        runtime.Done();
                        status = "";
                        changed = true;
                    }
                    else if (kind == "error")
                    {
                        object failureStage;
                        bool speechFailure = value.TryGetValue("stage", out failureStage) && Object.Equals(failureStage, "speech");
                        if (speechFailure && translationFinal && allowSystemVoice && receivedAudio.Length == 0 && !usingSystemVoice)
                        {
                            usingSystemVoice = true; status = "朗读故障，正在使用 Windows 英语声音…"; showFailure = true; changed = true;
                            systemVoice.Start(id, english, tracker.Current.Speed ?? selectedSpeed);
                        }
                        else SetFailure(LearningMessages.ForError(ErrorCode(value)), ErrorCode(value));
                    }
                }
            }
            catch (Exception error) when (error is FormatException || error is ArgumentException || error is InvalidOperationException)
            { if (worker != null) worker.RejectProtocol(epoch); }
        }
        private static string ErrorCode(Dictionary<string, object> value)
        { object code; return value.TryGetValue("code", out code) ? code as string : null; }
        private void SetFailure(string message, string code)
        {
            systemVoice.Cancel(); usingSystemVoice = false;
            player.Reset(0); finished = true; receivedAudio.SetLength(0);
            windowLifetime.Reset(); showFailure = true;
            status = tracker.Current != null && !tracker.Current.CanLearn ? LearningRequest.LengthNotice : message;
            runtime.Error(tracker.Current != null && !tracker.Current.CanLearn ? "text_limit_exceeded" : code);
            changed = true;
            // Display only when a user request has already made the learning window visible.
        }
        private void WorkerFailure(WorkerFailureNotice notice)
        {
            lock (gate)
            {
                if (closing) return;
                if (selection.Busy) { selection.Fail("学习服务已停止，请稍后重试。"); selectionSystemVoice = false; changed = true; }
                if (notice.DispatchedRequest.HasValue && tracker.IsCurrent(notice.DispatchedRequest.Value) && !finished)
                    SetFailure(notice.Recovering ? "本次学习已停止，后台正在自动恢复。文字已保留。" : LearningMessages.ForError(notice.Code), notice.Code);
                if (worker == null || worker.IsLatestEpoch(notice.Epoch))
                {
                    runtime.Unavailable(notice.Code, notice.Epoch, notice.Recovering);
                    modelSettings.NotifyUnavailable();
                    modelSetupNeeded = notice.Code == "configuration_unavailable" || notice.Code == "api_key_missing" || notice.Code == "api_key_unreadable";
                    RefreshModelSettingsView();
                }
                if (tracker.Current == null) status = notice.Recovering ? "学习服务正在恢复…" : LearningMessages.ForError(notice.Code);
                changed = true;
            }
        }
        private void WorkerRequestFailure(long id, string code)
        { lock (gate) { if (closing) return; if (selection.IsCurrent(id)) { selection.Fail("本次查词或朗读等待超时，请稍后重试。"); changed = true; }
            else if (tracker.IsCurrent(id) && !finished) SetFailure("本次学习等待超时或后台已停止；文字已保留，请稍后重新确认一句。", code); } }
        private void WorkerState(long epoch, string state)
        {
            lock (gate)
            {
                if (closing || worker == null || !worker.IsCurrent(epoch)) return;
                runtime.Starting(epoch);
                if (tracker.Current == null) status = "正在准备学习服务…";
                changed = true;
            }
        }
        private void RestartLearning()
        {
            CancelCurrent();
            lock (gate) { if (closing) return; runtime.Starting(); status = "正在应用设置，准备学习服务…"; changed = true; }
            worker.Restart();
        }
        private void AudioFailure(long requestId, string code)
        { lock (gate) { if (closing) return; if (selection.IsCurrent(requestId)) { selection.Fail("音频设备暂不可用，释义已保留。"); changed = true; }
            else if (tracker.IsCurrent(requestId)) SetFailure("英文已保留，音频设备暂不可用。", "audio_device_error"); } }
        private void SystemVoiceResult(long id, byte[] pcm, string error)
        {
            lock (gate)
            {
                if (SelectionVoiceResult(id, pcm, error)) return;
                if (closing || !tracker.IsCurrent(id) || !usingSystemVoice || finished) return;
                usingSystemVoice = false;
                if (error != null || pcm == null || pcm.Length == 0)
                { SetFailure("英文已保留；Windows 英语声音暂不可用。可在系统语言设置中安装英语语音包。", "system_voice_unavailable"); return; }
                audioRate = SystemVoiceFallback.SampleRate;
                for (int offset = 0; offset < pcm.Length; offset += 96000)
                {
                    int count = Math.Min(96000, pcm.Length - offset); var part = new byte[count]; Buffer.BlockCopy(pcm, offset, part, 0, count);
                    if (!player.Enqueue(id, part, audioRate)) { SetFailure("英文已保留，音频设备暂不可用。", "audio_device_error"); return; }
                    receivedAudio.Write(part, 0, count); nextChunk++;
                }
                runtime.AudioQueued(nextChunk, receivedAudio.Length); runtime.SystemVoice();
                finished = true; showFailure = false; status = ""; changed = true;
            }
        }
        private void DismissLearningWindow()
        {
            lock (gate)
            {
                if (closing) return;
                CancelSelectionLocked(true);
                // Keep a completed recording for an explicit later replay. A
                // partially generated recording is not offered as a full sentence.
                if (!finished) receivedAudio.SetLength(0);
                finished = true; visible = false; changed = true;
                windowLifetime.Reset(); player.Reset(0); runtime.Cancel();
                systemVoice.Cancel(); usingSystemVoice = false;
                if (worker != null) worker.Send(new { op = "cancel" }, null);
            }
        }
        private void CancelCurrent()
        {
            lock (gate)
            {
                if (closing) return;
                CancelSelectionLocked(true);
                tracker.CancelCurrent(Stopwatch.GetTimestamp());
                runtime.Cancel();
                windowLifetime.Reset();
                player.Reset(0); receivedAudio.SetLength(0); visible = false; finished = false; changed = true;
                systemVoice.Cancel(); usingSystemVoice = false;
                if (worker != null) worker.Send(new { op = "cancel" }, null);
            }
        }
        private void Replay()
        {
            lock (gate)
            {
                if (closing || tracker.Current == null || !finished || receivedAudio.Length == 0) return;
                CancelSelectionLocked(true);
                long id = tracker.Current.Id;
                player.Reset(id);
                windowLifetime.Reset(); showFailure = false;
                byte[] all = receivedAudio.ToArray();
                for (int offset = 0; offset < all.Length; offset += 96000)
                {
                    int count = Math.Min(96000, all.Length - offset);
                    var part = new byte[count]; Buffer.BlockCopy(all, offset, part, 0, count);
                    if (!player.Enqueue(id, part, audioRate)) { SetFailure("英文已保留，暂时无法重播。", "audio_device_error"); return; }
                }
                visible = true; status = ""; changed = true;
            }
        }
        private void RefreshView()
        {
            if (++preferencesTick >= 10) { preferencesTick = 0; ReloadPreferences(); RefreshMode(); }
            // Generated text is separate from a shown, topmost, on-screen window.
            // These booleans contain no input text or editor contents.
            var presentation = floating.Presentation;
            lock (gate) runtime.Window(presentation);
            WriteRuntimeStatus(false);
            string text, detail, selectionDetail; SelectionSpan selectionSpan; bool show, canReplay, canCopy; Rectangle? location;
            bool pointerInside = floating.PointerInside || floating.CopyInteraction;
            lock (gate)
            {
                if (closing) return;
                if (visible && !showFailure && !String.IsNullOrEmpty(english) && (tracker.Current == null || receivedAudio.Length > 0) &&
                    windowLifetime.ShouldHide(finished, tracker.Current == null || player.IsDrained(tracker.Current.Id), pointerInside, Stopwatch.GetTimestamp()))
                { visible = false; changed = true; }
                if (!changed || closing) return;
                changed = false; text = english; detail = String.IsNullOrEmpty(english) || showFailure || !translationFinal ? status : ""; show = visible; location = anchor;
                canCopy = translationFinal;
                canReplay = finished && receivedAudio.Length != 0 && tracker.Current != null;
                selectionSpan = selection.Span; selectionDetail = selection.Display;
            }
            replay.Enabled = canReplay;
            toolbar.UpdateReplay(canReplay);
            floating.SetActions(canCopy, canReplay);
            tray.Text = "Mansur 学习后台";
            if (show) floating.Present(text, detail, location); else floating.Hide();
            if (show) {
                if (selectionSpan == null) floating.ClearSelectionStudy();
                else floating.UpdateSelectionStudy(selectionSpan, selectionDetail);
            }
        }
        private void WriteRuntimeStatus(bool force)
        {
            try
            {
                string snapshot; long version;
                lock (gate) { snapshot = runtime.Snapshot(); version = runtime.Version; }
                // Only the existing UI timer or shutdown path reaches the filesystem, outside the broker lock.
                runtimeWriter.TrySave(snapshot, version, force);
            }
            catch (Exception) { }
        }
        protected override void ExitThreadCore()
        {
            lock (gate) closing = true;
            if (refresh != null) refresh.Stop();
            if (worker != null) worker.RequestStop();
            base.ExitThreadCore();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (gate) { if (disposed) return; disposed = true; closing = true; }
                if (refresh != null) { refresh.Stop(); refresh.Dispose(); }
                if (listener != null) listener.Dispose();
                if (control != null) control.Dispose();
                if (lexicons != null) lexicons.Dispose();
                if (player != null) player.Dispose();
                if (systemVoice != null) systemVoice.Dispose();
                selection.Audio.Dispose();
                if (worker != null) worker.Dispose();
                if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
                productIcon.Dispose();
                if (settingsForm != null) settingsForm.Dispose();
                if (toolbar != null) toolbar.Dispose();
                dispatcher.Dispose(); floating.Dispose(); receivedAudio.Dispose();
                lock (gate) runtime.Exit();
                WriteRuntimeStatus(true);
            }
            base.Dispose(disposing);
        }
    }
}
