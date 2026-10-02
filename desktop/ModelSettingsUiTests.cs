// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class ModelSettingsUiTests
    {
        // A deterministic owner-thread pump, with no shown windows or production endpoints.
        private sealed class Pump : SynchronizationContext, IDisposable
        {
            private readonly Queue<Action> pending = new Queue<Action>();
            private readonly AutoResetEvent available = new AutoResetEvent(false);
            public override void Post(SendOrPostCallback callback, object value) { lock (pending) pending.Enqueue(() => callback(value)); available.Set(); }
            internal void Wait(Task task)
            {
                var deadline = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (deadline.ElapsedMilliseconds > 5000) throw new InvalidOperationException("model-ui-test-timeout");
                    Action action = null; lock (pending) if (pending.Count > 0) action = pending.Dequeue();
                    if (action != null) action(); else available.WaitOne(10);
                }
                task.GetAwaiter().GetResult();
            }
            public void Dispose() { available.Dispose(); }
        }
        private class FakeService : IModelSettingsService
        {
            internal int Checks, Saves;
            internal bool FailSave, FailRead, DeferredCheck;
            internal string ActiveProvider = "local";
            internal ModelSettingsDraft Saved;
            internal TaskCompletionSource<ModelSettingsResult> Pending;
            public ModelSettingsState Read()
            {
                if (FailRead) throw new FormatException("test bad configuration");
                return new ModelSettingsState { HasSavedApiKey = true, ActiveProvider = ActiveProvider, Draft = Saved == null ?
                    new ModelSettingsDraft { Python = @"D:\模型\python.exe", LlamaServer = @"D:\模型\llama-server.exe", TranslationModel = @"D:\模型\model.gguf", VoiceModelDir = @"D:\模型\voice" } : WithoutKey(Saved) };
            }
            private static ModelSettingsDraft WithoutKey(ModelSettingsDraft input) { var copy = input.Copy(); copy.NewApiKey = ""; foreach (var p in copy.Services) { p.NewKey = ""; p.RemoveKey = false; } return copy; }
            public ModelSettingsResult Validate(ModelSettingsDraft draft) { return new ModelSettingsResult { Success = true }; }
            public ModelSettingsResult Save(ModelSettingsDraft draft)
            {
                Saves++; if (FailSave) return new ModelSettingsResult { Success = false, Message = "请选择存在的模型文件。" };
                Saved = draft.Copy(); return new ModelSettingsResult { Success = true, RestartRequired = true, Message = "已保存。" };
            }
            public Task<ModelSettingsResult> CheckAsync(ModelSettingsDraft draft, CancellationToken token)
            {
                Checks++;
                if (!DeferredCheck) return Task.FromResult(new ModelSettingsResult { Success = true, Message = "检查通过。" });
                Pending = new TaskCompletionSource<ModelSettingsResult>(); token.Register(() => Pending.TrySetCanceled()); return Pending.Task;
            }
        }
        private sealed class ApiFakeService : FakeService, IApiServiceSettings
        {
            internal int Fetches;
            internal bool Block;
            public async Task<string[]> FetchModelsAsync(ApiServiceProfile profile, CancellationToken token)
            { Fetches++; if (Block) await Task.Delay(5000, token); token.ThrowIfCancellationRequested(); return new[] { "fixed/translation-model", "fixed/speech-model" }; }
        }
        private static T Field<T>(ModelSettingsPage page, string name)
        { return (T)typeof(ModelSettingsPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page); }
        internal static void Run(Action<bool, string> check)
        {
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            var previousContext = SynchronizationContext.Current;
            using (var pump = new Pump())
            {
                WindowsFormsSynchronizationContext.AutoInstall = false; SynchronizationContext.SetSynchronizationContext(pump);
                try
                {
                    var fake = new FakeService(); int applied = 0; string notice = "";
                    using (var page = new ModelSettingsPage(fake, () => applied++, text => notice = text))
                    {
                        check(fake.Checks == 0 && fake.Saves == 0, "model-ui-open-does-not-check-network-or-save");
                        check(Field<TextBox>(page, "apiKey").UseSystemPasswordChar && Field<TextBox>(page, "apiKey").Text.Length == 0, "model-ui-saved-key-not-returned-to-masked-editor");
                        page.SelectService(ApiServices.LegacyId);
                        Field<ComboBox>(page, "apiModel").Text = "test/model";
                        Field<TextBox>(page, "apiKey").Text = "fixed-test-secret";
                        check(fake.Checks == 0 && fake.Saves == 0 && page.CaptureDraft().TranslationProvider == "openrouter", "model-ui-edit-and-provider-change-have-no-side-effects");
                        pump.Wait(page.CheckAsync());
                        check(fake.Checks == 1 && fake.Saves == 0 && applied == 0 && notice == "检查通过。", "model-ui-explicit-check-does-not-save-or-apply");
                        fake.FailSave = true; pump.Wait(page.SaveAsync());
                        check(applied == 0 && Field<TextBox>(page, "apiKey").Text.Length > 0 && notice == "请选择存在的模型文件。", "model-ui-failed-save-keeps-edits-and-does-not-apply");
                        fake.FailSave = false; pump.Wait(page.SaveAsync());
                        check(applied == 1 && fake.Saved.TranslationProvider == "openrouter" && fake.Saved.ApiModel == "test/model" && fake.Saved.Python.Contains("模型"), "model-ui-saves-real-fields-before-single-apply-callback");
                        check(Field<TextBox>(page, "apiKey").Text.Length == 0 && !notice.Contains("fixed-test-secret"), "model-ui-success-clears-transient-secret-and-message-is-safe");
                        Field<CheckBox>(page, "removeKey").Checked = true;
                        check(page.CaptureDraft().RemoveApiKey && page.CaptureDraft().NewApiKey.Length == 0 && !Field<TextBox>(page, "apiKey").Enabled, "model-ui-key-removal-is-explicit");
                        Field<CheckBox>(page, "removeKey").Checked = false;
                        check(!page.CaptureDraft().RemoveApiKey && page.CaptureDraft().NewApiKey.Length == 0, "model-ui-empty-key-keeps-existing-key");
                        Field<ComboBox>(page, "apiModel").Text = "unsaved/new-model"; string priorResult = notice;
                        fake.ActiveProvider = "openrouter"; page.RefreshRuntimeState();
                        check(Field<Label>(page, "active").Text.Contains("OpenRouter") && page.CaptureDraft().ApiModel == "unsaved/new-model" && notice == priorResult, "model-ui-runtime-refresh-retains-unsaved-draft-and-check-message");
                    }
                    using (var page = new ModelSettingsPage(new FakeService { FailRead = true }, null, text => notice = text))
                    { check(page.CanSave && page.CaptureDraft().TranslationProvider == "local" && Field<Label>(page, "result").Text.Contains("重新选择"), "model-ui-bad-configuration-remains-editable"); }
                    fake = new FakeService { DeferredCheck = true };
                    using (var page = new ModelSettingsPage(fake, null, text => notice = text))
                    {
                        Task pending = page.CheckAsync(); pump.Wait(page.CheckAsync());
                        check(page.IsBusy && !page.CanSave, "model-ui-check-is-single-flight");
                        page.CancelCheck(); pump.Wait(pending);
                        check(!page.IsBusy && fake.Checks == 1 && fake.Saves == 0 && notice == "检查已取消。", "model-ui-closing-cancels-check-without-saving");
                    }
                    using (var page = new ModelSettingsPage(new FakeService(), () => { throw new InvalidOperationException(); }, text => notice = text))
                    { pump.Wait(page.SaveAsync()); check(notice.Contains("已保存") && notice.Contains("尚未"), "model-ui-apply-failure-does-not-claim-active"); }
                    fake = new ApiFakeService();
                    using (var page = new ModelSettingsPage(fake, null, text => notice = text))
                    {
                        page.SelectService("translation-openai"); Field<ComboBox>(page, "apiModel").Text = "existing/model"; Field<TextBox>(page, "apiKey").Text = "fixed-existing-key";
                        foreach (string brand in new[] { "qwen", "zhipu", "kimi", "doubao", "minimax", "tokenhub", "hunyuan", "baidu", "stepfun", "spark" })
                        {
                            page.SelectService("translation-" + brand);
                            var draft = page.CaptureDraft();
                            check(draft.TranslationServiceId == "translation-" + brand && draft.SpeechProvider == "local" && Field<TextBox>(page, "apiKey").Text.Length == 0 &&
                                Field<Button>(page, "fetch").Enabled == ServicePresets.HasCatalog("translation-" + brand), "model-ui-domestic-entry-does-not-borrow-key-or-change-speech-" + brand);
                        }
                        page.SelectService("translation-openai");
                        check(Field<ComboBox>(page, "apiModel").Text == "existing/model" && Field<TextBox>(page, "apiKey").Text == "fixed-existing-key" && fake.Saves == 0 && fake.Checks == 0,
                            "model-ui-domestic-navigation-preserves-existing-draft-and-makes-no-network-or-save");
                    }
                    fake = new FakeService();
                    using (var page = new ModelSettingsPage(fake, null, text => notice = text))
                    {
                        page.SelectService("translation-openai");
                        Field<ComboBox>(page, "apiModel").Text = "translation-test";
                        Field<TextBox>(page, "apiKey").Text = "fixed-translation-test-key";
                        Field<ThemeChoice>(page, "capability").SelectedIndex = 1;
                        page.SelectService("speech-siliconflow");
                        Field<ComboBox>(page, "apiModel").Text = "speech-test";
                        Field<TextBox>(page, "apiVoice").Text = "speech-test:voice";
                        Field<TextBox>(page, "apiKey").Text = "fixed-speech-test-key";
                        var draft = page.CaptureDraft();
                        check(draft.TranslationServiceId == "translation-openai" && draft.SpeechServiceId == "speech-siliconflow" && draft.TranslationProvider == "compatible" && draft.SpeechProvider == "compatible", "model-ui-two-capabilities-select-providers-independently");
                        Field<ThemeChoice>(page, "capability").SelectedIndex = 0;
                        check(Field<TextBox>(page, "apiKey").Text == "fixed-translation-test-key" && Field<ComboBox>(page, "apiModel").Text == "translation-test", "model-ui-provider-switch-retains-only-own-key-and-model");
                        check(fake.Checks == 0 && fake.Saves == 0, "model-ui-switching-capabilities-never-connects-or-saves");
                        page.SelectService("local"); Field<ThemeChoice>(page, "capability").SelectedIndex = 1;
                        draft = page.CaptureDraft();
                        check(draft.TranslationProvider == "local" && draft.SpeechProvider == "compatible" && draft.Services.Find(p => p.Id == "translation-openai").Model == "translation-test", "model-ui-local-switch-retains-dormant-cloud-configuration");
                        pump.Wait(page.SaveAsync());
                        check(Field<TextBox>(page, "apiKey").Text.Length == 0 && page.CaptureDraft().Services.TrueForAll(p => p.NewKey.Length == 0), "model-ui-save-clears-both-capability-transient-keys");
                    }
                    var api = new ApiFakeService();
                    using (var page = new ModelSettingsPage(api, null, text => notice = text))
                    {
                        page.SelectService("translation-openai"); Field<ComboBox>(page, "apiModel").Text = "manual/model";
                        pump.Wait(page.FetchModelsAsync());
                        check(api.Fetches == 1 && api.Saves == 0 && Field<ComboBox>(page, "apiModel").Items.Count == 2 && page.CaptureDraft().Services.Find(p => p.Id == "translation-openai").Model == "manual/model", "model-ui-explicit-fetch-populates-list-without-replacing-manual-model-or-saving");
                        pump.Wait(page.CheckAsync());
                        check(api.Fetches == 2 && notice.Contains("目录") && !notice.Contains("翻译通过"), "model-ui-connection-check-does-not-claim-inference");
                        Field<Button>(page, "addService").PerformClick();
                        var draft = page.CaptureDraft(); var custom = draft.Services.Find(p => p.Id == draft.TranslationServiceId);
                        check(draft.TranslationServiceId.StartsWith("translation-custom-") && draft.SpeechProvider == "local" && custom.BaseUrl == "https://api.example.com/v1" && api.Fetches == 2, "model-ui-add-custom-provider-is-independent-and-offline");
                        var nameEditor = Field<TextBox>(page, "apiName");
                        bool customShown = nameEditor.Visible && nameEditor.Parent.Visible;
                        page.SelectService("translation-openai"); bool builtinHidden = !nameEditor.Parent.Visible;
                        page.SelectService(custom.Id);
                        check(customShown && builtinHidden && nameEditor.Visible && nameEditor.Parent.Visible, "model-ui-custom-name-field-restores-after-switching-built-in-service");
                        var add = Field<Button>(page, "addService"); var list = (TableLayoutPanel)add.Parent;
                        check(list.GetRow(add) == draft.Services.FindAll(p => p.Capability == "translation").Count + 2 && list.RowCount == list.Controls.Count, "model-ui-service-rebuild-keeps-add-button-in-last-real-row");
                    }
                    api = new ApiFakeService { Block = true };
                    using (var page = new ModelSettingsPage(api, null, text => notice = text))
                    {
                        page.SelectService("translation-openai"); Task pending = page.FetchModelsAsync();
                        page.CancelCheck(); pump.Wait(pending);
                        check(!page.IsBusy && api.Saves == 0 && notice == "检查已取消。", "model-ui-api-catalog-cancellation-restores-controls-without-save");
                    }
                }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall; }
            }
        }
    }
}
