# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('MansurIconPreparation.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MansurIconPreparation {
    public static class Native {
        [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        public static extern IntPtr LoadImage(IntPtr module, string path, uint type, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    }
}
'@
}
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetRoot=Join-Path $projectRoot 'desktop\iconassets\product'
$source=[Drawing.Image]::FromFile((Join-Path $assetRoot 'source.png'))
$entries=New-Object 'Collections.Generic.List[object]'
$sizes=@(16,20,24,32,40,48,64,128,256)
try {
    foreach($size in $sizes) {
        $bitmap=New-Object Drawing.Bitmap($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        $stream=New-Object IO.MemoryStream
        try {
            $graphics.CompositingQuality=[Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source,[Drawing.Rectangle]::new(0,0,$size,$size))
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            if($size -eq 256) {[IO.File]::WriteAllBytes((Join-Path $assetRoot 'brand.png'),$stream.ToArray())}
            $entries.Add([pscustomobject]@{size=$size;bytes=$stream.ToArray()})
        } finally {$stream.Dispose();$graphics.Dispose();$bitmap.Dispose()}
    }
} finally {$source.Dispose()}
$output=Join-Path $assetRoot 'Mansur.ico'
$stream=[IO.File]::Open($output,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
$writer=New-Object IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$entries.Count)
    $offset=6+16*$entries.Count
    foreach($entry in $entries) {
        $dimension=if($entry.size -eq 256){0}else{$entry.size}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
        $writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32)
        $writer.Write([uint32]$entry.bytes.Length);$writer.Write([uint32]$offset)
        $offset+=$entry.bytes.Length
    }
    foreach($entry in $entries){$writer.Write([byte[]]$entry.bytes)}
} finally {$writer.Dispose();$stream.Dispose()}
$preview=New-Object Drawing.Bitmap(640,180)
$graphics=[Drawing.Graphics]::FromImage($preview)
$font=New-Object Drawing.Font('Segoe UI',9)
try {
    $graphics.Clear([Drawing.Color]::FromArgb(239,243,248));$left=12
    foreach($size in @(16,24,32,48,64,128,256)) {
        $handle=[MansurIconPreparation.Native]::LoadImage([IntPtr]::Zero,$output,1,$size,$size,0x10)
        if ($handle -eq [IntPtr]::Zero) {throw 'Windows icon loader failed.'}
        $icon=[Drawing.Icon]::FromHandle($handle)
        try {
            if($icon.Width -ne $size -or $icon.Height -ne $size){throw 'Icon entry size mismatch.'}
            $display=[Math]::Min($size,128)
            $graphics.DrawIcon($icon,[Drawing.Rectangle]::new($left,12,$display,$display))
            $graphics.DrawString([string]$size+'px',$font,[Drawing.Brushes]::Black,$left,145)
            $left+=$display+22
        } finally {$icon.Dispose();[MansurIconPreparation.Native]::DestroyIcon($handle) | Out-Null}
    }
    $preview.Save((Join-Path $assetRoot 'preview.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally {$font.Dispose();$graphics.Dispose();$preview.Dispose()}
[pscustomobject]@{status='PRODUCT_ICON_READY';sizes=$sizes;icon_sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;bytes=(Get-Item -LiteralPath $output).Length} | ConvertTo-Json -Compress
