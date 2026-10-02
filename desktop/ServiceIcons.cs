// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    // Pinned Lobe Icons brand artwork, with MIT notice in the distribution.
    internal sealed class ServiceIcons : IDisposable
    {
        private readonly Dictionary<string, Bitmap> images = new Dictionary<string, Bitmap>();
        private readonly ToolbarIcons symbols = new ToolbarIcons();
        internal void Draw(Graphics graphics, string service, Rectangle target, Palette palette)
        {
            string brand = service == "local" ? "local" : service.Contains("-custom-") ? "custom" : service.Substring(service.IndexOf('-') + 1);
            if (brand == "local" || brand == "custom") { symbols.Draw(graphics, brand == "local" ? "keyboard" : "settings", target, palette.Accent); return; }
            if (brand == "siliconflow") brand = "siliconcloud";
            if (brand == "tokenhub") brand = "tencentcloud";
            string name = brand + (palette.Background.GetBrightness() < .5f ? "-dark" : "-light");
            Bitmap bitmap;
            if (!images.TryGetValue(name, out bitmap))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MansurNext.Services." + name + ".png"))
                { if (stream == null) throw new InvalidOperationException("Missing embedded service icon."); using (var original = new Bitmap(stream)) bitmap = new Bitmap(original); }
                images.Add(name, bitmap);
            }
            var mode = graphics.InterpolationMode; graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(bitmap, target); graphics.InterpolationMode = mode;
        }
        public void Dispose() { foreach (var bitmap in images.Values) bitmap.Dispose(); images.Clear(); symbols.Dispose(); }
    }
    internal sealed class ServiceBrandIcon : Control
    {
        internal string ServiceId = "local";
        private Palette palette;
        private readonly ServiceIcons icons = new ServiceIcons();
        internal void Apply(Palette colors) { palette = colors; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        { base.OnPaint(e); if (palette != null) icons.Draw(e.Graphics, ServiceId, Rectangle.Inflate(ClientRectangle, -4, -4), palette); }
        protected override void Dispose(bool disposing) { if (disposing) icons.Dispose(); base.Dispose(disposing); }
    }
}
