// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.IO;

namespace Mansur.Next.Desktop
{
    internal static class ProductIcon
    {
        internal static Icon Create(Size size)
        {
            using (Stream stream = typeof(ProductIcon).Assembly.GetManifestResourceStream("MansurNext.Product.Icon"))
            {
                if (stream == null) throw new InvalidOperationException("Product icon resource is missing.");
                using (var icon = new Icon(stream, size)) return (Icon)icon.Clone();
            }
        }
    }
}
