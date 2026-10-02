// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal sealed class ServiceButton : Button
    {
        internal string Detail = "";
        internal bool Selected;
        internal string ServiceId = "local";
        private readonly ServiceIcons icons = new ServiceIcons();
        private Palette palette;
        private Color surface;
        private bool hovering;
        internal void Apply(Palette colors, Color background) { palette = colors; surface = background; BackColor = background; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (palette == null) { base.OnPaint(e); return; }
            e.Graphics.Clear(surface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Selected ? FloatingSurface.SelectedColor(palette) : hovering ? FloatingSurface.HoverColor(palette) : surface;
            using (var outline = FloatingSurface.Outline(new RectangleF(.5f, .5f, Width - 1, Height - 1), 7))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, outline);
            if (Selected) using (var brush = new SolidBrush(palette.Accent)) e.Graphics.FillRectangle(brush, 1, 14, 3, Height - 28);
            float scale = e.Graphics.DpiX / 96f;
            int tile = (int)(30 * scale), x = (int)(9 * scale), y = (Height - tile) / 2;
            using (var outline = FloatingSurface.Outline(new RectangleF(x, y, tile, tile), 7 * scale))
            using (var brush = new SolidBrush(palette.Background.GetBrightness() < .5f ? Color.FromArgb(28, 34, 43) : Color.White)) e.Graphics.FillPath(brush, outline);
            icons.Draw(e.Graphics, ServiceId, new Rectangle(x + (int)(4 * scale), y + (int)(4 * scale), tile - (int)(8 * scale), tile - (int)(8 * scale)), palette);
            int left = x + tile + (int)(9 * scale);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(left, 5, Width - left - 6, 23), Enabled ? palette.Foreground : palette.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (var small = new Font(Font.FontFamily, Font.Size - 1))
                TextRenderer.DrawText(e.Graphics, Detail, small, new Rectangle(left, 28, Width - left - 6, 19), palette.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) icons.Dispose(); base.Dispose(disposing); }
    }
}
