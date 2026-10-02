// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class ToolbarIconTests
    {
        internal static void Run(Action<bool, string> check)
        {
            int productSizes = 0;
            foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64, 128 })
                using (Icon icon = ProductIcon.Create(new Size(size, size)))
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    int blue = 0;
                    for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                    { Color pixel = bitmap.GetPixel(x, y); if (pixel.A > 128 && pixel.B > pixel.R + 40) blue++; }
                    if (icon.Width == size && icon.Height == size && blue > 3) productSizes++;
                }
            check(productSizes == 8, "product-icon-embedded-multisize-resource-renders-supplied-blue-mark");
            using (Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            using (Bitmap bitmap = icon.ToBitmap())
            {
                int blue = 0;
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                { Color pixel = bitmap.GetPixel(x, y); if (pixel.A > 128 && pixel.B > pixel.R + 40) blue++; }
                check(blue > 3, "product-icon-windows-executable-resource-extracts-supplied-blue-mark");
            }
            int loaded = 0;
            using (var icons = new ToolbarIcons())
                foreach (string name in new[] { "rotate-ccw", "settings", "x", "grip-vertical", "keyboard", "palette", "volume-2", "book-open", "cpu", "copy" })
                    foreach (int pixels in new[] { 20, 25, 30, 40, 60, 80 })
                        using (var bitmap = new Bitmap(pixels, pixels))
                        {
                            using (var graphics = Graphics.FromImage(bitmap)) icons.Draw(graphics, name, new Rectangle(0, 0, pixels, pixels), Color.FromArgb(12, 125, 224));
                            bool nonempty = false, colored = false;
                            for (int y = 0; y < pixels; y++) for (int x = 0; x < pixels; x++)
                            { Color color = bitmap.GetPixel(x, y); if (color.A > 64) { nonempty = true; if (color.B > color.R + 80) colored = true; } }
                            if (nonempty && colored) loaded++;
                        }
            check(loaded == 60, "toolbar-and-settings-official-icon-resources-render-and-follow-theme-color");
            loaded = 0;
            using (var icons = new ServiceIcons())
                foreach (int theme in new[] { 1, 2 })
                    foreach (string id in new[] { "local", "translation-openai", "translation-openrouter", "translation-deepseek", "speech-siliconflow", "speech-custom-test", "translation-qwen", "translation-zhipu", "translation-kimi", "translation-doubao", "translation-minimax", "translation-tokenhub", "translation-hunyuan", "translation-baidu", "translation-stepfun", "translation-spark", "speech-stepfun" })
                        foreach (int size in new[] { 22, 33, 44 })
                            using (var bitmap = new Bitmap(size, size))
                            {
                                using (var g = Graphics.FromImage(bitmap)) icons.Draw(g, id, new Rectangle(0, 0, size, size), Palette.From(new Preferences { Theme = theme }));
                                bool visible = false; for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (bitmap.GetPixel(x, y).A > 64) visible = true;
                                if (visible) loaded++;
                            }
            check(loaded == 102, "services-brand-and-local-icons-render-in-both-themes-and-three-sizes");
            int actions = 0, lastAction = -1;
            using (var toolbar = new ToolbarForm(index => { actions++; lastAction = index; }, delegate { }))
            {
                toolbar.Apply(new Preferences { Theme = 1, FontSize = 20 }, false);
                var accessible = toolbar.AccessibilityObject;
                check(accessible.GetChildCount() == 4 && accessible.GetChild(2).Role == AccessibleRole.PushButton &&
                    accessible.GetChild(2).Name == "打开设置" && accessible.GetChild(3).Name.Contains("隐藏状态栏"), "toolbar-icons-have-accessible-button-names");
                toolbar.PreparePreview(null, false, -1);
                check(accessible.GetChild(0).State == AccessibleStates.Unavailable && accessible.GetChild(0).Name.Contains("暂不可用") &&
                    accessible.GetChild(1).State == AccessibleStates.Unavailable && toolbar.ActionDescription(1).Contains("没有可重播"), "toolbar-unavailable-mode-and-replay-have-explicit-semantics");
                accessible.GetChild(0).DoDefaultAction(); accessible.GetChild(1).DoDefaultAction();
                check(actions == 0, "toolbar-disabled-icon-actions-are-inert");
                toolbar.PreparePreview(true, true, 2);
                accessible.GetChild(1).DoDefaultAction();
                check(actions == 1 && lastAction == 1 && accessible.GetChild(0).Name.Contains("中文模式"), "toolbar-replay-icon-retains-original-action");
                accessible.GetChild(2).DoDefaultAction(); accessible.GetChild(3).DoDefaultAction();
                check(actions == 3 && lastAction == 3, "toolbar-settings-and-hide-icons-retain-original-actions");
                Rectangle original = accessible.GetChild(2).Bounds;
                bool scaled = true;
                foreach (int dpi in new[] { 96, 120, 144, 192 })
                    using (Bitmap bitmap = toolbar.RenderPreview(dpi))
                        scaled &= bitmap.Height == (int)Math.Ceiling(40 * dpi / 96.0) && bitmap.Width >= 184 * dpi / 96.0;
                check(scaled && accessible.GetChild(2).Bounds == original && !toolbar.Visible, "toolbar-production-painter-scales-without-showing-or-changing-hit-zones");
                toolbar.PreparePreview(false, true, -1);
                check(accessible.GetChild(0).Name.Contains("英文模式") && accessible.GetChild(0).Name.Contains("三空格提交并朗读原文") && toolbar.ActionDescription(2) == "打开设置", "toolbar-language-and-hover-descriptions-update-with-state");
            }
        }
    }
}
