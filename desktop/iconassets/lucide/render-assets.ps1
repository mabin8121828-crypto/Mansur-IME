# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
# Build-time only. Preserve Lucide's original paths; WPF renders the supported SVG
# path/circle/rect primitives. No WPF dependency is added to the shipped application.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
Add-Type -TypeDefinition @'
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
public static class OfficialIconRasterizer {
  static double Number(XmlElement e, string name, double fallback=0) {
    return e.HasAttribute(name) ? double.Parse(e.GetAttribute(name), CultureInfo.InvariantCulture) : fallback;
  }
  public static void Render(string source, string target, int pixels) {
    var document = new XmlDocument { XmlResolver = null }; document.Load(source);
    var svg = document.DocumentElement;
    if(svg.GetAttribute("viewBox")!="0 0 24 24" || svg.GetAttribute("fill")!="none" ||
       svg.GetAttribute("stroke-linecap")!="round" || svg.GetAttribute("stroke-linejoin")!="round") throw new InvalidDataException("Unexpected source icon format.");
    var visual = new DrawingVisual();
    using(var drawing = visual.RenderOpen()) {
      drawing.PushTransform(new ScaleTransform(pixels/24.0,pixels/24.0));
      var pen = new Pen(Brushes.White, Number(svg,"stroke-width")) { StartLineCap=PenLineCap.Round, EndLineCap=PenLineCap.Round, LineJoin=PenLineJoin.Round };
      foreach(XmlNode node in svg.ChildNodes) {
        var item = node as XmlElement; if(item==null) continue;
        Geometry geometry;
        switch(item.LocalName) {
          case "path": geometry = Geometry.Parse(item.GetAttribute("d")); break;
          case "circle": geometry = new EllipseGeometry(new Point(Number(item,"cx"),Number(item,"cy")),Number(item,"r"),Number(item,"r")); break;
          case "rect": geometry = new RectangleGeometry(new Rect(Number(item,"x"),Number(item,"y"),Number(item,"width"),Number(item,"height")),Number(item,"rx"),Number(item,"ry",Number(item,"rx"))); break;
          default: throw new InvalidDataException("Unsupported source icon primitive.");
        }
        drawing.DrawGeometry(null,pen,geometry);
      }
      drawing.Pop();
    }
    var bitmap = new RenderTargetBitmap(pixels,pixels,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using(var output = File.Create(target)) encoder.Save(output);
  }
}
'@ -ReferencedAssemblies @('PresentationCore','WindowsBase','System.Xaml','System.Xml')
$output = Join-Path $PSScriptRoot 'png'
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach($icon in @('rotate-ccw','settings','x','grip-vertical','keyboard','palette','volume-2','book-open','cpu','copy')) {
    foreach($pixels in @(20,25,30,40,60,80)) {
        [OfficialIconRasterizer]::Render((Join-Path $PSScriptRoot ($icon+'.svg')),(Join-Path $output ($icon+'-'+$pixels+'.png')),$pixels)
    }
}
Write-Output 'OFFICIAL_ICONS_RENDERED'
