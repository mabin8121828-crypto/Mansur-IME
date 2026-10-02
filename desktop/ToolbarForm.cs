// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class ToolbarLayout
    {
        internal static Rectangle Clamp(Point desired, Size size, Rectangle work)
        {
            int width = Math.Min(size.Width, work.Width), height = Math.Min(size.Height, work.Height);
            return new Rectangle(Math.Max(work.Left, Math.Min(desired.X, work.Right - width)),
                Math.Max(work.Top, Math.Min(desired.Y, work.Bottom - height)), width, height);
        }
        internal static Rectangle[] Areas(Size[] measured, int padding, int grip, int height)
        {
            var rectangles = new Rectangle[measured.Length]; int left = grip;
            for (int i = 0; i < measured.Length; i++) { rectangles[i] = new Rectangle(left, 0, measured[i].Width + padding * 2, height); left = rectangles[i].Right; }
            return rectangles;
        }
        internal static int Hit(Point point, Rectangle[] rectangles)
        { for (int i = 0; i < rectangles.Length; i++) if (rectangles[i].Contains(point)) return i; return -1; }
    }
    internal sealed class ToolbarForm : Form
    {
        private readonly Action<int> action;
        private readonly Action<Point> moved;
        private Palette palette = Palette.From(new Preferences());
        private Font drawingFont = new Font("Microsoft YaHei UI", 11f);
        private bool dragging, positioned, replayAvailable;
        private Point dragOffset;
        private Rectangle[] areas = new Rectangle[0];
        private int gripWidth;
        private int hover = -1, pressed = -1, lastX = Int32.MinValue, lastY = Int32.MinValue;
        private float measuredDpi;
        private readonly ToolTip tips = new ToolTip();
        private readonly ToolbarIcons icons = new ToolbarIcons();
        private bool? chineseMode;
        internal void UpdateMode(bool? chinese) { if (chineseMode == chinese) return; chineseMode = chinese; UpdateTip(); Invalidate(); }
        internal void UpdateReplay(bool available) { if (replayAvailable == available) return; replayAvailable = available; UpdateTip(); Invalidate(); }
        internal void PreparePreview(bool? chinese, bool available, int hovered)
        { UpdateMode(chinese); UpdateReplay(available); hover = hovered >= 0 && hovered < 4 ? hovered : -1; Invalidate(); }
        internal ToolbarForm(Action<int> onAction, Action<Point> onMoved)
        {
            action = onAction; moved = onMoved;
            Text = "Mansur 状态栏"; FormBorderStyle = FormBorderStyle.None;
            TopMost = true; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true; Size = new Size(244, 40);
            AccessibleName = "Mansur 输入法状态栏"; AccessibleRole = AccessibleRole.ToolBar;
            tips.ShowAlways = true; UpdateTip();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { var p = base.CreateParams; p.ExStyle |= FloatingForm.NoActivate | FloatingForm.ToolWindow; return p; } }
        protected override void WndProc(ref Message m)
        { if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } base.WndProc(ref m); }
        internal void Apply(Preferences p, bool present = true)
        {
            var nextPalette = Palette.From(p);
            float toolbarPoints = Math.Max(11, Math.Min(13, p.FontSize));
            bool redraw = !FloatingSurface.Same(palette, nextPalette), fontChanged = Math.Abs(drawingFont.Size - toolbarPoints) > 0.01;
            palette = nextPalette; BackColor = palette.Background;
            if (fontChanged) { drawingFont.Dispose(); drawingFont = new Font("Microsoft YaHei UI", toolbarPoints); }
            using (var graphics = CreateGraphics())
            {
                if (fontChanged || areas.Length == 0 || Math.Abs(measuredDpi - graphics.DpiX) > 0.01)
                {
                Size = MeasureLayout(graphics.DpiX);
                measuredDpi = graphics.DpiX; FloatingSurface.SetRegion(this, 6 * measuredDpi / 96); redraw = true;
                }
            }
            if (!positioned)
            {
                Point desired = p.ToolbarX == Int32.MinValue || p.ToolbarY == Int32.MinValue ?
                    new Point(Screen.PrimaryScreen.WorkingArea.Right - Width - 24, Screen.PrimaryScreen.WorkingArea.Bottom - Height - 24) : new Point(p.ToolbarX, p.ToolbarY);
                Bounds = ToolbarLayout.Clamp(desired, Size, Screen.FromPoint(desired).WorkingArea); positioned = true;
            }
            else if (!dragging && (lastX != p.ToolbarX || lastY != p.ToolbarY) && p.ToolbarX != Int32.MinValue && p.ToolbarY != Int32.MinValue)
            { Point desired = new Point(p.ToolbarX, p.ToolbarY); Bounds = ToolbarLayout.Clamp(desired, Size, Screen.FromPoint(desired).WorkingArea); }
            else if (!dragging) Bounds = ToolbarLayout.Clamp(Location, Size, Screen.FromRectangle(Bounds).WorkingArea);
            lastX = p.ToolbarX; lastY = p.ToolbarY;
            if (p.ToolbarVisible && present)
            {
                if (!Visible) { Show(); SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x0010 | 0x0040); }
            }
            else if (Visible) Hide();
            if (redraw) Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            PaintToolbar(e.Graphics, ClientSize, e.Graphics.DpiX);
        }
        private Size MeasureLayout(float dpi)
        {
            int padding = (int)Math.Ceiling(10 * dpi / 96), iconSize = (int)Math.Ceiling(20 * dpi / 96);
            int height = (int)Math.Ceiling(40 * dpi / 96);
            gripWidth = (int)Math.Ceiling(24 * dpi / 96);
            var sizes = new[] { new Size(iconSize, iconSize), new Size(iconSize, iconSize), new Size(iconSize, iconSize), new Size(iconSize, iconSize) };
            areas = ToolbarLayout.Areas(sizes, padding, gripWidth, height);
            return new Size(areas[3].Right + 1, height);
        }
        // Offscreen diagnostics call this same painter at an explicit DPI. No Show,
        // focus, settings writes or external input is involved.
        internal Bitmap RenderPreview(float dpi)
        {
            if (dpi < 96 || dpi > 384) throw new ArgumentOutOfRangeException("dpi");
            Rectangle[] savedAreas = areas; int savedGrip = gripWidth; Bitmap bitmap = null;
            try
            {
                Size size = MeasureLayout(dpi);
                bitmap = new Bitmap(size.Width, size.Height); bitmap.SetResolution(dpi, dpi);
                using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(palette.Background); PaintToolbar(graphics, size, dpi); }
                return bitmap;
            }
            catch { if (bitmap != null) bitmap.Dispose(); throw; }
            finally { areas = savedAreas; gripWidth = savedGrip; }
        }
        private void PaintToolbar(Graphics graphics, Size size, float dpi)
        {
            FloatingSurface.Border(graphics, size, FloatingSurface.BorderColor(palette), 6 * dpi / 96);
            int iconSize = (int)Math.Ceiling(20 * dpi / 96);
            icons.Draw(graphics, "grip-vertical", new Rectangle((gripWidth - iconSize) / 2, (size.Height - iconSize) / 2, iconSize, iconSize), palette.Muted);
            for (int i = 0; i < areas.Length; i++)
            {
                bool enabled = ActionEnabled(i);
                if (enabled && (i == hover || i == pressed || (i == 0 && chineseMode == true)))
                {
                    Rectangle box = Rectangle.Inflate(areas[i], -(int)Math.Ceiling(3 * dpi / 96), -(int)Math.Ceiling(4 * dpi / 96));
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = FloatingSurface.Outline(box, 5 * dpi / 96))
                    using (var brush = new SolidBrush(i == pressed || (i == 0 && chineseMode == true) ? FloatingSurface.SelectedColor(palette) : FloatingSurface.HoverColor(palette))) graphics.FillPath(brush, path);
                }
                Color color = !enabled ? palette.Muted : i == 0 ? palette.Accent : palette.Foreground;
                if (i == 0 && chineseMode.HasValue)
                {
                    using (var font = new Font(drawingFont.FontFamily, drawingFont.SizeInPoints * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel))
                        TextRenderer.DrawText(graphics, chineseMode.Value ? "中" : "英", font, areas[i], color,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                else
                {
                    string name = i == 0 ? "keyboard" : i == 1 ? "rotate-ccw" : i == 2 ? "settings" : "x";
                    Rectangle area = areas[i];
                    icons.Draw(graphics, name, new Rectangle(area.Left + (area.Width - iconSize) / 2, area.Top + (area.Height - iconSize) / 2, iconSize, iconSize), color);
                }
            }
        }
        internal string ActionDescription(int index)
        {
            if (index == 0) return !chineseMode.HasValue ? "输入模式暂不可用，请先将光标放在使用 Mansur Next 的输入框" : chineseMode.Value ? "中文模式，点击或轻按 Shift 切换英文" : "英文模式，三空格提交并朗读原文；点击或轻按 Shift 切换中文";
            if (index == 1) return replayAvailable ? "重播英文朗读" : "暂时没有可重播的声音，完成一次朗读后可用";
            if (index == 2) return "打开设置";
            if (index == 3) return "隐藏状态栏，可从托盘恢复";
            return "拖动状态栏";
        }
        private void UpdateTip() { tips.SetToolTip(this, ActionDescription(hover)); }
        private bool ActionEnabled(int index) { return index == 0 ? chineseMode.HasValue : index != 1 || replayAvailable; }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int hit = ToolbarLayout.Hit(e.Location, areas);
            if (hit >= 0) { if (ActionEnabled(hit)) { pressed = hit; Capture = true; Invalidate(); } return; }
            dragging = true; dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top); Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging)
            {
                int next = ToolbarLayout.Hit(e.Location, areas);
                if (next != hover)
                {
                    hover = next; Invalidate();
                    UpdateTip();
                }
                Cursor = next < 0 ? Cursors.SizeAll : ActionEnabled(next) ? Cursors.Hand : Cursors.Default;
                return;
            }
            Point pointer = Cursor.Position, desired = new Point(pointer.X - dragOffset.X, pointer.Y - dragOffset.Y);
            Rectangle rect = ToolbarLayout.Clamp(desired, Size, Screen.FromPoint(pointer).WorkingArea);
            SetWindowPos(Handle, new IntPtr(-1), rect.X, rect.Y, rect.Width, rect.Height, 0x0010);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (dragging) { dragging = false; Capture = false; moved(Location); return; }
            int selected = pressed; pressed = -1; Capture = false; Invalidate();
            if (selected >= 0 && selected == ToolbarLayout.Hit(e.Location, areas) && ActionEnabled(selected)) action(selected);
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; pressed = -1; Invalidate(); } }
        protected override void Dispose(bool disposing)
        { if (disposing) { drawingFont.Dispose(); tips.Dispose(); icons.Dispose(); } base.Dispose(disposing); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ToolbarAccessibility(this); }
        private sealed class ToolbarAccessibility : ControlAccessibleObject
        {
            private readonly ToolbarForm owner;
            private readonly AccessibleObject[] buttons;
            internal ToolbarAccessibility(ToolbarForm owner) : base(owner)
            {
                this.owner = owner; buttons = new AccessibleObject[4];
                for (int i = 0; i < buttons.Length; i++) buttons[i] = new ButtonAccessibility(owner, this, i);
            }
            public override int GetChildCount() { return buttons.Length; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < buttons.Length ? buttons[index] : null; }
            public override AccessibleObject HitTest(int x, int y)
            { int index = ToolbarLayout.Hit(owner.PointToClient(new Point(x, y)), owner.areas); return index >= 0 ? buttons[index] : base.HitTest(x, y); }
        }
        private sealed class ButtonAccessibility : AccessibleObject
        {
            private readonly ToolbarForm owner; private readonly AccessibleObject parent; private readonly int index;
            internal ButtonAccessibility(ToolbarForm owner, AccessibleObject parent, int index) { this.owner = owner; this.parent = parent; this.index = index; }
            public override AccessibleObject Parent { get { return parent; } }
            public override string Name { get { return owner.ActionDescription(index); } set { } }
            public override string DefaultAction { get { return "按下"; } }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override AccessibleStates State { get { return owner.ActionEnabled(index) ? AccessibleStates.None : AccessibleStates.Unavailable; } }
            public override Rectangle Bounds { get { return owner.areas.Length > index ? owner.RectangleToScreen(owner.areas[index]) : Rectangle.Empty; } }
            public override void DoDefaultAction()
            {
                if (owner.IsDisposed || !owner.IsHandleCreated) return;
                Action invoke = delegate { if (!owner.IsDisposed && owner.ActionEnabled(index)) owner.action(index); };
                if (owner.InvokeRequired)
                {
                    try { owner.BeginInvoke(invoke); }
                    catch (InvalidOperationException) { /* The owned window closed between the check and dispatch. */ }
                }
                else invoke();
            }
        }
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
