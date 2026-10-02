// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    // Explicit off-screen rendering only. It is separate from self-tests and never starts the broker/model.
    internal static class PreviewRenderer
    {
        private sealed class PreviewValues : ISettingsValues
        {
            internal int Theme;
            public object Get(string name) { if (name == "Theme") return Theme; if (name == "FontSize") return 20; return null; }
            public void Set(string name, object value) { throw new InvalidOperationException("Preview is read-only."); }
            public void Delete(string name) { throw new InvalidOperationException("Preview is read-only."); }
        }
        private sealed class PreviewModels : IModelSettingsService, IApiServiceSettings
        {
            private readonly bool api;
            internal PreviewModels(bool useApi = false) { api = useApi; }
            public ModelSettingsState Read()
            {
                return new ModelSettingsState { HasSavedApiKey = api, ActiveProvider = "local", RestartRequired = api,
                    StatusText = "选择文件或填写连接信息后，可以先检查再保存。",
                    Draft = new ModelSettingsDraft { TranslationProvider = api ? "openrouter" : "local", Python = @"D:\Mansur 模型\运行环境\python.exe",
                        LlamaServer = @"D:\Mansur 模型\翻译\llama-server.exe", TranslationModel = @"D:\Mansur 模型\翻译\model.gguf",
                        VoiceModelDir = @"D:\Mansur 模型\英文朗读", ApiModel = "provider/model" } };
            }
            public ModelSettingsResult Validate(ModelSettingsDraft draft) { throw new InvalidOperationException("Preview is read-only."); }
            public ModelSettingsResult Save(ModelSettingsDraft draft) { throw new InvalidOperationException("Preview is read-only."); }
            public Task<ModelSettingsResult> CheckAsync(ModelSettingsDraft draft, CancellationToken token) { throw new InvalidOperationException("Preview never contacts providers."); }
            public Task<string[]> FetchModelsAsync(ApiServiceProfile profile, CancellationToken token) { throw new InvalidOperationException("Preview never contacts providers."); }
        }
        private sealed class PreviewStartup : IStartupService
        {
            public StartupState Read() { return new StartupState { CanChange = true, Message = "尚未登记登录启动。" }; }
            public StartupResult SetEnabled(bool enabled) { throw new InvalidOperationException("Preview never modifies startup."); }
            public StartupResult Refresh(string executable) { throw new InvalidOperationException("Preview never modifies startup."); }
        }
        internal static int RunStartup(string destination)
        {
            try
            {
                if (!Path.IsPathRooted(destination)) return 2;
                Directory.CreateDirectory(destination); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                foreach (int theme in new[] { 1, 2 })
                {
                    var store = new SettingsStore(new PreviewValues { Theme = theme });
                    using (var settings = new SettingsForm(store, null, delegate { }, new PreviewModels(), startup: new PreviewStartup()))
                    {
                        settings.SelectPage(2); Prepare(settings);
                        string name = "startup-" + (theme == 1 ? "light" : "dark");
                        Save(settings.LearningPreviewContent, destination, name + "-full-content");
                        settings.Size = settings.MinimumSize; Prepare(settings); Save(settings, destination, name + "-compact");
                        var page = (ScrollableControl)settings.LearningPreviewContent.Parent;
                        if (!page.AutoScroll || page.VerticalScroll.Maximum + 1 < settings.LearningPreviewContent.Bottom)
                            throw new InvalidOperationException("Startup settings scroll range is incomplete.");
                    }
                }
                Console.WriteLine("STARTUP_PREVIEW_OK: production off-screen controls; no startup item, settings, model or network changed."); return 0;
            }
            catch { Console.Error.WriteLine("STARTUP_PREVIEW_FAILED"); return 1; }
        }
        internal static int Run(string destination)
        {
            try
            {
                if (!Path.IsPathRooted(destination)) return 2;
                Directory.CreateDirectory(destination);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                foreach (int theme in new[] { 1, 2 })
                {
                    var values = new PreviewValues { Theme = theme }; var store = new SettingsStore(values);
                    string label = theme == 1 ? "light" : "dark";
                    using (var manager = new LexiconManager(store, Path.Combine(destination, "unused-lexicons"), AppDomain.CurrentDomain.BaseDirectory))
                    using (var settings = new SettingsForm(store, manager, delegate { }, new PreviewModels()))
                    using (var toolbar = new ToolbarForm(delegate { }, delegate { }))
                    {
                        Prepare(settings);
                        for (int compact = 0; compact < 2; compact++)
                        {
                            if (compact == 1) settings.Size = settings.MinimumSize;
                            for (int page = 0; page < settings.PreviewPageCount; page++)
                            {
                                settings.SelectPage(page); Prepare(settings);
                                using (var bitmap = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, settings.Size)); bitmap.Save(Path.Combine(destination, "settings-" + label + "-" + (page + 1) + (compact == 0 ? "" : "-compact") + ".png"), ImageFormat.Png); }
                            }
                        }
                        toolbar.Apply(store.Read(), false); toolbar.PerformLayout();
                        using (var bitmap = new Bitmap(toolbar.Width, toolbar.Height)) { toolbar.DrawToBitmap(bitmap, toolbar.ClientRectangle); bitmap.Save(Path.Combine(destination, "toolbar-20pt-" + label + ".png"), ImageFormat.Png); }
                    }
                    using (var popup = new FloatingForm(delegate { }))
                    {
                        popup.ApplyPreferences(new Preferences { Theme = theme, FontSize = 12 });
                        popup.SetActions(true, true); popup.PreparePreview("Hello, Mansur.", "", new Size(1280, 800)); Prepare(popup); Save(popup, destination, "learning-compact-" + label);
                        popup.SetActions(false, false); popup.PreparePreview("I'll call you", "正在生成…", new Size(1280, 800)); Save(popup, destination, "learning-streaming-" + label);
                        popup.SetActions(true, false); popup.PreparePreview("Hello, Mansur.", "英文已保留；朗读暂不可用。", new Size(1280, 800)); Save(popup, destination, "learning-recovery-" + label);
                        popup.PreparePreview("I'll call you once I get home. "+"I'll call you once I get home. "+"I'll call you once I get home.", "", new Size(240, 320)); Save(popup, destination, "learning-small-screen-" + label);
                    }
                    foreach (bool api in new[] { false, true })
                        using (var manager = new LexiconManager(store, Path.Combine(destination, "unused-lexicons"), AppDomain.CurrentDomain.BaseDirectory))
                        using (var settings = new SettingsForm(store, manager, delegate { }, new PreviewModels(api)))
                        {
                            settings.SelectPage(4); Prepare(settings);
                            string prefix = "models-" + (api ? "api-" : "local-") + label;
                        Save(settings, destination, prefix + "-top");
                        Save(settings.ModelPreviewContent, destination, prefix + "-full-content");
                        }
                }
                Console.WriteLine("PREVIEW_OK: off-screen production controls; no shown forms, model, audio or registry writes."); return 0;
            }
            catch { Console.Error.WriteLine("PREVIEW_FAILED"); return 1; }
        }
        internal static int RunModels(string destination)
        {
            try
            {
                if (!Path.IsPathRooted(destination)) return 2;
                Directory.CreateDirectory(destination);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                foreach (int theme in new[] { 1, 2 })
                {
                    var store = new SettingsStore(new PreviewValues { Theme = theme });
                    foreach (bool api in new[] { false, true })
                        using (var manager = new LexiconManager(store, Path.Combine(destination, "unused-lexicons"), AppDomain.CurrentDomain.BaseDirectory))
                        using (var settings = new SettingsForm(store, manager, delegate { }, new PreviewModels(api)))
                        {
                            settings.SelectModelsPage(); Prepare(settings);
                            string prefix = "models-" + (api ? "api-" : "local-") + (theme == 1 ? "light" : "dark");
                            CheckModelScroll(settings, prefix);
                            Save(settings, destination, prefix + "-top");
                            Save(settings.ModelPreviewContent, destination, prefix + "-full-content");
                            settings.Size = settings.MinimumSize; Prepare(settings);
                            CheckModelScroll(settings, prefix + "-compact");
                            Save(settings, destination, prefix + "-compact");
                            if (api)
                            {
                                settings.ClientSize = new Size(960, 744); Prepare(settings);
                                var model = (ModelSettingsPage)typeof(SettingsForm).GetField("models", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(settings);
                                model.SelectService("translation-deepseek"); Prepare(settings);
                                Save(settings, destination, "services-translation-" + (theme == 1 ? "light" : "dark"));
                                var selector = (ThemeChoice)typeof(ModelSettingsPage).GetField("capability", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(model);
                                selector.SelectedIndex = 1; model.SelectService("speech-openai"); Prepare(settings);
                                Save(settings, destination, "services-speech-" + (theme == 1 ? "light" : "dark"));
                                Save(settings.ModelPreviewContent, destination, "services-speech-full-" + (theme == 1 ? "light" : "dark"));
                                settings.Size = settings.MinimumSize; Prepare(settings);
                                CheckModelScroll(settings, prefix + "-speech-compact");
                                Save(settings, destination, "services-speech-compact-" + (theme == 1 ? "light" : "dark"));
                            }
                        }
                }
                Console.WriteLine("MODELS_PREVIEW_OK: production off-screen controls; no shown forms, model, network or saved settings."); return 0;
            }
            catch { Console.Error.WriteLine("MODELS_PREVIEW_FAILED"); return 1; }
        }
        internal static int RunServices(string destination)
        {
            try
            {
                if (!Path.IsPathRooted(destination)) return 2;
                Directory.CreateDirectory(destination); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                foreach (int theme in new[] { 1, 2 })
                    using (var settings = new SettingsForm(new SettingsStore(new PreviewValues { Theme = theme }), null, delegate { }, new PreviewModels(true)))
                    {
                        settings.SelectModelsPage(); Prepare(settings);
                        var model = (ModelSettingsPage)typeof(SettingsForm).GetField("models", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(settings);
                        model.SelectService("translation-deepseek"); Prepare(settings);
                        Console.WriteLine("MODEL_BOUNDS " + model.Bounds + " parent=" + model.Parent.Bounds + " visible=" + model.Visible);
                        foreach (Control child in model.Controls) Console.WriteLine("MODEL_CHILD " + child.GetType().Name + " bounds=" + child.Bounds + " visible=" + child.Visible);
                        var add = (Button)typeof(ModelSettingsPage).GetField("addService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(model);
                        var list = (TableLayoutPanel)add.Parent;
                        Console.WriteLine("SERVICE_ADD_LAYOUT row=" + list.GetRow(add) + " rows=" + list.RowCount + " button=" + add.Bounds + " list=" + list.ClientSize + " disposed=" + add.IsDisposed);
                        if (add.IsDisposed || !list.ClientRectangle.Contains(add.Bounds)) throw new InvalidOperationException("Service add button leaves its list.");
                        string label = theme == 1 ? "light" : "dark"; Save(settings, destination, "services-translation-" + label);
                        foreach (string brand in new[] { "qwen", "kimi", "minimax", "spark" })
                        {
                            model.SelectService("translation-" + brand); Prepare(settings);
                            CheckModelScroll(settings, "domestic-" + brand + "-" + label);
                            Save(settings, destination, "domestic-" + brand + "-" + label);
                        }
                        var viewport = (Panel)typeof(ModelSettingsPage).GetField("serviceViewport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(model);
                        if (!viewport.AutoScroll || viewport.DisplayRectangle.Height <= viewport.ClientSize.Height || viewport.VerticalScroll.Maximum + 1 < list.Height)
                            throw new InvalidOperationException("Domestic service list is not scrollable to the last entry.");
                        var selector = (ThemeChoice)typeof(ModelSettingsPage).GetField("capability", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(model);
                        selector.SelectedIndex = 1; model.SelectService("speech-openai"); Prepare(settings);
                        Save(settings, destination, "services-speech-" + label); Save(settings.ModelPreviewContent, destination, "services-speech-full-" + label);
                        settings.Size = settings.MinimumSize; Prepare(settings); CheckModelScroll(settings, "services-speech-compact-" + label);
                        Save(settings, destination, "services-speech-compact-" + label);
                        model.SelectService("speech-stepfun"); Prepare(settings);
                        Save(settings, destination, "domestic-speech-stepfun-compact-" + label);
                    }
                Console.WriteLine("SERVICES_PREVIEW_PASS: production controls, fixed configuration, no cloud request or user settings changed."); return 0;
            }
            catch { Console.Error.WriteLine("SERVICES_PREVIEW_FAILED"); return 1; }
        }
        private static void CheckModelScroll(SettingsForm settings, string label)
        {
            Control content = settings.ModelPreviewContent;
            var page = content.Parent as ScrollableControl;
            Console.WriteLine("MODEL_SCROLL " + label + " content=" + content.Height + " bottom=" + content.Bottom + " viewport=" + (page == null ? -1 : page.ClientSize.Height) + " max=" + (page == null ? -1 : page.VerticalScroll.Maximum) + " extent=" + (page == null ? -1 : page.DisplayRectangle.Height));
            if (page == null || !page.AutoScroll || page.VerticalScroll.Maximum + 1 < content.Bottom)
                throw new InvalidOperationException("Model settings scroll range is incomplete.");
        }
        internal static int RunLearning(string destination)
        {
            try
            {
                if (!Path.IsPathRooted(destination)) return 2;
                Directory.CreateDirectory(destination);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                foreach (int theme in new[] { 1, 2 })
                {
                    string label = theme == 1 ? "light" : "dark";
                    var p = new Preferences { Theme = theme, FontSize = 20 };
                    using (var toolbar = new ToolbarForm(delegate { }, delegate { }))
                    using (var learning = new FloatingForm())
                    {
                        toolbar.Apply(p, false); toolbar.PreparePreview(true, true, 2); Prepare(toolbar);
                        Save(toolbar, destination, "toolbar-active-20pt-" + label);
                        toolbar.PreparePreview(null, false, -1);
                        Save(toolbar, destination, "toolbar-unavailable-20pt-" + label);
                        learning.ApplyPreferences(new Preferences { Theme = theme, FontSize = 12 });
                        learning.PreparePreview("I'll call you once I get home.", "", new Size(1280, 800)); Prepare(learning);
                        CheckLearningTextLayout(learning);
                        Save(learning, destination, "learning-12pt-" + label);
                        learning.TextView.Select(5, 4);
                        Save(learning, destination, "learning-selected-copy-" + label);
                        const string selectionSource = "Tonight I will land at Victoria Square.";
                        learning.PreparePreview(selectionSource, "", new Size(1280, 800));
                        learning.TextView.Select(selectionSource.IndexOf("Victoria", StringComparison.Ordinal), 8); learning.TextView.NotifySelection();
                        var selectionSpan = learning.SelectionView.Span;
                        learning.UpdateSelectionStudy(selectionSpan, "专有名词 · 维多利亚\r\n\r\n本句中与 Square 组成地点名称。具体位置需更多信息确定。"); Prepare(learning);
                        CheckLearningTextLayout(learning); Save(learning, destination, "selection-learning-12pt-" + label);
                        learning.ApplyPreferences(p); learning.PreparePreview(selectionSource, "", new Size(340, 180)); Prepare(learning);
                        CheckLearningTextLayout(learning); Save(learning, destination, "selection-learning-narrow-20pt-" + label);
                        learning.ApplyPreferences(p);
                        learning.PreparePreview("I'll call you once I get home.", "", new Size(1280, 800)); Prepare(learning);
                        CheckLearningTextLayout(learning);
                        Save(learning, destination, "learning-20pt-" + label);
                        const string longText = "I'll call you once I get home. I'll call you once I get home. I'll call you once I get home. I'll call you once I get home.";
                        learning.PreparePreview(longText, "", new Size(1280, 800)); Prepare(learning);
                        CheckLearningTextLayout(learning);
                        Save(learning, destination, "learning-long-20pt-" + label);
                        learning.PreparePreview(longText, "", new Size(340, 180)); Prepare(learning);
                        CheckLearningTextLayout(learning);
                        Save(learning, destination, "learning-narrow-20pt-" + label);
                        learning.PreparePreview("", "正在准备朗读…", new Size(1280, 800)); Prepare(learning);
                        CheckLearningTextLayout(learning);
                        Save(learning, destination, "learning-waiting-" + label);
                        learning.PreparePreview("", "学习后台暂不可用；中文已保留。可从托盘重启本地学习后台。", new Size(340, 280)); Prepare(learning);
                        CheckLearningTextLayout(learning);
                        Save(learning, destination, "learning-error-" + label);
                        foreach (int dpi in new[] { 96, 120, 144, 192 })
                        {
                            toolbar.PreparePreview(true, true, 2);
                            using (var bitmap = toolbar.RenderPreview(dpi)) bitmap.Save(Path.Combine(destination, "icons-chinese-" + label + "-" + dpi + "dpi.png"), ImageFormat.Png);
                            toolbar.PreparePreview(false, true, 1);
                            using (var bitmap = toolbar.RenderPreview(dpi)) bitmap.Save(Path.Combine(destination, "icons-english-" + label + "-" + dpi + "dpi.png"), ImageFormat.Png);
                            toolbar.PreparePreview(null, false, -1);
                            using (var bitmap = toolbar.RenderPreview(dpi)) bitmap.Save(Path.Combine(destination, "icons-unavailable-" + label + "-" + dpi + "dpi.png"), ImageFormat.Png);
                        }
                    }
                }
                Console.WriteLine("LEARNING_PREVIEW_OK: production off-screen controls; no shown forms, models, audio or stored preferences changed."); return 0;
            }
            catch (Exception error) { Console.WriteLine("LEARNING_PREVIEW_FAILED " + error.GetType().Name + ": " + error.Message); return 1; }
        }
        private static void CheckLearningTextLayout(Control control)
        {
            var selectionPanel = control as SelectionStudyPanel;
            if (selectionPanel != null && !selectionPanel.HasSelection) return;
            var label = control as Label;
            if (label != null && !label.AutoEllipsis && !String.IsNullOrEmpty(label.Text) && label.Height < label.GetPreferredSize(new Size(label.Width, 0)).Height)
                throw new InvalidOperationException("Learning text is clipped by its label.");
            var english = control as SelectableEnglishText;
            if (english != null && english.ScrollBars == ScrollBars.None && !String.IsNullOrEmpty(english.Text) && english.Height < english.GetPreferredSize(new Size(english.Width, 0)).Height)
                throw new InvalidOperationException("Selectable English text is clipped.");
            var panel = control as Panel;
            if (panel != null && selectionPanel == null)
            {
                int bottom = 0;
                foreach (Control child in panel.Controls) if (!String.IsNullOrEmpty(child.Text)) bottom = Math.Max(bottom, child.Bottom);
                if (!panel.AutoScroll || panel.AutoScrollMinSize.Height < bottom || panel.HorizontalScroll.Visible)
                {
                    Console.WriteLine("LEARNING_SCROLL_DETAILS client=" + panel.ClientSize + " display=" + panel.DisplayRectangle + " vertical=" + panel.VerticalScroll.Visible);
                    foreach (Control child in panel.Controls) Console.WriteLine("LEARNING_SCROLL_CHILD " + child.GetType().Name + " bounds=" + child.Bounds + " margin=" + child.Margin + " visible=" + child.Visible);
                    throw new InvalidOperationException("Learning scroll area: extent=" + panel.AutoScrollMinSize.Height + " bottom=" + bottom + " horizontal=" + panel.HorizontalScroll.Visible);
                }
            }
            var form = control as FloatingForm;
            if (form != null)
            {
                Panel body = null; Control action = null;
                foreach (Control child in form.Controls)
                {
                    if (child is Panel) body = (Panel)child;
                    else if (child.AccessibleRole == AccessibleRole.PushButton && !String.IsNullOrEmpty(child.Text)) action = child;
                }
                if (body != null && action != null && body.Controls.Count > 0 && !String.IsNullOrEmpty(body.Controls[0].Text) &&
                    (action.Bounds.IntersectsWith(body.Bounds) || !form.ClientRectangle.Contains(action.Bounds)))
                    throw new InvalidOperationException("Learning action overlaps its text or leaves the window.");
            }
            foreach (Control child in control.Controls) CheckLearningTextLayout(child);
        }
        private static void Save(Control control, string directory, string name)
        { using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size)); bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png); } }
        private static void Prepare(Control control)
        {
            var handle = control.Handle;
            foreach (Control child in control.Controls) Prepare(child);
            control.PerformLayout();
        }
    }
}
