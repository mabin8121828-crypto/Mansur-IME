// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal sealed class SettingsForm : Form
    {
        private readonly SettingsStore store;
        private readonly LexiconManager lexicons;
        private readonly Action changed;
        private readonly IStartupService startupService;
        private readonly ThemeChoice theme = new ThemeChoice(new[] { "跟随系统", "浅色", "深色" }, true);
        private readonly ThemeChoice layout = Choice("横向", "纵向");
        private readonly ThemeChoice voice = Choice("Heart · 美式女声", "Bella · 美式女声", "Michael · 美式男声", "Emma · 英式女声");
        private readonly NumericUpDown fontSize = new NumericUpDown { Minimum = 10, Maximum = 20, Value = 12, Width = 90 };
        private readonly NumericUpDown speed = new NumericUpDown { Minimum = 75, Maximum = 125, Value = 100, Increment = 5, Width = 90 };
        private readonly CheckBox abbreviation = new CheckBox { Text = "启用简拼与混拼", AutoSize = true };
        private readonly CheckBox englishSuggestions = new CheckBox { Name = "EnglishSuggestions", Text = "英文模式提供常用词补全", AutoSize = true };
        private readonly CheckBox autoRemember = new CheckBox { Name = "AutoRemember", Text = "记住常用字词", AutoSize = true,
            AccessibleName = "记住常用字词", AccessibleDescription = "只在本机保存已确认的字词和使用次数；取消或删改的草稿不保存。关闭后保留已有记录。" };
        private readonly CheckBox toolbar = new CheckBox { Text = "显示悬浮状态栏", AutoSize = true };
        private readonly CheckBox loginStartup = new CheckBox { Text = "登录 Windows 后启动英文伴读", AutoSize = true };
        private readonly CheckBox systemFallback = new CheckBox { Name = "SystemSpeechFallback", Text = "朗读失败时使用 Windows 英语声音", AutoSize = true };
        private readonly Label startupStatus = new Label { AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false, Tag = "muted" };
        private readonly CheckBox[] fuzzy = new CheckBox[8];
        private readonly Label notice = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "设置只保存在这台电脑。\n调整后点击“保存设置”生效。" };
        private readonly Button import = new SettingsActionButton { Text = "导入词库…", AutoSize = true }, export = new SettingsActionButton { Text = "导出用户词库…", AutoSize = true };
        private readonly Panel sidebar = new Panel { Dock = DockStyle.Left, Width = 218, Padding = new Padding(18, 26, 18, 20) };
        private readonly Panel content = new Panel { Dock = DockStyle.Fill };
        private readonly Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 82, Padding = new Padding(30, 14, 30, 14) };
        private readonly List<Panel> pages = new List<Panel>();
        private readonly List<SettingsNavigationButton> navigation = new List<SettingsNavigationButton>();
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly Icon productIcon = ProductIcon.Create(SystemInformation.IconSize);
        private readonly Button save = new SettingsActionButton { Text = "保存设置", Width = 112, Height = 40 };
        private readonly Button close = new SettingsActionButton { Text = "关闭", Width = 78, Height = 40 };
        private readonly ModelSettingsPage models;
        private const int ModelPageIndex = 4;
        private string modelNotice = "模型配置保存在本机。\n保存并应用会重新准备学习服务。";
        private int selectedPage;
        private bool loading;
        private bool busy;
        private bool preferencesSaving, startupConfigured, startupCanChange;
        internal SettingsForm(SettingsStore settings, LexiconManager manager, Action onChanged, IModelSettingsService modelSettings = null, Action applyModelSettings = null, IStartupService startup = null)
        {
            store = settings; lexicons = manager; changed = onChanged; startupService = startup;
            Text = "Mansur 设置 · " + PackageVersion(); StartPosition = FormStartPosition.CenterScreen;
            Icon = productIcon;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(800, 640); ClientSize = new Size(960, 744);
            Font = OwnFont(10.5f); ShowInTaskbar = true; KeyPreview = true;
            var brand = new SettingsBrandHeader();
            var menu = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 276, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            string[] names = { "外观", "拼音输入", "英语朗读", "个人词库", "模型与 API" };
            string[] icons = { "palette", "keyboard", "volume-2", "book-open", "cpu" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var button = new SettingsNavigationButton { Text = names[i], IconName = icons[i], Width = 182, Height = 44, Margin = new Padding(0, 0, 0, 8), TabIndex = i, AccessibleName = names[i] + "设置页" };
                button.Click += delegate { SelectPage(index); };
                button.KeyDown += delegate(object sender, KeyEventArgs e) {
                    if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;
                    int next = (index + (e.KeyCode == Keys.Up ? names.Length - 1 : 1)) % names.Length;
                    SelectPage(next); navigation[next].Focus(); e.Handled = e.SuppressKeyPress = true;
                };
                navigation.Add(button); menu.Controls.Add(button);
            }
            var version = TextLabel("本地版本\n" + PackageVersion(), 8.5f); version.Dock = DockStyle.Bottom; version.Height = 45; version.AutoSize = false; version.Tag = "muted";
            sidebar.Controls.Add(menu); sidebar.Controls.Add(brand); sidebar.Controls.Add(version);
            var appearance = Page("外观", "调整候选、英文学习窗和状态栏，让日常输入更清楚。" );
            var appearanceCard = Card(appearance, "主题与候选", "所有软件使用同一套外观。选择主题可在此预览，保存后应用。" );
            Field(appearanceCard, "显示主题", theme); Field(appearanceCard, "候选排列", layout);
            Field(appearanceCard, "文字大小", ValueWithUnit(fontSize, "磅  ·  适用于候选与英文学习窗"));
            var toolbarCard = Card(appearance, "悬浮状态栏", "快速切换中英模式、重播英文或打开设置。" );
            Add(toolbarCard, toolbar); Add(toolbarCard, Hint("拖动左侧 ⋮ 调整位置；点击 × 隐藏，也可从托盘重新显示。"));
            var input = Page("拼音输入", "保留全拼习惯，也可以用简拼、混拼和适合自己的模糊音。" );
            var spellingCard = Card(input, "拼音习惯", "完整拼音优先，简拼作为额外候选。" );
            Add(spellingCard, abbreviation); Add(spellingCard, Hint("例如：nh 或 nhao 可以输入“你好”。"));
            autoRemember.Margin = new Padding(0, 12, 0, 4); Add(spellingCard, autoRemember);
            Add(spellingCard, Hint("只在本机保存已确认的字词和使用次数；\n取消或删改的草稿不保存。关闭后保留已有记录。"));
            var fuzzyCard = Card(input, "模糊音", "只勾选你需要的组合，默认全部关闭。" );
            var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
            string[] fuzzyNames = { "z / zh", "c / ch", "s / sh", "n / l", "f / h", "an / ang", "en / eng", "in / ing" };
            for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            for (int i = 0; i < 8; i++) { fuzzy[i] = new CheckBox { Text = fuzzyNames[i], AutoSize = true, Margin = new Padding(0, 5, 8, 9) }; grid.Controls.Add(fuzzy[i], i % 4, i / 4); }
            Add(fuzzyCard, grid);
            var keysCard = Card(input, "选词与快捷操作", null);
            Add(keysCard, Hint("空格 / 数字 / 鼠标选词；- 或 PageUp 上一页，= / + / PageDown 下一页。\n轻按 Shift 切换中英文；英文模式保留大小写与空格。Esc 取消草稿。\n中文模式未选词时，Enter 保留字母；再按 Enter 只提交。"));
            var learning = Page("英语朗读", "中文翻译成英文；直接输入英文时，显示原文并朗读。" );
            var englishCard = Card(input, "英文输入", "轻按 Shift 切换中英文；姓名、大小写、数字和标点保持原样。" );
            Add(englishCard, englishSuggestions);
            Add(englishCard, Hint("默认关闭。开启后输入两个字母可显示常用词建议。\nTab 补全首项，也可点选；空格、数字和 Enter 不自动改词。\n这是基础前缀补全，陌生姓名及网址不作纠正。"));
            var learningCard = Card(learning, "声音偏好", "以下声线用于本地朗读；API 音色在“模型与 API”页配置。语速从下一句生效。" );
            Field(learningCard, "朗读声线", voice); Field(learningCard, "朗读速度", ValueWithUnit(speed, "%  ·  100% 为正常速度"));
            var gestureCard = Card(learning, "把一句话变成学习机会", null);
            Add(gestureCard, TextLabel("输入完成  →  快速按三次空格", 12, true));
            Add(gestureCard, Hint("三空格提交并朗读；英文保留原文，不经过翻译。\nEnter 只提交，不朗读；发送消息仍用软件的发送操作。"));
            var copyCard = Card(learning, "复制英文", "英文浮窗中的文字可选择并复制。" );
            Add(copyCard, Hint("鼠标拖选后按 Ctrl+C，或右键复制所选英文。\n浮窗的复制图标复制整句，重播图标重播本句。\n播放完成 3 秒后收起；悬停或选中文字时保持显示。"));
            var recoveryCard = Card(learning, "备用朗读", "模型或 API 朗读故障时，英文仍可查看和复制。" );
            Add(recoveryCard, systemFallback);
            Add(recoveryCard, Hint("默认关闭。开启后仅在朗读失败且尚未播放音频时使用。\n需已安装 Windows 英语声音，音色会不同；不发送额外云端请求。"));
            var startupCard = Card(learning, "登录后自动启动", "让英文伴读在登录这台电脑后自动准备；首次安装默认关闭。" );
            Add(startupCard, loginStartup); startupStatus.Font = OwnFont(9.5f); startupStatus.Margin = new Padding(0, 8, 0, 8); Add(startupCard, startupStatus);
            Add(startupCard, Hint("只为当前 Windows 账户启动英文伴读。Windows 可能延后或禁用启动项，\n可在系统“启动应用”中检查。勾选后点击“保存设置”登记。"));
            loginStartup.CheckedChanged += delegate { if (!loading) startupStatus.Text = "此选项有未保存的更改；点击“保存设置”后生效。"; };
            var dictionary = Page("个人词库", "加入常用词、地域词或专业词，让候选更贴近你的日常。" );
            var dictionaryCard = Card(dictionary, "导入与备份", "导入会合并现有用户词库；导出可留作备份或带到另一台电脑。" );
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
            import.Margin = new Padding(0, 2, 12, 6); export.Margin = new Padding(0, 2, 0, 6); import.Padding = export.Padding = new Padding(14, 8, 14, 8);
            actions.Controls.Add(import); actions.Controls.Add(export); Add(dictionaryCard, actions);
            Add(dictionaryCard, Hint("支持多选文件；同词合并并保留较高词频。导出只包含用户词库。"));
            var formatCard = Card(dictionary, "支持的文件", null);
            Add(formatCard, TextLabel("UTF-8 TSV", 10.5f, true));
            Add(formatCard, Hint("拼音、文字、词频、分节拼音，用 Tab 分隔。\n示例：nihao  /  你好  /  10000  /  ni hao"));
            Add(formatCard, TextLabel("Rime 词库（.dict.yaml）", 10.5f, true));
            Add(formatCard, Hint("读取带拼音的数据区，不自动导入引用的其他词库。\n每个文件最多 4 MiB，合并后最多 10,000 条用户词。"));
            var activationCard = Card(dictionary, "导入后如何生效", null);
            Add(activationCard, Hint("完成当前输入，再切到其他输入法后切回。\n只有编译成功才更新词库；失败时原词库保持可用。"));
            var modelPage = Page("模型与 API", "翻译与朗读分别选择服务；本地模型和在线接口可以自由组合。" );
            models = new ModelSettingsPage(modelSettings, applyModelSettings, message => { modelNotice = message; if (selectedPage == ModelPageIndex) notice.Text = message; });
            models.BusyChanged += delegate { UpdateSaveButton(); };
            Add(modelPage, models);
            var footerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            notice.Font = OwnFont(9); notice.Margin = new Padding(0, 0, 20, 0); notice.AccessibleName = "设置操作结果";
            var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false, Margin = Padding.Empty };
            close.Margin = new Padding(0, 0, 10, 0); save.Margin = Padding.Empty; buttons.Controls.Add(close); buttons.Controls.Add(save);
            save.Click += async delegate { if (selectedPage == ModelPageIndex) await models.SaveAsync(); else await SavePreferencesAsync(); }; close.Click += delegate { Close(); }; CancelButton = close;
            footerLayout.Controls.Add(notice, 0, 0); footerLayout.Controls.Add(buttons, 1, 0); footer.Controls.Add(footerLayout);
            var main = new Panel { Dock = DockStyle.Fill }; main.Controls.Add(content); main.Controls.Add(footer);
            Controls.Add(main); Controls.Add(sidebar);
            theme.SelectedIndexChanged += delegate { if (!loading) { var p = store.Read(); p.Theme = theme.SelectedIndex; ApplyColors(Palette.From(p)); } };
            import.Click += async delegate { await Import(); }; export.Click += async delegate { await Export(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if ((busy || preferencesSaving || models.IsSaving) && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; notice.Text = models.IsSaving ? "正在保存模型配置，请稍候。" : preferencesSaving ? "正在保存设置，请稍候。" : "正在处理词库，请稍候。"; }
                else models.CancelCheck();
            };
            LoadStartupState(); LoadPreferences(); SelectPage(0);
        }
        private static ThemeChoice Choice(params string[] labels) { return new ThemeChoice(labels); }
        private static string PackageVersion()
        {
            try
            {
                string path = Path.Combine(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")), "manifest.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 65536) return "开发版";
                string version = Json.String(Json.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8), 65536), "version", 64);
                return System.Text.RegularExpressions.Regex.IsMatch(version, @"\A[A-Za-z0-9.\-]{1,64}\z") ? version : "开发版";
            }
            catch (Exception error) when (Expected(error)) { return "开发版"; }
        }
        private Font OwnFont(float size, bool bold = false)
        {
            var font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular); ownedFonts.Add(font); return font;
        }
        private Label TextLabel(string text, float size = 10, bool bold = false)
        {
            return new Label { Text = text, AutoSize = true, Font = OwnFont(size, bold), Margin = new Padding(0, 0, 0, 6), Dock = DockStyle.Top, UseMnemonic = false };
        }
        private Label Hint(string text) { var label = TextLabel(text, 9.5f); label.Tag = "muted"; return label; }
        private TableLayoutPanel Page(string title, string description)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(30, 28, 30, 10), Visible = false };
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.Controls.Add(TextLabel(title, 21, true)); var subtitle = Hint(description); subtitle.Margin = new Padding(0, 3, 0, 22); table.Controls.Add(subtitle);
            page.Controls.Add(table); pages.Add(page); content.Controls.Add(page); return table;
        }
        private SettingsCard Card(TableLayoutPanel page, string title, string description)
        {
            var card = new SettingsCard { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(20, 16, 20, 12), Margin = new Padding(0, 0, 0, 12) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Add(card, TextLabel(title, 12, true));
            if (description != null) { var hint = Hint(description); hint.Margin = new Padding(0, 0, 0, 12); Add(card, hint); }
            page.Controls.Add(card); return card;
        }
        private static void Add(TableLayoutPanel panel, Control control) { panel.Controls.Add(control); }
        private void Field(TableLayoutPanel panel, string label, Control control)
        {
            var title = TextLabel(label, 9.5f, true); title.Margin = new Padding(0, 8, 0, 6); Add(panel, title);
            control.Margin = new Padding(0, 0, 0, 3); control.Dock = DockStyle.Top; Add(panel, control);
        }
        private Control ValueWithUnit(NumericUpDown value, string unit)
        {
            value.AccessibleName = value == fontSize ? "文字大小，磅" : "朗读速度，百分比";
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty, WrapContents = true };
            var field = new SettingsNumberField(value);
            var text = Hint(unit); text.Dock = DockStyle.None; text.Margin = new Padding(0, 9, 0, 0);
            row.Controls.Add(field); row.Controls.Add(text); return row;
        }
        internal int PreviewPageCount { get { return pages.Count; } }
        internal Control ModelPreviewContent { get { return pages[ModelPageIndex].Controls[0]; } }
        internal Control LearningPreviewContent { get { return pages[2].Controls[0]; } }
        internal void RefreshModelRuntimeState() { models.RefreshRuntimeState(); }
        internal void SelectModelsPage() { SelectPage(ModelPageIndex); }
        internal void SelectPage(int index)
        {
            if (index < 0 || index >= pages.Count) throw new ArgumentOutOfRangeException("index");
            if (index == ModelPageIndex) notice.Text = modelNotice;
            else if (selectedPage == ModelPageIndex) notice.Text = "设置只保存在这台电脑。\n调整后点击“保存设置”生效。";
            selectedPage = index;
            for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; navigation[i].Selected = i == index; navigation[i].AccessibleDescription = i == index ? "当前设置页" : "切换到此设置页"; navigation[i].Invalidate(); }
            pages[index].BringToFront(); pages[index].PerformLayout();
            if (models != null) UpdateSaveButton();
        }
        private void UpdateSaveButton()
        {
            save.Text = selectedPage == ModelPageIndex ? "保存并应用" : "保存设置";
            save.Enabled = !busy && !preferencesSaving && !models.IsBusy && (selectedPage != ModelPageIndex || models.CanSave);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            { SelectPage((selectedPage + ((keyData & Keys.Shift) != 0 ? pages.Count - 1 : 1)) % pages.Count); navigation[selectedPage].Focus(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); FitWorkingArea();
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e); FitWorkingArea();
        }
        private void FitWorkingArea()
        {
            // Keep the footer reachable on small screens / large Windows scaling.
            // The selected page scrolls; settings are never written by a resize.
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int width = Math.Max(1, work.Width - 24), height = Math.Max(1, work.Height - 24);
            MinimumSize = new Size(Math.Min(MinimumSize.Width, width), Math.Min(MinimumSize.Height, height));
            Size = new Size(Math.Min(Width, width), Math.Min(Height, height));
            Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
        }
        private void LoadPreferences()
        {
            loading = true;
            var p = store.Read(); theme.SelectedIndex = p.Theme; layout.SelectedIndex = p.CandidateLayout; fontSize.Value = p.FontSize;
            voice.SelectedIndex = Array.IndexOf(LearningRequest.Voices, p.Voice); speed.Value = p.SpeedPercent;
            abbreviation.Checked = p.Abbreviation; autoRemember.Checked = p.AutoRemember; toolbar.Checked = p.ToolbarVisible;
            systemFallback.Checked = p.SystemSpeechFallback;
            englishSuggestions.Checked = p.EnglishSuggestions;
            for (int i = 0; i < 8; i++) fuzzy[i].Checked = (p.FuzzyMask & (1 << i)) != 0;
            loading = false; ApplyColors(Palette.From(p));
        }
        private void LoadStartupState()
        {
            loading = true; startupConfigured = startupCanChange = false; loginStartup.Checked = false;
            try
            {
                var state = startupService == null ? null : startupService.Read();
                if (state == null) startupStatus.Text = "登录启动设置暂不可用，请通过已安装版本打开设置。";
                else
                {
                    startupConfigured = state.Configured; startupCanChange = state.CanChange; loginStartup.Checked = state.Configured;
                    startupStatus.Text = String.IsNullOrEmpty(state.Message) ? (state.Configured ? "已登记登录启动；实际启动受 Windows 设置控制。" : "尚未登记登录启动。") : state.Message;
                }
            }
            catch (Exception) { startupStatus.Text = "暂时无法读取登录启动状态；其他设置仍可使用。"; }
            finally { loginStartup.Enabled = startupCanChange; loading = false; }
        }
        private void ApplyColors(Palette colors)
        {
            bool dark = colors.Background.GetBrightness() < .5f;
            Color surface = dark ? Color.FromArgb(36, 44, 56) : Color.White;
            Color edge = dark ? Color.FromArgb(64, 77, 95) : Color.FromArgb(229, 233, 239);
            ApplyColors(this, colors, colors.Background, surface, edge);
            sidebar.BackColor = dark ? Color.FromArgb(23, 29, 38) : Color.White;
            ApplyColors(sidebar, colors, sidebar.BackColor, surface, edge);
            footer.BackColor = surface; ApplyColors(footer, colors, surface, surface, edge);
            foreach (var button in navigation) button.Apply(colors, sidebar.BackColor);
            StyleButton(save, colors.Accent, dark ? Color.FromArgb(15, 29, 44) : Color.White, colors.Accent);
            StyleButton(close, surface, colors.Foreground, edge); StyleButton(import, surface, colors.Accent, colors.Accent); StyleButton(export, surface, colors.Foreground, edge);
            Invalidate(true);
        }
        private static void ApplyColors(Control control, Palette colors, Color background, Color surface, Color edge)
        {
            var card = control as SettingsCard; if (card != null) { background = surface; card.BorderColor = edge; }
            control.BackColor = background; control.ForeColor = Object.Equals(control.Tag, "muted") ? colors.Muted : colors.Foreground;
            var number = control as SettingsNumberField; if (number != null) number.Apply(surface, edge);
            var choice = control as ThemeChoice;
            if (choice != null) { choice.Apply(colors, background); return; }
            var modelPage = control as ModelSettingsPage;
            if (modelPage != null) { modelPage.ApplyPalette(colors); return; }
            foreach (Control child in control.Controls) ApplyColors(child, colors, background, surface, edge);
        }
        private static void StyleButton(Button button, Color background, Color foreground, Color border)
        {
            var action = button as SettingsActionButton; if (action != null) { action.Apply(background, foreground, border); return; }
            button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false; button.BackColor = background; button.ForeColor = foreground;
            button.FlatAppearance.BorderColor = border; button.FlatAppearance.BorderSize = 1;
        }
        internal async Task SavePreferencesAsync()
        {
            if (busy || preferencesSaving || models.IsBusy) return;
            bool saveStartup = selectedPage == 2 && startupService != null && startupCanChange && loginStartup.Checked != startupConfigured;
            bool desiredStartup = loginStartup.Checked;
            Preferences p;
            try
            {
                p = store.Read(); p.Theme = theme.SelectedIndex; p.CandidateLayout = layout.SelectedIndex; p.FontSize = (int)fontSize.Value;
                p.Voice = LearningRequest.Voices[voice.SelectedIndex]; p.SpeedPercent = (int)speed.Value;
                p.Abbreviation = abbreviation.Checked; p.AutoRemember = autoRemember.Checked; p.ToolbarVisible = toolbar.Checked; p.FuzzyMask = 0;
                p.SystemSpeechFallback = systemFallback.Checked;
                p.EnglishSuggestions = englishSuggestions.Checked;
                for (int i = 0; i < 8; i++) if (fuzzy[i].Checked) p.FuzzyMask |= 1 << i;
            }
            catch (Exception error) when (Expected(error)) { notice.Text = "设置未保存，登录启动未改变。请确认当前用户有写入权限后重试。"; return; }
            preferencesSaving = true; content.Enabled = sidebar.Enabled = false; import.Enabled = export.Enabled = false; UpdateSaveButton();
            notice.Text = "正在保存设置…";
            bool preferencesSaved = false;
            try
            {
                // Finish the existing preferences transaction first. A failure here must never enable login startup.
                await Task.Run(() => store.SavePreferences(p)); preferencesSaved = true;
                if (IsDisposed) return;
                ApplyColors(Palette.From(p));
                bool applied = true; try { if (changed != null) changed(); } catch (Exception) { applied = false; }
                string message = applied ? "外观、输入与朗读设置已保存。" : "外观、输入与朗读设置已保存，界面刷新未完成；请重新打开设置。";
                if (saveStartup)
                {
                    StartupResult answer = await Task.Run(() => startupService.SetEnabled(desiredStartup));
                    if (IsDisposed) return;
                    if (answer != null && answer.Success)
                    {
                        startupConfigured = desiredStartup;
                        startupStatus.Text = String.IsNullOrEmpty(answer.Message) ? (desiredStartup ? "已登记登录启动。" : "已关闭登录启动。") : answer.Message;
                        notice.Text = message + "\n" + startupStatus.Text;
                    }
                    else
                    {
                        startupStatus.Text = answer == null || String.IsNullOrEmpty(answer.Message) ? "登录启动未保存，请稍后重试。" : answer.Message;
                        notice.Text = message + "\n登录启动未保存：" + startupStatus.Text;
                    }
                }
                else notice.Text = message + "\n朗读偏好从下一句生效；登录启动未改变。";
            }
            catch (Exception)
            {
                if (!IsDisposed)
                {
                    if (preferencesSaved) { startupStatus.Text = "登录启动未保存，请稍后重试。"; notice.Text = "外观、输入与朗读设置已保存。\n登录启动未保存，请检查启动项权限后重试。"; }
                    else notice.Text = "设置未保存，登录启动未改变。请确认当前用户有写入权限后重试。";
                }
            }
            finally
            {
                if (!IsDisposed) { preferencesSaving = false; content.Enabled = sidebar.Enabled = true; import.Enabled = export.Enabled = !busy; loginStartup.Enabled = startupCanChange; UpdateSaveButton(); }
            }
        }
        private async Task Import()
        {
            using (var dialog = new OpenFileDialog { Title = "导入词库", Multiselect = true, Filter = "支持的词库 (*.tsv;*.dict.yaml)|*.tsv;*.dict.yaml|所有文件 (*.*)|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true); notice.Text = "正在检查并编译词库，原词库继续可用…";
                try
                {
                    string[] paths = dialog.FileNames; var result = await Task.Run(() => lexicons.Import(paths));
                    if (IsDisposed) return;
                    notice.Text = String.Format("有效 {0} 行，拒绝 {1} 行（缺拼音 {2}）；新增 {3} 条，共 {4} 条。\n请先完成当前输入，再切到其他输入法后切回生效。", result.Accepted, result.Rejected, result.MissingPinyin, result.Added, result.Total);
                    changed();
                }
                catch (Exception error) when (Expected(error)) { if (!IsDisposed) notice.Text = SafeLexiconError(error); }
                finally { if (!IsDisposed) SetBusy(false); }
            }
        }
        private async Task Export()
        {
            using (var dialog = new SaveFileDialog { Title = "导出用户词库", FileName = "Mansur-user-lexicon.tsv", Filter = "UTF-8 TSV (*.tsv)|*.tsv", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true);
                try { string path = dialog.FileName; await Task.Run(() => lexicons.Export(path)); if (!IsDisposed) notice.Text = "用户词库已导出为 UTF-8 TSV。"; }
                catch (Exception error) when (Expected(error)) { if (!IsDisposed) notice.Text = SafeLexiconError(error); }
                finally { if (!IsDisposed) SetBusy(false); }
            }
        }
        private void SetBusy(bool value) { busy = value; import.Enabled = export.Enabled = !value; UpdateSaveButton(); }
        private static bool Expected(Exception error)
        { return error is IOException || error is UnauthorizedAccessException || error is FormatException || error is ArgumentException || error is System.Security.SecurityException || error is OperationCanceledException || error is System.ComponentModel.Win32Exception || error is InvalidOperationException; }
        private static string SafeLexiconError(Exception error)
        {
            // Only our fixed validation text is shown; native compiler stderr and imported rows are never surfaced.
            if (error is FormatException && !(error is System.Text.DecoderFallbackException)) return error.Message;
            return "操作未完成；原生效词库已保留。请检查文件格式、可用空间和完整安装组件。";
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) { productIcon.Dispose(); foreach (var font in ownedFonts) font.Dispose(); ownedFonts.Clear(); } }
    }
}
