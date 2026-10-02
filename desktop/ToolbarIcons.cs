// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;

namespace Mansur.Next.Desktop
{
    // Official Lucide SVG geometry, rasterized by WPF at build time. Original
    // sources, pinned revision and ISC/MIT notice are in iconassets/lucide.
    internal sealed class ToolbarIcons : IDisposable
    {
        private readonly Dictionary<string, Bitmap> images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private static readonly int[] resolutions = { 20, 25, 30, 40, 60, 80 };
        internal void Draw(Graphics graphics, string name, Rectangle target, Color color)
        {
            int pixels = resolutions[resolutions.Length - 1];
            foreach (int resolution in resolutions) if (resolution >= target.Width) { pixels = resolution; break; }
            string key = name + "-" + pixels;
            Bitmap bitmap;
            if (!images.TryGetValue(key, out bitmap))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MansurNext.Icons." + key + ".png"))
                {
                    if (stream == null) throw new InvalidOperationException("Missing embedded toolbar icon.");
                    using (var decoded = new Bitmap(stream)) bitmap = new Bitmap(decoded);
                }
                images.Add(key, bitmap);
            }
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(new ColorMatrix(new[] {
                    new[] { color.R / 255f, 0, 0, 0, 0 }, new[] { 0, color.G / 255f, 0, 0, 0 },
                    new[] { 0, 0, color.B / 255f, 0, 0 }, new[] { 0, 0, 0, color.A / 255f, 0 }, new float[] { 0, 0, 0, 0, 1 }
                }));
                graphics.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
            }
        }
        public void Dispose() { foreach (Bitmap bitmap in images.Values) bitmap.Dispose(); images.Clear(); }
    }
}
