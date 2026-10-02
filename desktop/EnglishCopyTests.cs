// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class EnglishCopyTests
    {
        internal static void Run(Action<bool, string> check)
        {
            string copied = null; int calls = 0; bool busy = false;
            using (var form = new FloatingForm(text => {
                calls++; if (busy) throw new ExternalException("fixed clipboard busy"); copied = text;
            }))
            {
                const string sentence = "Hello, Mansur. I'll call you once I get home.";
                form.PreparePreview(sentence, "", new Size(1280, 800));
                var text = form.TextView;
                check(text.ReadOnly && text.Multiline && text.ImeMode == ImeMode.Disable && !text.ShortcutsEnabled,
                    "english-copy-readonly-selection-does-not-open-ime-or-paste");
                check(calls == 0, "english-copy-display-never-changes-clipboard");
                form.CopyAll();
                check(calls == 1 && copied == sentence, "english-copy-whole-sentence-exact-text");
                text.Select(7, 6); text.HandleCopyKey(Keys.Control | Keys.C);
                check(calls == 2 && copied == "Mansur" && text.Text == sentence, "english-copy-control-c-copies-only-selection");
                form.PreparePreview(sentence, "", new Size(900, 650));
                check(text.SelectedText == "Mansur", "english-copy-layout-refresh-preserves-selection");
                text.Select(0, 0); text.HandleCopyKey(Keys.Control | Keys.C);
                check(calls == 2 && copied == "Mansur", "english-copy-empty-selection-keeps-clipboard");
                text.HandleCopyKey(Keys.Control | Keys.A); text.HandleCopyKey(Keys.Control | Keys.Insert);
                check(calls == 3 && copied == sentence, "english-copy-select-all-and-alternative-shortcut");
                busy = true; form.CopyAll(); busy = false; form.CopyAll();
                check(calls == 5 && copied == sentence && text.Text == sentence, "english-copy-busy-clipboard-keeps-text-and-allows-retry");
                bool dismissed = false; form.DismissRequested += () => dismissed = true;
                text.HandleCopyKey(Keys.Escape);
                check(!dismissed && !form.SelectionView.HasSelection, "english-copy-escape-first-closes-selection-learning");
                text.HandleCopyKey(Keys.Escape);
                check(dismissed && text.Text == sentence, "english-copy-escape-dismisses-without-changing-text");
                form.ApplyPreferences(new Preferences { Theme = 2, FontSize = 20 });
                check(text.BackColor == form.BackColor && text.GetPreferredSize(new Size(150, 0)).Height > text.GetPreferredSize(new Size(1000, 0)).Height,
                    "english-copy-theme-and-wrapped-height-follow-real-text");
                form.PreparePreview("Hello", "", new Size(1280, 800));
                check(text.SelectionLength == 0 && calls == 5, "english-copy-new-result-clears-selection-without-copying");
                bool oldButton = false;
                foreach (Control control in form.Controls) if (control.Text.Contains("使用英文")) oldButton = true;
                check(!oldButton, "english-copy-no-host-writeback-action");
                form.SetActions(false, false); form.CopyAll();
                check(calls == 5, "english-copy-incomplete-generation-does-not-copy-whole-sentence");
                form.SetActions(true, true); form.CopyAll();
                check(calls == 6 && copied == "Hello", "english-copy-final-generation-enables-whole-sentence");
                form.ApplyPreferences(new Preferences { Theme = 1, FontSize = 12 });
                form.PreparePreview("Hello", "", new Size(1280, 800));
                check(form.Height <= 60 && form.Width <= 240, "english-popup-short-result-compact-icon-row");
                bool copyIcon = false, replayIcon = false;
                foreach (Control control in form.Controls) {
                    if (control.AccessibleName == "已复制" || control.AccessibleName == "复制英文") copyIcon = control.Text.Length == 0;
                    if (control.AccessibleName == "重播本句") replayIcon = control.Text.Length == 0;
                }
                check(copyIcon && replayIcon, "english-popup-actions-use-icons-and-accessible-labels");
            }
        }
    }
}
