// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    // Read-only plain text: selection belongs to this window, never to the host editor.
    internal sealed class SelectableEnglishText : TextBox
    {
        private readonly Action copy, dismiss, beginSelection;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem copyItem;
        internal event Action SelectionSettled;
        private Action<bool, bool> learning;
        internal void EnableLearning(Action<bool, bool> action)
        {
            learning = action;
            var lookup = new ToolStripMenuItem("查看释义", null, delegate { NotifySelection(); action(false, false); }) { ShortcutKeyDisplayString = "Ctrl+D" };
            var speak = new ToolStripMenuItem("朗读所选英文", null, delegate { NotifySelection(); action(true, false); }) { ShortcutKeyDisplayString = "Ctrl+R" };
            var slow = new ToolStripMenuItem("慢速朗读所选英文", null, delegate { NotifySelection(); action(true, true); });
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(lookup); menu.Items.Add(speak); menu.Items.Add(slow);
            menu.Opening += delegate { lookup.Enabled = speak.Enabled = slow.Enabled = SelectionLength > 0; };
        }
        internal SelectableEnglishText(Action copy, Action dismiss, Action beginSelection, string copyLabel = "复制所选英文")
        {
            this.copy = copy; this.dismiss = dismiss; this.beginSelection = beginSelection;
            Multiline = true; ReadOnly = true; BorderStyle = BorderStyle.None;
            WordWrap = true; ScrollBars = ScrollBars.None; HideSelection = false;
            ShortcutsEnabled = false; ImeMode = ImeMode.Disable; TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            AccessibleName = "英文，可选择并复制"; Cursor = Cursors.IBeam;
            copyItem = new ToolStripMenuItem(copyLabel, null, delegate { copy(); });
            menu.Items.Add(copyItem);
            menu.Items.Add(new ToolStripMenuItem("全选", null, delegate { SelectAll(); }));
            menu.Opening += delegate { copyItem.Enabled = SelectionLength > 0; };
            ContextMenuStrip = menu;
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021)
            {
                // Only an explicit click on the text activates the learning window.
                // Mark it first, so host focus cancellation cannot destroy this selection.
                BeginSelection(); message.Result = new IntPtr(1); return;
            }
            base.WndProc(ref message);
        }
        internal void BeginSelection()
        { SetStyle(ControlStyles.Selectable, true); beginSelection(); }
        internal void EndSelection()
        { SetStyle(ControlStyles.Selectable, false); }
        internal bool HandleCopyKey(Keys key)
        {
            if (key == (Keys.Control | Keys.C) || key == (Keys.Control | Keys.Insert)) { copy(); return true; }
            if (key == (Keys.Control | Keys.A)) { SelectAll(); NotifySelection(); return true; }
            if (key == Keys.Escape) { dismiss(); return true; }
            if (learning != null && (key == (Keys.Control | Keys.D) || key == (Keys.Control | Keys.R)))
            { NotifySelection(); learning(key == (Keys.Control | Keys.R), false); return true; }
            return false;
        }
        protected override bool ProcessCmdKey(ref Message message, Keys key)
        { return HandleCopyKey(key) || base.ProcessCmdKey(ref message, key); }
        internal void NotifySelection() { var action = SelectionSettled; if (action != null) action(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); NotifySelection(); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End) NotifySelection();
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 ? proposedSize.Width : Int32.MaxValue;
            var measured = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, width - 4), Int32.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak);
            return new Size(measured.Width + 4, measured.Height + 6);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) menu.Dispose(); base.Dispose(disposing); }
    }
}
