// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal sealed class SettingsCard : TableLayoutPanel
    {
        internal Color BorderColor = Color.Gray;
        internal SettingsCard() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = FloatingSurface.Outline(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12 * e.Graphics.DpiX / 96))
            using (var fill = new SolidBrush(BackColor)) e.Graphics.FillPath(fill, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            FloatingSurface.Border(e.Graphics, Size, BorderColor, 12 * e.Graphics.DpiX / 96);
        }
    }

    internal sealed class SettingsNavigationButton : Button
    {
        internal bool Selected;
        internal string IconName;
        private readonly ToolbarIcons icons = new ToolbarIcons();
        private Palette palette;
        private Color surface;
        private bool hovering;
        internal SettingsNavigationButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; TextAlign = ContentAlignment.MiddleLeft; }
        internal void Apply(Palette colors, Color background) { palette = colors; surface = background; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (palette == null) { base.OnPaint(e); return; }
            bool dark = palette.Background.GetBrightness() < .5f;
            Color background = Selected ? (dark ? Color.FromArgb(41, 65, 91) : Color.FromArgb(235, 243, 255)) : surface;
            if (!Selected && hovering) background = dark ? Color.FromArgb(34, 44, 57) : Color.FromArgb(246, 248, 251);
            e.Graphics.Clear(surface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = FloatingSurface.Outline(new RectangleF(0, 0, Width - 1, Height - 1), 9 * e.Graphics.DpiX / 96))
            using (var fill = new SolidBrush(background)) e.Graphics.FillPath(fill, path);
            int inset = System.Math.Max(12, (int)(14 * e.Graphics.DpiX / 96));
            if (!string.IsNullOrEmpty(IconName))
            {
                int size = (int)(19 * e.Graphics.DpiX / 96);
                icons.Draw(e.Graphics, IconName, new Rectangle(inset, (Height - size) / 2, size, size), Selected ? palette.Accent : palette.Muted);
                inset += size + (int)(12 * e.Graphics.DpiX / 96);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(inset, 0, Width - inset * 2, Height), Selected ? palette.Accent : palette.Foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), palette.Accent, background);
        }
        protected override void OnMouseEnter(System.EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
        protected override void OnMouseLeave(System.EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) icons.Dispose(); base.Dispose(disposing); }
    }
}
