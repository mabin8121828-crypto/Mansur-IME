# Official Lucide toolbar assets

Upstream: https://github.com/lucide-icons/lucide

Pinned revision: `5a92b9ba262de5bf10e864219883267672c05db8`.

The original `rotate-ccw`, `settings`, `x`, `grip-vertical`, and `keyboard` SVG files are included verbatim. Exact URLs and SHA-256 hashes are in `sources.json`. The complete upstream ISC and Feather MIT notice is preserved in `LICENSE` and embedded in the desktop executable.

`render-assets.ps1` uses the Windows WPF geometry parser and renderer to turn the original SVG path, circle, and rounded-rectangle geometry into transparent white PNGs at 20, 25, 30, 40, 60, and 80 pixels. Run with Windows PowerShell 5.1 in STA mode. It rejects unsupported primitives; this is a fixed-source build conversion, not a general runtime SVG loader. No paths are manually redrawn or approximated.

Runtime uses the next adequate embedded PNG resolution and applies the current palette color with a color matrix. Standard Windows/.NET System.Drawing handles display; WPF is only used by the build-time export script. The toolbar uses source icons for replay, settings, hide, drag, and unavailable input mode. The necessary current mode text `中` or `英` is retained when known.
