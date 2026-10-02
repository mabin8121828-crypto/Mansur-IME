// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class FloatingSurface
    {
        internal static bool Same(Palette a, Palette b)
        { return a.Background == b.Background && a.Foreground == b.Foreground && a.Muted == b.Muted && a.Border == b.Border && a.Accent == b.Accent; }
        internal static Color BorderColor(Palette p)
        { return p.Background.R < 128 ? Color.FromArgb(55, 66, 81) : Color.FromArgb(215, 224, 234); }
        internal static Color HoverColor(Palette p)
        { return p.Background.R < 128 ? Color.FromArgb(37, 45, 56) : Color.FromArgb(238, 243, 249); }
        internal static Color SelectedColor(Palette p)
        { return p.Background.R < 128 ? Color.FromArgb(42, 58, 77) : Color.FromArgb(228, 238, 249); }
        internal static GraphicsPath Outline(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        internal static void SetRegion(Form form, float radius)
        {
            if (form.Width < 2 || form.Height < 2) return;
            using (var path = Outline(new RectangleF(0, 0, form.Width, form.Height), radius))
            { Region old = form.Region; form.Region = new Region(path); if (old != null) old.Dispose(); }
        }
        internal static void Border(Graphics graphics, Size size, Color color, float radius)
        {
            if (size.Width < 2 || size.Height < 2) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Outline(new RectangleF(0.5f, 0.5f, size.Width - 1, size.Height - 1), radius))
            using (var pen = new Pen(color)) graphics.DrawPath(pen, path);
        }
    }
    internal sealed class FloatingForm : Form
    {
        internal const int NoActivate = 0x08000000;
        internal const int ToolWindow = 0x00000080;
        internal const int TopmostWindow = 0x00000008;
        private readonly SelectableEnglishText english;
        private readonly Label status;
        private readonly LearningContent content;
        private readonly SelectionStudyPanel study;
        private readonly SurfaceAction dismiss, copyEnglish, replayEnglish;
        private readonly Action<string> copyText;
        private readonly Timer copyFeedback = new Timer { Interval = 1800 };
        private Palette palette = Palette.From(new Preferences { Theme = 1 });
        private bool appearanceChanged = true, layoutReady, preparing;
        private Rectangle lastWork;
        private Rectangle? lastAnchor;
        private float lastDpi;
        private volatile bool copyInteraction;
        private bool canCopy = true, canReplay;
        internal event Action DismissRequested;
        internal event Action ReplayRequested;
        internal event Action<SelectionSpan> SelectionChanged;
        internal event Action<SelectionSpan, bool, bool> SelectionRequested;
        internal SelectionStudyPanel SelectionView { get { return study; } }
        internal bool CopyInteraction { get { return copyInteraction; } }
        internal SelectableEnglishText TextView { get { return english; } }
        internal FloatingPresentation Presentation
        {
            get
            {
                if (!IsHandleCreated) return new FloatingPresentation();
                bool shown = Visible && IsWindowVisible(Handle);
                long style = IntPtr.Size == 8 ? GetWindowLongPtr(Handle, -20).ToInt64() : GetWindowLong(Handle, -20);
                bool onScreen = false;
                foreach (var screen in Screen.AllScreens) if (screen.WorkingArea.IntersectsWith(Bounds)) { onScreen = true; break; }
                int cloak; bool cloaked = DwmGetWindowAttribute(Handle, 14, out cloak, 4) == 0 && cloak != 0;
                return new FloatingPresentation { Visible = shown, EnglishVisible = shown && english.Visible && english.TextLength > 0 && IsWindowVisible(english.Handle),
                    Topmost = (style & TopmostWindow) != 0, OnScreen = onScreen, Cloaked = cloaked };
            }
        }
        internal bool PointerInside { get { return Visible && Bounds.Contains(System.Windows.Forms.Cursor.Position); } }
        internal FloatingForm(Action<string> copy = null)
        {
            Text = "学习窗口"; AccessibleName = "英语学习内容";
            FormBorderStyle = FormBorderStyle.None; AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.Selectable, false);
            DoubleBuffered = true; BackColor = palette.Background;
            copyText = copy ?? (text => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true, 3, 50));
            english = new SelectableEnglishText(CopySelection, EscapeStudy, BeginCopySelection)
                { ForeColor = palette.Foreground, BackColor = palette.Background, Font = new Font("Segoe UI", 12f), Margin = Padding.Empty };
            status = new Label { ForeColor = palette.Muted, Font = new Font("Microsoft YaHei UI", 10f), AutoSize = false, UseMnemonic = false };
            content = new LearningContent { AutoScroll = true, TabStop = false };
            study = new SelectionStudyPanel(BeginCopySelection, EscapeStudy, Copy);
            study.Margin = status.Margin = Padding.Empty;
            study.Apply(palette, english.Font.Size);
            english.SelectionSettled += SelectionSettled;
            study.Requested += (speech, slow) => { var action = SelectionRequested; if (canCopy && study.Span != null && action != null) action(study.Span, speech, slow); };
            study.Closed += ClearSelectionStudy;
            english.EnableLearning((speech, slow) => study.Request(speech, slow || study.SlowVoice));
            dismiss = new SurfaceAction("关闭学习窗口", "x", RequestDismiss);
            copyEnglish = new SurfaceAction("复制英文", "copy", CopyAll);
            replayEnglish = new SurfaceAction("重播本句", "rotate-ccw", () => { var action = ReplayRequested; if (action != null) action(); });
            content.Controls.Add(english); content.Controls.Add(status); content.Controls.Add(study); Controls.Add(content); Controls.Add(dismiss); Controls.Add(copyEnglish); Controls.Add(replayEnglish);
            dismiss.Apply(palette); copyEnglish.Apply(palette); replayEnglish.Apply(palette);
            replayEnglish.SetState("重播本句", false, "完整朗读后可重播");
            copyFeedback.Tick += delegate { copyFeedback.Stop(); copyEnglish.SetState("复制英文", canCopy, canCopy ? "复制整句英文" : "英文生成完成后可复制整句"); };
        }
        internal void SetActions(bool copy, bool replay)
        {
            if (canCopy == copy && canReplay == replay) return;
            canCopy = copy; canReplay = replay;
            if (!copy) ClearSelectionStudy();
            copyFeedback.Stop();
            copyEnglish.SetState("复制英文", canCopy, canCopy ? "复制整句英文" : "英文生成完成后可复制整句");
            replayEnglish.SetState("重播本句", canReplay, canReplay ? "重播本句" : "完整朗读后可重播");
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        // A settings poll must not repaint or reposition an unchanged learning window.
        internal bool ApplyPreferences(Preferences p)
        {
            var colors = Palette.From(p);
            bool different = !FloatingSurface.Same(colors, palette) || Math.Abs(english.Font.Size - p.FontSize) > 0.01;
            if (!different) return false;
            palette = colors; BackColor = content.BackColor = english.BackColor = colors.Background; english.ForeColor = colors.Foreground;
            status.ForeColor = colors.Muted; dismiss.Apply(colors); copyEnglish.Apply(colors); replayEnglish.Apply(colors);
            study.Apply(colors, p.FontSize);
            if (Math.Abs(english.Font.Size - p.FontSize) > 0.01)
            {
                english.Font.Dispose(); english.Font = new Font("Segoe UI", p.FontSize);
                float auxiliarySize = Math.Max(9.5f, Math.Min(11f, p.FontSize - 2));
                status.Font.Dispose(); status.Font = new Font("Microsoft YaHei UI", auxiliarySize);
            }
            appearanceChanged = true; Invalidate(); return true;
        }
        protected override CreateParams CreateParams
        { get { var value = base.CreateParams; value.ExStyle |= ToolWindow | TopmostWindow | (copyInteraction ? 0 : NoActivate); return value; } }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
            if (message.Msg == 0x02E0 && layoutReady && !preparing)
            { appearanceChanged = true; if (Visible) Present(english.Text, status.Text, lastAnchor); }
        }
        internal void Present(string text, string statusText, Rectangle? anchor)
        {
            Rectangle work = (anchor.HasValue ? Screen.FromRectangle(anchor.Value) : Screen.PrimaryScreen).WorkingArea;
            Prepare(text, statusText, anchor, work);
            if (!Visible) Show();
            // Form.TopMost activates a lazily created HWND in .NET Framework.
            // Use the native no-activation placement only after the window exists.
            SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x0010 | 0x0040);
        }
        // The same production layout without Show, focus queries, audio or registry writes.
        internal void PreparePreview(string text, string statusText, Size available)
        { Prepare(text, statusText, null, new Rectangle(Point.Empty, available)); }
        private void Prepare(string text, string statusText, Rectangle? anchor, Rectangle work)
        {
            text = text ?? ""; statusText = statusText ?? "";
            using (var graphics = CreateGraphics())
            {
                float dpi = graphics.DpiX;
                if (layoutReady && !appearanceChanged && english.Text == text && status.Text == statusText && lastAnchor == anchor && lastWork == work && Math.Abs(lastDpi - dpi) < 0.01) return;
                bool newText = english.Text != text || status.Text != statusText;
                if (english.Text != text)
                {
                    ResetSelectionStudy(false);
                    english.Text = text; english.Select(0, 0);
                    copyFeedback.Stop(); copyEnglish.SetState("复制英文", canCopy, canCopy ? "复制整句英文" : "英文生成完成后可复制整句");
                }
                status.Text = statusText;
                bool hasEnglish = !String.IsNullOrEmpty(text), hasStatus = !String.IsNullOrEmpty(statusText);
                int padding = Math.Max(8, (int)Math.Ceiling(14 * dpi / 96));
                int gap = Math.Max(6, (int)Math.Ceiling(8 * dpi / 96));
                int buttonSize = (int)Math.Ceiling(28 * dpi / 96), buttonInset = (int)Math.Ceiling(6 * dpi / 96);
                int closeReserve = buttonSize * (hasEnglish ? 3 : 1) + buttonInset + (int)Math.Ceiling(5 * dpi / 96);
                int naturalText = Math.Max(hasEnglish ? english.GetPreferredSize(Size.Empty).Width : 0,
                    hasStatus ? status.GetPreferredSize(Size.Empty).Width : 0);
                int naturalWidth = naturalText + padding + closeReserve + 4;
                if (study.HasSelection) naturalWidth = Math.Max(naturalWidth, (int)Math.Ceiling(460 * dpi / 96));
                int wantedWidth = Math.Max((int)Math.Ceiling(156 * dpi / 96), Math.Min(naturalWidth, (int)Math.Ceiling(620 * dpi / 96)));
                int width = Math.Max(80, Math.Min(wantedWidth, work.Width - Math.Min(24, work.Width / 8)));
                int contentWidth = Math.Max(24, width - padding - closeReserve), textWidth = contentWidth;
                int fullHeight = hasEnglish ? english.GetPreferredSize(new Size(textWidth, 0)).Height + 4 : 0;
                int statusHeight = hasStatus ? status.GetPreferredSize(new Size(textWidth, 0)).Height + 4 : 0;
                int bodyGap = hasEnglish && hasStatus ? gap : 0;
                int maxHeight = Math.Max(48, work.Height - Math.Min(24, work.Height / 8));
                int fixedHeight = padding * 2;
                int fullBody = fullHeight + bodyGap + statusHeight;
                int studyHeight = study.HasSelection ? study.Measure(textWidth, dpi) : 0;
                if (studyHeight > 0) fullBody += gap + studyHeight;
                int viewHeight = Math.Min(fullBody, Math.Max(16, maxHeight - fixedHeight));
                if (fullBody > viewHeight)
                {
                    textWidth = Math.Max(24, textWidth - SystemInformation.VerticalScrollBarWidth - 2);
                    fullHeight = hasEnglish ? english.GetPreferredSize(new Size(textWidth, 0)).Height + 4 : 0;
                    statusHeight = hasStatus ? status.GetPreferredSize(new Size(textWidth, 0)).Height + 4 : 0;
                    fullBody = fullHeight + bodyGap + statusHeight;
                    studyHeight = study.HasSelection ? study.Measure(textWidth, dpi) : 0;
                    if (studyHeight > 0) fullBody += gap + studyHeight;
                }
                int height = Math.Min(maxHeight, Math.Max(buttonSize + buttonInset * 2, fixedHeight + viewHeight));
                Rectangle bounds = Positioning.Place(anchor, work, new Size(width, height));
                preparing = true; SuspendLayout(); content.SuspendLayout();
                try
                {
                    // Reset previous scroll extents before changing width. Otherwise
                    // a narrow selected card can leave a stale horizontal scrollbar
                    // after the next short sentence replaces it.
                    content.ResetScroll();
                    if (newText) content.AutoScrollPosition = Point.Empty;
                    content.SetBounds(padding, padding, contentWidth, viewHeight);
                    english.Visible = hasEnglish; status.Visible = hasStatus;
                    english.SetBounds(0, 0, textWidth, fullHeight);
                    content.AutoScrollMinSize = new Size(0, fullBody);
                    status.SetBounds(0, fullHeight + bodyGap, textWidth, statusHeight);
                    study.SetBounds(0, fullHeight + bodyGap + statusHeight + gap, textWidth, studyHeight);
                    dismiss.SetBounds(width - buttonInset - buttonSize, buttonInset, buttonSize, buttonSize);
                    copyEnglish.Visible = hasEnglish;
                    replayEnglish.Visible = hasEnglish;
                    copyEnglish.SetBounds(width - buttonInset - buttonSize * 2, buttonInset, buttonSize, buttonSize);
                    replayEnglish.SetBounds(width - buttonInset - buttonSize * 3, buttonInset, buttonSize, buttonSize);
                    content.AutoScroll = true;
                    Bounds = bounds;
                }
                finally { content.ResumeLayout(); ResumeLayout(); preparing = false; }
                lastWork = work; lastAnchor = anchor; lastDpi = dpi; appearanceChanged = false; layoutReady = true;
                content.RefreshScroll();
                if (study.HasSelection) content.ScrollControlIntoView(study);
                FloatingSurface.SetRegion(this, 6 * dpi / 96); Invalidate();
            }
        }
        private sealed class LearningContent : Panel
        {
            internal void ResetScroll()
            {
                AutoScrollPosition = Point.Empty;
                AutoScrollMinSize = Size.Empty; AutoScroll = false;
                AdjustFormScrollbars(false);
            }
            internal void RefreshScroll()
            {
                AutoScroll = true; AdjustFormScrollbars(true);
                AutoScrollPosition = Point.Empty;
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        { base.OnPaint(e); FloatingSurface.Border(e.Graphics, ClientSize, FloatingSurface.BorderColor(palette), 6 * e.Graphics.DpiX / 96); }
        private void RequestDismiss()
        { var requested = DismissRequested; if (requested != null) requested(); else Hide(); }
        internal void CopyAll()
        {
            if (canCopy) Copy(english.Text);
        }
        internal void CopySelection()
        {
            Copy(english.SelectedText);
        }
        private void Copy(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            try { copyText(text); copyEnglish.SetState("已复制", canCopy, "英文已复制"); }
            catch (Exception error) when (error is ExternalException || error is System.Threading.ThreadStateException)
            { copyEnglish.SetState("重试复制", canCopy, "剪贴板正忙，请再点一次"); }
            copyFeedback.Stop(); copyFeedback.Start();
        }
        private void BeginCopySelection()
        {
            copyInteraction = true;
            SetStyle(ControlStyles.Selectable, true);
            ProtectAutomaticActivation(false);
        }
        private void SelectionSettled()
        {
            SelectionSpan span = canCopy ? SelectionSpan.Create(english.Text, english.SelectionStart, english.SelectionLength) : null;
            if (!study.Bind(span)) return;
            var action = SelectionChanged; if (action != null) action(span);
            ReflowStudy();
        }
        internal void ClearSelectionStudy()
        { ResetSelectionStudy(true); }
        private void ResetSelectionStudy(bool reflow)
        {
            if (study == null || !study.Bind(null)) return;
            var action = SelectionChanged; if (action != null) action(null);
            if (reflow) ReflowStudy();
        }
        private void EscapeStudy()
        { if (study.HasSelection) ClearSelectionStudy(); else RequestDismiss(); }
        internal void UpdateSelectionStudy(SelectionSpan span, string text)
        {
            if (span == null || study.Span == null || !study.Span.Same(span)) return;
            if (study.Update(text)) ReflowStudy();
        }
        private void ReflowStudy()
        {
            appearanceChanged = true;
            if (layoutReady && !preparing) {
                if (Visible) Present(english.Text, status.Text, lastAnchor);
                else Prepare(english.Text, status.Text, lastAnchor, lastWork);
            }
        }
        private void EndCopySelection()
        {
            copyInteraction = false;
            SetStyle(ControlStyles.Selectable, false);
            english.EndSelection();
            ActiveControl = null;
            ProtectAutomaticActivation(true);
        }
        private void ProtectAutomaticActivation(bool protect)
        {
            if (!IsHandleCreated) return;
            long current = IntPtr.Size == 8 ? GetWindowLongPtr(Handle, -20).ToInt64() : GetWindowLong(Handle, -20);
            long wanted = protect ? current | NoActivate : current & ~NoActivate;
            if (current == wanted) return;
            if (IntPtr.Size == 8) SetWindowLongPtr(Handle, -20, new IntPtr(wanted));
            else SetWindowLong(Handle, -20, (int)wanted);
        }
        protected override void OnDeactivate(EventArgs e)
        { ClearSelectionStudy(); EndCopySelection(); base.OnDeactivate(e); }
        protected override void OnVisibleChanged(EventArgs e)
        { if (!Visible && english != null) { ClearSelectionStudy(); EndCopySelection(); } base.OnVisibleChanged(e); }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { EscapeStudy(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { copyFeedback.Dispose(); english.Font.Dispose(); status.Font.Dispose(); } base.Dispose(disposing); }
        internal sealed class SurfaceAction : Control
        {
            private readonly Action action;
            private readonly string icon;
            private readonly ToolbarIcons icons = new ToolbarIcons();
            private readonly ToolTip tip = new ToolTip();
            private Palette colors;
            private bool hovered, pressed, actionEnabled = true;
            internal SurfaceAction(string name, string icon, Action action)
            {
                this.action = action; this.icon = icon;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false; Cursor = Cursors.Hand;
                Font = new Font("Microsoft YaHei UI", 10.5f);
                Text = icon == null ? name : "";
                AccessibleName = name; AccessibleRole = AccessibleRole.PushButton;
                tip.ShowAlways = true; tip.SetToolTip(this, AccessibleName);
            }
            internal void Apply(Palette palette) { colors = palette; BackColor = palette.Background; Invalidate(); }
            internal void SetState(string text, bool enabled, string description)
            {
                Text = icon == null ? text : ""; AccessibleName = text; AccessibleDescription = description;
                actionEnabled = enabled; Cursor = enabled ? Cursors.Hand : Cursors.Default;
                if (!enabled) { pressed = false; Capture = false; }
                tip.SetToolTip(this, description); Invalidate();
            }
            internal Size MeasureAction(float dpi)
            {
                Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                return new Size(Math.Max((int)Math.Ceiling(88 * dpi / 96), text.Width + (int)Math.Ceiling(24 * dpi / 96)),
                    Math.Max((int)Math.Ceiling(30 * dpi / 96), text.Height + (int)Math.Ceiling(10 * dpi / 96)));
            }
            protected override void WndProc(ref Message message)
            { if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } base.WndProc(ref message); }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (icon == null || (actionEnabled && (hovered || pressed)))
                    using (var shape = FloatingSurface.Outline(new RectangleF(0, 0, Width, Height), 5 * e.Graphics.DpiX / 96))
                    using (var brush = new SolidBrush(actionEnabled && (pressed || icon == null) ? FloatingSurface.SelectedColor(colors) : FloatingSurface.HoverColor(colors)))
                    { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillPath(brush, shape); }
                if (icon != null)
                {
                    int size = (int)Math.Ceiling(16 * e.Graphics.DpiX / 96);
                    icons.Draw(e.Graphics, icon, new Rectangle((Width - size) / 2, (Height - size) / 2, size, size), actionEnabled ? colors.Muted : Color.FromArgb(120, colors.Muted));
                }
                else TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, actionEnabled ? colors.Accent : colors.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e)
            { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && actionEnabled) { pressed = true; Capture = true; Invalidate(); } }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return;
                bool invoke = actionEnabled && pressed && ClientRectangle.Contains(e.Location);
                pressed = false; Capture = false; Invalidate(); if (invoke) action();
            }
            protected override void OnMouseCaptureChanged(EventArgs e)
            { base.OnMouseCaptureChanged(e); if (!Capture) { pressed = false; Invalidate(); } }
            protected override AccessibleObject CreateAccessibilityInstance() { return new ActionAccessibility(this); }
            private sealed class ActionAccessibility : ControlAccessibleObject
            {
                private readonly SurfaceAction owner;
                internal ActionAccessibility(SurfaceAction owner) : base(owner) { this.owner = owner; }
                public override string DefaultAction { get { return owner.AccessibleName; } }
                public override AccessibleStates State { get { return base.State | (owner.actionEnabled ? AccessibleStates.None : AccessibleStates.Unavailable); } }
                public override void DoDefaultAction()
                {
                    if (owner.IsDisposed || !owner.IsHandleCreated) return;
                    Action invoke = delegate { if (!owner.IsDisposed && owner.actionEnabled) owner.action(); };
                    if (owner.InvokeRequired) { try { owner.BeginInvoke(invoke); } catch (InvalidOperationException) { } }
                    else invoke();
                }
            }
            protected override void Dispose(bool disposing)
            { if (disposing) { icons.Dispose(); tip.Dispose(); Font.Dispose(); } base.Dispose(disposing); }
        }
        [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint="SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    }
    internal sealed class FloatingPresentation
    {
        internal bool Visible, EnglishVisible, Topmost, OnScreen, Cloaked;
    }
}
