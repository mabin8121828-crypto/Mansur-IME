// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    // Real radio buttons retain the standard keyboard and accessibility behavior.
    internal sealed class ThemeChoice : TableLayoutPanel
    {
        private sealed class ChoiceButton : RadioButton
        {
            internal Palette Palette;
            internal Color Surface;
            internal bool ThemeSample;
            internal int SampleIndex;
            private bool hovering;
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Palette == null) { base.OnPaint(e); return; }
                e.Graphics.Clear(Surface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color fill = Checked ? FloatingSurface.SelectedColor(Palette) : hovering ? FloatingSurface.HoverColor(Palette) : Surface;
                bool dark = Palette.Background.GetBrightness() < .5f;
                Color border = Checked ? Palette.Accent : dark ? Color.FromArgb(64,77,95) : Color.FromArgb(229,233,239);
                using (var path = FloatingSurface.Outline(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * e.Graphics.DpiX / 96))
                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(border)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
                Rectangle textBounds = Rectangle.Inflate(ClientRectangle, -8, -3);
                if (ThemeSample)
                {
                    float scale = e.Graphics.DpiX / 96;
                    var sample = new RectangleF(12 * scale, 10 * scale, Width - 24 * scale, 30 * scale);
                    bool sampleDark = SampleIndex == 2 || (SampleIndex == 0 && dark);
                    using (var path = FloatingSurface.Outline(sample, 4 * scale))
                    using (var brush = new SolidBrush(sampleDark ? Color.FromArgb(34, 42, 55) : Color.FromArgb(242, 245, 249))) e.Graphics.FillPath(brush, path);
                    using (var ink = new SolidBrush(sampleDark ? Color.FromArgb(118, 136, 158) : Color.FromArgb(193, 202, 214)))
                    {
                        e.Graphics.FillRectangle(ink, sample.X + 8 * scale, sample.Y + 7 * scale, sample.Width * .56f, 3 * scale);
                        e.Graphics.FillRectangle(ink, sample.X + 30 * scale, sample.Y + 18 * scale, sample.Width * .42f, 3 * scale);
                    }
                    using (var accent = new SolidBrush(Palette.Accent)) e.Graphics.FillRectangle(accent, sample.X + 8 * scale, sample.Y + 16 * scale, 15 * scale, 7 * scale);
                    textBounds = new Rectangle(8, (int)(44 * scale), Width - 16, Height - (int)(44 * scale));
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                    !Enabled ? Palette.Muted : Checked ? Palette.Accent : Palette.Foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), Palette.Accent, fill);
            }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
        }
        private Palette palette;
        private Color surface;
        internal event EventHandler SelectedIndexChanged;
        internal ThemeChoice(string[] labels, bool themeSamples = false)
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
            ColumnCount = labels.Length > 3 ? 2 : labels.Length;
            for (int i = 0; i < ColumnCount; i++) ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / ColumnCount));
            for (int i = 0; i < labels.Length; i++)
            {
                var button = new ChoiceButton { Text = labels[i], Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat,
                    ThemeSample = themeSamples, SampleIndex = i,
                    AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(9, 5, 9, 5),
                    Margin = new Padding(0, 0, i % ColumnCount == ColumnCount - 1 ? 0 : 8, 6), MinimumSize = new Size(0, themeSamples ? 72 : 38), UseVisualStyleBackColor = false };
                button.CheckedChanged += delegate(object sender, EventArgs args) {
                    PaintChoices();
                    if (((RadioButton)sender).Checked && SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                };
                Controls.Add(button, i % ColumnCount, i / ColumnCount);
            }
        }
        internal void Apply(Palette colors, Color background)
        { palette = colors; surface = background; BackColor = background; PaintChoices(); }
        private void PaintChoices()
        {
            if (palette == null) return;
            foreach (ChoiceButton button in Controls)
            {
                button.Palette = palette; button.Surface = surface; button.BackColor = surface; button.Invalidate();
            }
        }
        internal int SelectedIndex
        {
            get { for (int i = 0; i < Controls.Count; i++) if (((RadioButton)Controls[i]).Checked) return i; return -1; }
            set { if (value < 0 || value >= Controls.Count) throw new ArgumentOutOfRangeException("value"); ((RadioButton)Controls[value]).Checked = true; }
        }
    }
}
