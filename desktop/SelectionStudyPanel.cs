// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal sealed class SelectionStudyPanel : Panel
    {
        private readonly Label title;
        private readonly SelectableEnglishText detail;
        private readonly FloatingForm.SurfaceAction lookup, read, close;
        private readonly CheckBox slow;
        private Palette palette;
        internal event Action<bool, bool> Requested;
        internal event Action Closed;
        internal SelectionSpan Span { get; private set; }
        internal bool HasSelection { get { return Span != null; } }
        internal bool SlowVoice { get { return slow.Checked; } set { slow.Checked = value; } }
        internal SelectableEnglishText DetailView { get { return detail; } }
        internal SelectionStudyPanel(Action beginSelection, Action dismiss, Action<string> copy)
        {
            Visible = false; TabStop = false;
            title = new Label { AutoEllipsis = true, UseMnemonic = false, Font = new Font("Segoe UI", 11f, FontStyle.Bold), AccessibleName = "选中内容" };
            detail = new SelectableEnglishText(() => copy(detail.SelectedText), dismiss, beginSelection, "复制所选释义")
                { Font = new Font("Microsoft YaHei UI", 10f) };
            lookup = new FloatingForm.SurfaceAction("查看释义", "book-open", () => Request(false, false));
            read = new FloatingForm.SurfaceAction("朗读所选英文", "volume-2", () => Request(true, slow.Checked));
            close = new FloatingForm.SurfaceAction("收起选词学习", "x", () => { var action = Closed; if (action != null) action(); });
            slow = new CheckBox { Text = "慢速", AutoSize = true, TabStop = false, Font = new Font("Microsoft YaHei UI", 9f), AccessibleName = "慢速朗读所选英文" };
            Controls.Add(title); Controls.Add(detail); Controls.Add(lookup); Controls.Add(read); Controls.Add(close); Controls.Add(slow);
        }
        internal void Request(bool speech, bool slowVoice)
        { if (Span != null) { var action = Requested; if (action != null) action(speech, slowVoice); } }
        internal bool Bind(SelectionSpan value)
        {
            if (Span != null && Span.Same(value) || Span == null && value == null) return false;
            Span = value; Visible = value != null; title.Text = value == null ? "" : value.Text.Trim(); detail.Text = ""; return true;
        }
        internal bool Update(string text)
        { text = text ?? ""; if (detail.Text == text) return false; detail.Text = text; return true; }
        internal void Apply(Palette colors, float fontSize)
        {
            palette = colors; BackColor = detail.BackColor = colors.Background; title.ForeColor = detail.ForeColor = colors.Foreground;
            slow.ForeColor = colors.Muted; lookup.Apply(colors); read.Apply(colors); close.Apply(colors);
            float size = Math.Max(10, fontSize);
            if (Math.Abs(detail.Font.Size - size) > 0.01) { detail.Font.Dispose(); detail.Font = new Font("Microsoft YaHei UI", size); }
        }
        internal int Measure(int width, float dpi)
        {
            int icon = (int)Math.Ceiling(28 * dpi / 96), gap = (int)Math.Ceiling(8 * dpi / 96);
            int textWidth = Math.Max(24, width - 2 * gap);
            int detailHeight = String.IsNullOrEmpty(detail.Text) ? 0 : detail.GetPreferredSize(new Size(textWidth, 0)).Height + gap;
            bool narrow = width < (int)Math.Ceiling(320 * dpi / 96);
            int actionY = narrow ? icon + gap : gap;
            int headerHeight = (narrow ? icon * 2 : icon) + gap * 2;
            title.SetBounds(gap, gap, Math.Max(24, width - (narrow ? icon + gap * 3 : icon * 3 + slow.PreferredSize.Width + gap * 3)), icon);
            close.SetBounds(width - icon - gap, gap, icon, icon);
            read.SetBounds(width - icon * (narrow ? 1 : 2) - gap, actionY, icon, icon);
            lookup.SetBounds(width - icon * (narrow ? 2 : 3) - gap, actionY, icon, icon);
            slow.Location = new Point(narrow ? gap : width - icon * 3 - slow.PreferredSize.Width - gap * 2, actionY + Math.Max(0, (icon - slow.PreferredSize.Height) / 2));
            detail.Visible = detailHeight > 0; detail.ScrollBars = ScrollBars.None;
            detail.SetBounds(gap, headerHeight, textWidth, detailHeight);
            return headerHeight + (detailHeight > 0 ? detailHeight + gap : 0);
        }
        protected override void OnPaint(PaintEventArgs e)
        { base.OnPaint(e); using (var pen = new Pen(palette.Border)) e.Graphics.DrawLine(pen, 0, 0, Width, 0); }
        protected override void Dispose(bool disposing)
        { if (disposing) { title.Font.Dispose(); detail.Font.Dispose(); slow.Font.Dispose(); } base.Dispose(disposing); }
    }
}
