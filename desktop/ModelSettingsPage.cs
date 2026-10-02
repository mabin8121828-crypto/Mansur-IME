// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal sealed class ModelSettingsPage : TableLayoutPanel
    {
        private readonly IModelSettingsService service;
        private readonly Action applySaved;
        private readonly Action<string> notify;
        private readonly ThemeChoice capability = new ThemeChoice(new[] { "中文翻译", "英文朗读" });
        private readonly TextBox python = PathBox(), llama = PathBox(), translation = PathBox(), speech = PathBox();
        private readonly TextBox apiName = new TextBox { Dock = DockStyle.Top, MaxLength = 60 };
        private readonly TextBox baseUrl = new TextBox { Dock = DockStyle.Top, MaxLength = 2048 };
        private readonly ComboBox apiModel = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 200, IntegralHeight = false, DropDownHeight = 220 };
        private readonly TextBox apiVoice = new TextBox { Dock = DockStyle.Top, MaxLength = 200 };
        private readonly TextBox apiKey = new TextBox { Dock = DockStyle.Top, MaxLength = 8192, UseSystemPasswordChar = true };
        private readonly CheckBox removeKey = new CheckBox { Text = "保存时删除此服务的密钥", AutoSize = true };
        private readonly CheckBox streaming = new CheckBox { Text = "边生成边显示英文", AutoSize = true, AccessibleDescription = "完整生成后才朗读；不支持流式的接口可关闭此项。" };
        private readonly ThemeChoice audioFormat = new ThemeChoice(new[] { "WAV", "PCM" });
        private readonly NumericUpDown sampleRate = new NumericUpDown { Minimum = 8000, Maximum = 96000, Value = 24000, Increment = 1000, Width = 105 };
        private readonly Label keyState = new Label(), active = new Label(), result = new Label(), apiTitle = new Label(), apiDescription = new Label();
        private readonly LinkLabel vendorDocs = new LinkLabel { Text = "查看官方配置说明", AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        private readonly ServiceBrandIcon apiIcon = new ServiceBrandIcon { Width = 34, Height = 34, Margin = new Padding(0, 0, 10, 0) };
        private readonly Button check = ActionButton("检查配置"), fetch = ActionButton("拉取模型");
        private Button addService = ActionButton("添加服务");
        private readonly Button runtimeToggle = ActionButton("运行环境");
        private readonly TableLayoutPanel serviceList = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Margin = new Padding(0, 0, 16, 0) };
        private readonly Panel serviceViewport = new Panel { AutoScroll = true, Height = 450, Dock = DockStyle.Top, Margin = new Padding(0, 0, 12, 0), AccessibleName = "模型服务列表，可滚动" };
        private readonly SettingsCard localCard, apiCard, runtimeCard;
        private readonly TableLayoutPanel speechOptions;
        private readonly List<Button> buttons = new List<Button>();
        private readonly List<Font> fonts = new List<Font>();
        private readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
        private readonly List<ServiceButton> serviceButtons = new List<ServiceButton>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private List<ApiServiceProfile> profiles = ApiServices.Defaults();
        private Dictionary<string, bool> savedKeys = new Dictionary<string, bool>();
        private readonly string[] selected = { "local", "local" };
        private string currentId = "local";
        private int currentCapability;
        private Palette palette;
        private bool loading, busy, saving;
        internal event EventHandler BusyChanged;
        internal bool IsBusy { get { return busy; } }
        internal bool IsSaving { get { return saving; } }
        internal bool CanSave { get { return service != null && !busy; } }
        private string CapabilityName { get { return currentCapability == 0 ? "translation" : "speech"; } }
        private ApiServiceProfile CurrentProfile { get { return currentId == "local" ? null : ApiServices.Find(profiles, currentId, CapabilityName); } }

        internal ModelSettingsPage(IModelSettingsService source, Action applyConfiguration, Action<string> notice)
        {
            service = source; applySaved = applyConfiguration; notify = notice;
            SuspendLayout(); Font = MakeFont(10.5f); AutoSize = true; Dock = DockStyle.Top; ColumnCount = 1; Margin = Padding.Empty;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            capability.Margin = new Padding(0, 0, 0, 4); Controls.Add(capability);
            LabelStyle(active, true); active.Margin = new Padding(0, 3, 0, 18); Controls.Add(active);
            var manager = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 0, 0, 16) };
            manager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); manager.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            serviceList.Margin = Padding.Empty; serviceList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); serviceViewport.Controls.Add(serviceList); manager.Controls.Add(serviceViewport, 0, 0);
            var editors = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Margin = Padding.Empty };
            editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); manager.Controls.Add(editors, 1, 0); Controls.Add(manager);
            localCard = NewCard(); editors.Controls.Add(localCard);
            apiCard = NewCard(); editors.Controls.Add(apiCard);
            LabelStyle(apiTitle); apiTitle.Font = MakeFont(13, true);
            var brandRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8), WrapContents = false };
            apiTitle.Dock = DockStyle.None; apiTitle.Margin = new Padding(0, 5, 0, 0); brandRow.Controls.Add(apiIcon); brandRow.Controls.Add(apiTitle); Add(apiCard, brandRow);
            LabelStyle(apiDescription, true); Add(apiCard, apiDescription);
            Add(apiCard, vendorDocs);
            vendorDocs.LinkClicked += delegate {
                string url = ServicePresets.Docs(currentId);
                if (url.Length == 0) return;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception) { Report("暂时无法打开浏览器，请到厂商官网查看 API 配置说明。"); }
            };
            Field(apiCard, "服务名称", apiName);
            Field(apiCard, "Base URL", baseUrl);
            var modelRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = Padding.Empty };
            modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            var modelField = new SettingsTextField(apiModel) { Margin = new Padding(0, 0, 10, 0) };
            fetch.Margin = Padding.Empty; fetch.Dock = DockStyle.Top; fetch.Height = 40;
            modelRow.Controls.Add(modelField, 0, 0); modelRow.Controls.Add(fetch, 1, 0); Field(apiCard, "模型 ID", modelRow);
            Field(apiCard, "API 密钥", apiKey); LabelStyle(keyState, true); Add(apiCard, keyState); Add(apiCard, removeKey);
            speechOptions = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Margin = Padding.Empty };
            speechOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Field(speechOptions, "音色 ID", apiVoice); Field(speechOptions, "音频格式", audioFormat);
            Field(speechOptions, "采样率（Hz）", sampleRate);
            Add(speechOptions, Hint("WAV 按文件实际采样率播放；PCM 需与服务一致。\n请使用支持 WAV 或 PCM 的朗读模型。"));
            Add(apiCard, speechOptions);
            Add(apiCard, streaming);
            var actionRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(190, 10, 0, 0) };
            check.Margin = new Padding(0, 0, 10, 0); runtimeToggle.Margin = Padding.Empty;
            actionRow.Controls.Add(check); actionRow.Controls.Add(runtimeToggle); Controls.Add(actionRow);
            LabelStyle(result, true); result.Margin = new Padding(190, 8, 0, 16); Controls.Add(result);
            runtimeCard = NewCard(); Heading(runtimeCard, "运行环境", "本地模型与 API 服务共用此运行程序。"); Controls.Add(runtimeCard);
            Field(runtimeCard, "Python 运行程序", FileRow(python, "选择 Python 运行程序", "运行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"));
            buttons.Add(check); buttons.Add(fetch); buttons.Add(addService); buttons.Add(runtimeToggle);
            runtimeToggle.Click += delegate { runtimeCard.Visible = !runtimeCard.Visible; };
            capability.SelectedIndexChanged += delegate {
                if (loading) return; StoreEditor(); currentCapability = capability.SelectedIndex; currentId = selected[currentCapability]; ShowEditor(); ClearCheckResult();
            };
            foreach (Control box in new Control[] { python, llama, translation, speech, apiName, baseUrl, apiModel, apiVoice, apiKey })
                box.TextChanged += delegate { if (!loading) ClearCheckResult(); };
            audioFormat.SelectedIndexChanged += delegate { if (!loading) { UpdateRate(); ClearCheckResult(); } };
            sampleRate.ValueChanged += delegate { if (!loading) ClearCheckResult(); };
            removeKey.CheckedChanged += delegate { if (!loading) { apiKey.Enabled = !removeKey.Checked; if (removeKey.Checked) apiKey.Clear(); ClearCheckResult(); } };
            streaming.CheckedChanged += delegate { if (!loading) ClearCheckResult(); };
            check.Click += async delegate { await CheckAsync(); }; fetch.Click += async delegate { await FetchModelsAsync(); };
            LoadState(); ResumeLayout(true);
        }
        private static TextBox PathBox() { return new TextBox { Dock = DockStyle.Fill, MaxLength = 32760 }; }
        private static Button ActionButton(string text) { return new SettingsActionButton { Text = text, AutoSize = true }; }
        private Font MakeFont(float size, bool bold = false)
        { string key = size.ToString(System.Globalization.CultureInfo.InvariantCulture) + (bold ? "bold" : "regular"); Font value; if (!fontCache.TryGetValue(key, out value)) { value = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular); fonts.Add(value); fontCache.Add(key, value); } return value; }
        private void LabelStyle(Label label, bool muted = false)
        { label.AutoSize = true; label.Dock = DockStyle.Top; label.Font = MakeFont(9.5f); label.Margin = new Padding(0, 0, 0, 8); label.UseMnemonic = false; if (muted) label.Tag = "muted"; }
        private Label Hint(string text) { var label = new Label { Text = text }; LabelStyle(label, true); return label; }
        private static SettingsCard NewCard()
        {
            var card = new SettingsCard { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(18, 16, 18, 12), Margin = Padding.Empty };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return card;
        }
        private void Heading(TableLayoutPanel card, string title, string description)
        { var heading = new Label { Text = title }; LabelStyle(heading); heading.Font = MakeFont(12, true); Add(card, heading); Add(card, Hint(description)); }
        private static void Add(TableLayoutPanel parent, Control child) { parent.Controls.Add(child); }
        private void Field(TableLayoutPanel parent, string label, Control editor)
        {
            var title = new Label { Text = label }; LabelStyle(title); title.Font = Font; title.Margin = new Padding(0, 8, 0, 6);
            editor.Margin = new Padding(0, 0, 0, 7); editor.Dock = DockStyle.Top; editor.AccessibleName = label;
            if (editor is TextBox) editor = new SettingsTextField(editor) { Margin = new Padding(0, 0, 0, 7) };
            else if (editor is NumericUpDown) editor = new SettingsNumberField((NumericUpDown)editor);
            Add(parent, title); Add(parent, editor);
        }
        private Control FileRow(TextBox editor, string title, string filter)
        {
            var row = BrowseRow(editor, out Button browse);
            browse.Click += delegate { using (var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false, RestoreDirectory = true }) if (dialog.ShowDialog(FindForm()) == DialogResult.OK) editor.Text = dialog.FileName; };
            return row;
        }
        private Control FolderRow(TextBox editor)
        {
            var row = BrowseRow(editor, out Button browse);
            browse.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "选择包含 Kokoro 朗读模型的文件夹", ShowNewFolderButton = false }) if (dialog.ShowDialog(FindForm()) == DialogResult.OK) editor.Text = dialog.SelectedPath; };
            return row;
        }
        private Control BrowseRow(TextBox editor, out Button browse)
        {
            var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            var input = new SettingsTextField(editor) { Margin = new Padding(0, 0, 10, 0) };
            browse = new SettingsActionButton { Text = "浏览…", Dock = DockStyle.Top, Height = 40, Margin = Padding.Empty };
            buttons.Add(browse); row.Controls.Add(input, 0, 0); row.Controls.Add(browse, 1, 0); return row;
        }
        private void LoadState()
        {
            loading = true;
            try
            {
                var state = service == null ? new ModelSettingsState { StatusText = "模型设置服务暂未连接，请重新打开设置。" } : service.Read() ?? new ModelSettingsState();
                var draft = state.Draft ?? new ModelSettingsDraft(); profiles = draft.Services.ConvertAll(p => p.Copy(false)); savedKeys = new Dictionary<string, bool>(state.SavedKeys);
                savedKeys[ApiServices.LegacyId] = state.HasSavedApiKey;
                var legacy = ApiServices.Find(profiles, ApiServices.LegacyId, "translation"); if (legacy.Model.Length == 0) legacy.Model = draft.ApiModel ?? "";
                selected[0] = draft.TranslationProvider == "local" ? "local" : draft.TranslationProvider == "openrouter" ? ApiServices.LegacyId : draft.TranslationServiceId;
                selected[1] = draft.SpeechProvider == "local" ? "local" : draft.SpeechServiceId;
                foreach (int index in new[] { 0, 1 }) if (selected[index] != "local" && !profiles.Exists(p => p.Id == selected[index] && p.Capability == (index == 0 ? "translation" : "speech"))) selected[index] = "local";
                python.Text = draft.Python ?? ""; llama.Text = draft.LlamaServer ?? ""; translation.Text = draft.TranslationModel ?? ""; speech.Text = draft.VoiceModelDir ?? "";
                runtimeCard.Visible = python.Text.Length == 0;
                if (capability.SelectedIndex < 0) capability.SelectedIndex = 0; currentCapability = capability.SelectedIndex; currentId = selected[currentCapability]; SetRuntimeState(state);
                result.Text = String.IsNullOrEmpty(state.StatusText) ? "选择服务并填写配置，保存后应用。" : state.StatusText;
            }
            catch (Exception)
            { profiles = ApiServices.Defaults(); selected[0] = selected[1] = "local"; currentId = "local"; capability.SelectedIndex = currentCapability = 0; active.Text = "现有配置暂时无法读取。"; result.Text = "请重新选择模型文件或填写 API 信息，再保存配置。"; }
            finally { loading = false; ShowEditor(); }
        }
        private void StoreEditor()
        {
            if (loading || currentId == "local") return;
            var profile = CurrentProfile; profile.Name = apiName.Text.Trim(); profile.BaseUrl = baseUrl.Text.Trim(); profile.Model = apiModel.Text.Trim();
            profile.NewKey = apiKey.Text.Trim(); profile.RemoveKey = removeKey.Checked; profile.Voice = apiVoice.Text.Trim();
            profile.AudioFormat = audioFormat.SelectedIndex == 1 ? "pcm" : "wav"; profile.SampleRate = (int)sampleRate.Value;
            profile.Stream = streaming.Checked;
        }
        private void AddCustomService()
        {
            if (profiles.Count >= ApiServices.MaximumProfiles) { Report("最多可保存 " + ApiServices.MaximumProfiles + " 个服务。"); return; }
            StoreEditor(); var profile = new ApiServiceProfile { Id = CapabilityName + "-custom-" + Guid.NewGuid().ToString("N").Substring(0, 12), Capability = CapabilityName, Name = "自定义兼容接口", BaseUrl = "https://api.example.com/v1" };
            profiles.Add(profile); SelectService(profile.Id); apiName.Focus(); apiName.SelectAll();
        }
        internal void SelectService(string id)
        { if (busy) return; StoreEditor(); if (id != "local") ApiServices.Find(profiles, id, CapabilityName); selected[currentCapability] = currentId = id; ShowEditor(); ClearCheckResult(); }
        private void ShowEditor()
        {
            loading = true; SuspendLayout(); serviceList.SuspendLayout(); localCard.SuspendLayout(); apiCard.SuspendLayout();
            try
            {
                var oldControls = new List<Control>(); foreach (Control control in serviceList.Controls) if (control != addService) oldControls.Add(control);
                foreach (var control in oldControls) control.Dispose();
                buttons.Remove(addService); addService.Dispose(); addService = ActionButton("添加服务"); addService.Click += delegate { AddCustomService(); }; buttons.Add(addService);
                serviceList.Controls.Clear(); serviceList.RowStyles.Clear(); serviceList.RowCount = 1; serviceButtons.Clear();
                var heading = Hint("服务"); heading.Margin = new Padding(10, 4, 0, 10); serviceList.Controls.Add(heading, 0, 0);
                AddServiceButton("local", "本地模型", currentCapability == 0 ? "离线翻译" : "Kokoro 朗读");
                foreach (var profile in profiles) if (profile.Capability == CapabilityName)
                { bool hasKey; savedKeys.TryGetValue(profile.Id, out hasKey); AddServiceButton(profile.Id, profile.Name, profile.NewKey.Length > 0 || hasKey ? "密钥已配置" : "待配置"); }
                addService.Visible = true; addService.Dock = DockStyle.Top; addService.Margin = new Padding(0, 8, 0, 0);
                serviceList.RowCount = serviceButtons.Count + 2; serviceList.Controls.Add(addService, 0, serviceButtons.Count + 1);
                localCard.Visible = currentId == "local"; apiCard.Visible = currentId != "local"; speechOptions.Visible = currentCapability == 1; streaming.Visible = currentCapability == 0;
                apiKey.Clear(); removeKey.Checked = false; apiModel.Items.Clear();
                if (currentId == "local")
                {
                    // Editors persist across page changes; detach them before rebuilding the local card.
                    foreach (Control box in new Control[] { translation, llama, speech }) if (box.Parent != null) box.Parent.Controls.Remove(box);
                    oldControls.Clear(); foreach (Control control in localCard.Controls) oldControls.Add(control);
                    foreach (var control in oldControls) control.Dispose(); localCard.Controls.Clear(); buttons.RemoveAll(b => b.IsDisposed);
                    if (currentCapability == 0)
                    { Heading(localCard, "本地翻译", "使用已准备好的 GGUF 模型，无需 API 密钥。"); Field(localCard, "翻译模型", FileRow(translation, "选择翻译模型", "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*")); Field(localCard, "翻译运行组件", FileRow(llama, "选择 llama-server 程序", "运行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*")); }
                    else
                    { Heading(localCard, "本地朗读", "使用 Kokoro 离线朗读；声线与语速在“英语朗读”页调整。"); Field(localCard, "朗读模型文件夹", FolderRow(speech)); Add(localCard, Hint("文件夹需包含 kokoro-v1.0.onnx 和 voices-v1.0.bin。")); }
                }
                else
                {
                    var profile = CurrentProfile; apiTitle.Text = profile.Name; apiIcon.ServiceId = profile.Id; apiDescription.Text = currentCapability == 0 ? "只翻译三空格确认的本句中文。" : "将生成的英文交给此服务朗读。";
                    string description = ServicePresets.Description(profile.Id);
                    if (description.Length > 0) apiDescription.Text += "\n" + description;
                    if (!ServicePresets.HasCatalog(profile.Id)) apiDescription.Text += "\n从厂商控制台复制模型 ID；此处只检查配置。";
                    vendorDocs.Visible = ServicePresets.Docs(profile.Id).Length > 0;
                    apiName.Text = profile.Name; baseUrl.Text = profile.BaseUrl; apiModel.Text = profile.Model; apiVoice.Text = profile.Voice;
                    streaming.Checked = profile.Stream;
                    bool customName = profile.Id.Contains("-custom-"); apiName.Visible = customName;
                    Control nameField = apiName.Parent is SettingsTextField ? apiName.Parent : apiName;
                    nameField.Visible = customName;
                    apiCard.Controls[apiCard.Controls.IndexOf(nameField) - 1].Visible = customName;
                    apiKey.Text = profile.NewKey; removeKey.Checked = profile.RemoveKey; apiKey.Enabled = !profile.RemoveKey;
                    audioFormat.SelectedIndex = profile.AudioFormat == "pcm" ? 1 : 0; sampleRate.Value = profile.SampleRate;
                    bool hasKey; savedKeys.TryGetValue(currentId, out hasKey); keyState.Text = hasKey ? "密钥已加密保存；留空保持，输入可替换。" : "按当前用户加密保存；与其他服务独立。";
                }
                check.Text = currentId == "local" ? "检查本地配置" : ServicePresets.HasCatalog(currentId) ? "测试连接" : "检查配置";
                fetch.Enabled = service is IApiServiceSettings && ServicePresets.HasCatalog(currentId); UpdateRate();
                if (palette != null) ApplyPalette(palette);
            }
            finally { serviceList.ResumeLayout(true); localCard.ResumeLayout(true); apiCard.ResumeLayout(true); ResumeLayout(true); loading = false; }
        }
        private void UpdateRate() { sampleRate.Enabled = audioFormat.SelectedIndex == 1 || currentId == "speech-siliconflow" || currentId == "speech-stepfun"; }
        private void AddServiceButton(string id, string name, string detail)
        {
            var button = new ServiceButton { Text = name, Detail = detail, ServiceId = id, Selected = currentId == id, Dock = DockStyle.Top, Height = 52, Margin = new Padding(0, 0, 0, 4), AccessibleName = name + (currentId == id ? "，已选择" : "") };
            button.Click += delegate { SelectService(id); }; serviceButtons.Add(button);
            serviceList.RowCount = serviceButtons.Count + 1; serviceList.Controls.Add(button, 0, serviceButtons.Count);
        }
        private void SetRuntimeState(ModelSettingsState state)
        {
            string name = state.ActiveProvider == "openrouter" ? "OpenRouter 翻译" : state.ActiveProvider == "compatible" ? "API 翻译" : "本地翻译";
            active.Text = (String.IsNullOrEmpty(state.ActiveProvider) ? "" : "当前后台：" + name + (state.RestartRequired ? " · 更改尚未应用" : "") + "\n") + state.StatusText;
        }
        internal void RefreshRuntimeState()
        { if (service == null || IsDisposed) return; try { var state = service.Read(); if (state != null) SetRuntimeState(state); } catch (Exception) { active.Text = "暂时无法确认学习后台状态。"; } }
        private void ClearCheckResult() { Report("有未保存的更改。点击“保存并应用”后生效。"); }
        internal ModelSettingsDraft CaptureDraft()
        {
            StoreEditor(); var legacy = ApiServices.Find(profiles, ApiServices.LegacyId, "translation");
            bool legacySelected = selected[0] == ApiServices.LegacyId && legacy.BaseUrl.TrimEnd('/') == "https://openrouter.ai/api/v1";
            return new ModelSettingsDraft { TranslationProvider = selected[0] == "local" ? "local" : legacySelected ? "openrouter" : "compatible",
                SpeechProvider = selected[1] == "local" ? "local" : "compatible", TranslationServiceId = selected[0] == "local" ? ApiServices.LegacyId : selected[0],
                SpeechServiceId = selected[1] == "local" ? "speech-openai" : selected[1], Services = profiles.ConvertAll(p => p.Copy()), Python = python.Text,
                LlamaServer = llama.Text, TranslationModel = translation.Text, VoiceModelDir = speech.Text, ApiModel = legacy.Model,
                NewApiKey = legacy.NewKey, RemoveApiKey = legacy.RemoveKey };
        }
        private void SetBusy(bool value, bool isSaving = false)
        { busy = value; saving = value && isSaving; foreach (Control child in Controls) child.Enabled = !value; check.Enabled = service != null && !value; if (BusyChanged != null) BusyChanged(this, EventArgs.Empty); }
        private void Report(string message) { result.Text = message; if (notify != null) notify(message); }
        internal async Task FetchModelsAsync()
        { await CheckOrFetch(true); }
        internal async Task CheckAsync()
        { await CheckOrFetch(false); }
        private async Task CheckOrFetch(bool fillModels)
        {
            if (busy || service == null) return;
            var draft = CaptureDraft(); var profile = CurrentProfile == null ? null : CurrentProfile.Copy(); var api = service as IApiServiceSettings;
            SetBusy(true); Report(profile == null ? "正在检查本地配置…" : !ServicePresets.HasCatalog(profile.Id) ? "正在检查配置…" : fillModels ? "正在拉取模型…" : "正在测试连接…");
            try
            {
                if (profile != null && api != null)
                {
                    var ids = await Task.Run(() => api.FetchModelsAsync(profile, lifetime.Token));
                    if (!IsDisposed && !lifetime.IsCancellationRequested)
                    { if (fillModels) { string text = apiModel.Text; apiModel.Items.Clear(); apiModel.Items.AddRange(ids); apiModel.Text = text; }
                      Report(!ServicePresets.HasCatalog(profile.Id) ? "配置检查通过。未验证网络、密钥有效性或模型权限；实际结果以确认输入后的调用为准。" : ids.Length == 0 ? "连接成功，但未提供模型目录。请手填模型 ID；实际能力以使用结果为准。" : "连接成功，已读取 " + ids.Length + " 个模型。目录可能包含其他能力；请选择翻译或朗读模型。"); }
                }
                else
                { var answer = await Task.Run(() => service.CheckAsync(draft, lifetime.Token)); if (!IsDisposed && !lifetime.IsCancellationRequested) Report(answer == null ? "检查未完成，请重试。" : answer.Message); }
            }
            catch (OperationCanceledException) { if (!IsDisposed) Report("检查已取消。"); }
            catch (ModelConfigurationException error) { if (!IsDisposed) Report(ModelSettingsService.Message(error.Code)); }
            catch (Exception) { if (!IsDisposed) Report("检查未完成，请核对服务地址、密钥和网络。目录不可用时仍可手填模型。"); }
            finally { ClearSecrets(draft); if (profile != null) profile.NewKey = ""; if (!IsDisposed) SetBusy(false); }
        }
        private static void ClearSecrets(ModelSettingsDraft draft) { draft.NewApiKey = ""; foreach (var profile in draft.Services) profile.NewKey = ""; }
        internal async Task SaveAsync()
        {
            if (busy || service == null) return;
            var draft = CaptureDraft(); SetBusy(true, true); Report("正在保存配置…");
            try
            {
                var answer = await Task.Run(() => service.Save(draft));
                if (IsDisposed) return;
                if (answer == null || !answer.Success) { Report(answer == null ? "保存未完成，请重试。" : answer.Message); return; }
                apiKey.Clear(); foreach (var profile in profiles) profile.NewKey = ""; removeKey.Checked = false;
                if (applySaved == null) { LoadState(); Report("配置已保存。重新启动学习后台后生效。"); }
                else { try { applySaved(); LoadState(); Report("配置已保存并开始应用，学习后台正在准备。"); } catch (Exception) { LoadState(); Report("配置已保存，但学习后台尚未重新启用。请重试保存并应用。"); } }
            }
            catch (Exception) { if (!IsDisposed) Report("配置未保存，请核对文件和当前账户的写入权限。"); }
            finally { ClearSecrets(draft); if (!IsDisposed) SetBusy(false); }
        }
        internal void ApplyPalette(Palette colors)
        {
            palette = colors; Color surface = colors.Background.GetBrightness() < .5f ? Color.FromArgb(36, 44, 56) : Color.White;
            ApplyColors(this, colors, colors.Background, surface); capability.Apply(colors, colors.Background); audioFormat.Apply(colors, surface);
            Color edge = colors.Background.GetBrightness() < .5f ? Color.FromArgb(64, 77, 95) : Color.FromArgb(229, 233, 239);
            foreach (Button button in buttons) if (!button.IsDisposed) ((SettingsActionButton)button).Apply(surface, colors.Accent, edge);
            foreach (var button in serviceButtons) button.Apply(colors, colors.Background);
            apiIcon.Apply(colors); vendorDocs.LinkColor = vendorDocs.ActiveLinkColor = vendorDocs.VisitedLinkColor = colors.Accent;
        }
        private static void ApplyColors(Control control, Palette colors, Color background, Color surface)
        {
            Color edge = colors.Background.GetBrightness() < .5f ? Color.FromArgb(64, 77, 95) : Color.FromArgb(229, 233, 239);
            var card = control as SettingsCard; if (card != null) { background = surface; card.BorderColor = edge; }
            control.BackColor = background; control.ForeColor = Object.Equals(control.Tag, "muted") ? colors.Muted : colors.Foreground;
            var text = control as SettingsTextField; if (text != null) { background = surface; text.Apply(surface, edge, colors.Accent); }
            var number = control as SettingsNumberField; if (number != null) { background = surface; number.Apply(surface, edge); }
            foreach (Control child in control.Controls) ApplyColors(child, colors, background, surface);
        }
        internal void CancelCheck() { if (!saving) lifetime.Cancel(); }
        protected override void Dispose(bool disposing)
        { if (disposing) { lifetime.Cancel(); foreach (var profile in profiles) profile.NewKey = ""; } base.Dispose(disposing); if (disposing) { foreach (var font in fonts) font.Dispose(); fonts.Clear(); lifetime.Dispose(); } }
    }
}
