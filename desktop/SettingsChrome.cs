// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal sealed class SettingsBrandHeader : Control
    {
        private readonly Bitmap logo;
        private readonly Font title = new Font("Segoe UI", 18, FontStyle.Bold);
        private readonly Font caption = new Font("Microsoft YaHei UI", 9);
        internal SettingsBrandHeader()
        {
            using (var stream = typeof(ProductIcon).Assembly.GetManifestResourceStream("MansurNext.Product.Brand"))
            using (var decoded = new Bitmap(stream)) logo = new Bitmap(decoded);
            Height = 98; Dock = DockStyle.Top; TabStop = false;
            AccessibleName = "Mansur 输入与学习"; AccessibleRole = AccessibleRole.Graphic;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = e.Graphics.DpiX / 96f;
            int size = (int)(52 * scale), inset = (int)(3 * scale), left = size + (int)(10 * scale);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var state = e.Graphics.Save();
            using (var mask = FloatingSurface.Outline(new RectangleF(inset, 0, size, size), 10 * scale)) e.Graphics.SetClip(mask);
            e.Graphics.DrawImage(logo, new Rectangle(inset, 0, size, size)); e.Graphics.Restore(state);
            TextRenderer.DrawText(e.Graphics, "Mansur", title, new Rectangle(left, 0, Width - left, (int)(31 * scale)), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, "输入与学习", caption, new Rectangle(left, (int)(32 * scale), Width - left, (int)(21 * scale)), Color.FromArgb(128, ForeColor), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        protected override void Dispose(bool disposing) { if (disposing) { logo.Dispose(); title.Dispose(); caption.Dispose(); } base.Dispose(disposing); }
    }

    // Real buttons retain keyboard invocation, tab order and accessibility.
    internal sealed class SettingsActionButton : Button
    {
        private Color edge = Color.LightGray;
        private bool hovering, pressed;
        internal SettingsActionButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            MinimumSize = new Size(0, 36); Padding = new Padding(14, 7, 14, 7);
        }
        internal void Apply(Color background, Color foreground, Color border)
        { BackColor = background; ForeColor = foreground; edge = border; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = BackColor;
            if (!Enabled) fill = Color.FromArgb(128, fill);
            else if (hovering || pressed) fill = ControlPaint.Light(fill, pressed ? .08f : .04f);
            using (var path = FloatingSurface.Outline(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * e.Graphics.DpiX / 96))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(edge)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
            TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -8, -3), Enabled ? ForeColor : SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ForeColor, fill);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    internal sealed class SettingsNumberField : Panel
    {
        private readonly NumericUpDown number;
        private Color edge;
        internal SettingsNumberField(NumericUpDown value)
        {
            number = value; Width = 104; Height = 38; Margin = new Padding(0, 0, 12, 0);
            number.BorderStyle = BorderStyle.None; number.Location = new Point(12, 8); number.Width = 78;
            Controls.Add(number); DoubleBuffered = true;
        }
        internal void Apply(Color surface, Color border) { BackColor = number.BackColor = surface; edge = border; Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = FloatingSurface.Outline(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * e.Graphics.DpiX / 96))
            using (var brush = new SolidBrush(BackColor))
            using (var pen = new Pen(edge)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        }
    }

    internal sealed class SettingsTextField : Panel
    {
        private readonly Control editor;
        private Color edge, accent;
        internal SettingsTextField(Control value)
        {
            editor = value; Height = 40; Dock = DockStyle.Top; Padding = new Padding(10, 8, 10, 6); DoubleBuffered = true;
            var text = editor as TextBox; if (text != null) text.BorderStyle = BorderStyle.None;
            var combo = editor as ComboBox; if (combo != null) combo.FlatStyle = FlatStyle.Flat;
            editor.Dock = DockStyle.Top; editor.Margin = Padding.Empty; Controls.Add(editor);
            editor.GotFocus += EditorFocusChanged; editor.LostFocus += EditorFocusChanged;
        }
        private void EditorFocusChanged(object sender, EventArgs e) { Invalidate(); }
        protected override void Dispose(bool disposing)
        { if (disposing) { editor.GotFocus -= EditorFocusChanged; editor.LostFocus -= EditorFocusChanged; } base.Dispose(disposing); }
        internal void Apply(Color surface, Color border, Color focus)
        { BackColor = editor.BackColor = surface; edge = border; accent = focus; Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = FloatingSurface.Outline(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * e.Graphics.DpiX / 96))
            using (var brush = new SolidBrush(BackColor))
            using (var pen = new Pen(editor.ContainsFocus ? accent : edge)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        }
    }
}
