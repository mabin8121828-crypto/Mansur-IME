// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class LearningFocusProbe
{
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder value, int size, out int required);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        Console.SetOut(new StreamWriter(args[1], false) { AutoFlush = true });
        var name = new System.Text.StringBuilder(256); int required;
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out required) ||
            !name.ToString().StartsWith("MansurFocusProbe", StringComparison.Ordinal)) { Console.WriteLine("PRIVATE_DESKTOP_REQUIRED"); return 2; }
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Assembly assembly = Assembly.LoadFrom(args[0]);
            Type type = assembly.GetType("Mansur.Next.Desktop.FloatingForm", true);
            string copied = null;
            using (var host = new Form { Size = new Size(900, 600) })
            using (var editor = new TextBox { Multiline = true, Dock = DockStyle.Fill })
            using (var learning = (Form)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { (Action<string>)(text => copied = text) }, null))
            {
                host.Controls.Add(editor); host.Show(); host.Activate(); editor.Focus(); Pump();
                int lost = 0; editor.LostFocus += delegate { lost++; };
                bool fixtureFocused = GetFocus() == editor.Handle && GetActiveWindow() == host.Handle;
                MethodInfo present = type.GetMethod("Present", BindingFlags.Instance | BindingFlags.NonPublic);
                var english = (Control)type.GetProperty("TextView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(learning);
                learning.Activated += delegate { Console.WriteLine("LEARNING_ACTIVATED"); };
                english.GotFocus += delegate { Console.WriteLine("ENGLISH_FOCUSED"); };
                type.GetMethod("PreparePreview", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(learning,
                    new object[] { "", "正在准备英文…", new Size(900,600) });
                Console.WriteLine("PREPARE_KEEPS_FOCUS " + (GetFocus() == editor.Handle));
                var anchor = new Rectangle(100, 100, 0, 24);
                present.Invoke(learning, new object[] { "", "正在准备英文…", (Rectangle?)anchor }); Pump();
                bool waitingFocused = GetFocus() == editor.Handle && GetActiveWindow() == host.Handle;
                bool waitingAbove = AboveEditor(learning,host);
                Console.WriteLine("AFTER_WAIT active_learning="+(GetActiveWindow()==learning.Handle)+" focused_english="+(GetFocus()==english.Handle)+" focused_form="+(GetFocus()==learning.Handle));
                present.Invoke(learning, new object[] { "Hello, Mansur.", "", (Rectangle?)anchor }); Pump();
                bool translatedFocused = GetFocus() == editor.Handle && GetActiveWindow() == host.Handle;
                bool translatedAbove = AboveEditor(learning,host);
                var presentation=type.GetProperty("Presentation",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(learning);
                var stateType=presentation.GetType();
                Func<string,bool> flag=name=>(bool)stateType.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(presentation);
                bool actuallyVisible=flag("Visible")&&flag("EnglishVisible")&&flag("Topmost")&&flag("OnScreen")&&!flag("Cloaked");
                using(var screenshot=new Bitmap(learning.Width,learning.Height)) {
                    learning.DrawToBitmap(screenshot,learning.ClientRectangle);
                    screenshot.Save(Path.Combine(Path.GetDirectoryName(args[1]),"actual-learning-window.png"),System.Drawing.Imaging.ImageFormat.Png);
                }
                learning.Hide(); Pump();
                host.Activate(); editor.Focus(); Pump();
                present.Invoke(learning, new object[] { "", "正在准备英文…", (Rectangle?)anchor }); Pump();
                bool redisplayFocused = GetFocus() == editor.Handle && GetActiveWindow() == host.Handle;
                bool redisplayAbove = AboveEditor(learning,host);
                bool noAutomaticLoss = lost == 0;
                present.Invoke(learning, new object[] { "Hello, Mansur.", "", (Rectangle?)anchor }); Pump();
                bool autoGuard = (GetWindowLong(learning.Handle, -20) & 0x08000000) != 0;
                var activateMessage = Message.Create(english.Handle, 0x0021, learning.Handle, IntPtr.Zero);
                object[] messageArgs = { activateMessage };
                english.GetType().GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(english, messageArgs);
                bool explicitActivation = ((Message)messageArgs[0]).Result.ToInt32() == 1 &&
                    (GetWindowLong(learning.Handle, -20) & 0x08000000) == 0;
                learning.Activate(); english.Focus(); Pump();
                ((TextBox)english).Select(7, 6);
                english.GetType().GetMethod("NotifySelection", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(english, null);
                var study = (Control)type.GetProperty("SelectionView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(learning);
                var span = study.GetType().GetProperty("Span", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(study);
                type.GetMethod("UpdateSelectionStudy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(learning,
                    new object[] { span, "专有名词 · 人名\r\n\r\n本句中是称呼。" }); Pump();
                var studyDetail = (Control)study.GetType().GetProperty("DetailView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(study);
                bool selectedStudyVisible = study.Visible && IsWindowVisible(study.Handle) && studyDetail.Visible && IsWindowVisible(studyDetail.Handle) &&
                    GetFocus() == english.Handle && GetActiveWindow() == learning.Handle && ((TextBox)english).SelectedText == "Mansur" && AboveEditor(learning,host);
                Console.WriteLine("selected_learning_visible_focus_and_highlight=" + selectedStudyVisible);
                using (var studyImage = new Bitmap(learning.Width, learning.Height)) {
                    learning.DrawToBitmap(studyImage, new Rectangle(Point.Empty, studyImage.Size));
                    studyImage.Save(Path.Combine(Path.GetDirectoryName(args[1]), "actual-selection-learning.png"), ImageFormat.Png);
                }
                if (!selectedStudyVisible) return 1;
                english.GetType().GetMethod("HandleCopyKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(english, new object[] { Keys.Control | Keys.C });
                bool selectionWorks = GetFocus() == english.Handle && copied == "Mansur" &&
                    (bool)type.GetProperty("CopyInteraction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(learning);
                host.Activate(); editor.Focus(); Pump();
                bool guardRestored = (GetWindowLong(learning.Handle, -20) & 0x08000000) != 0 &&
                    !(bool)type.GetProperty("CopyInteraction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(learning);
                int intentionalLosses = lost;
                learning.Hide();
                present.Invoke(learning, new object[] { "", "正在准备英文…", (Rectangle?)anchor }); Pump();
                bool afterSelectionSafe = GetFocus() == editor.Handle && GetActiveWindow() == host.Handle && lost == intentionalLosses;
                bool afterSelectionAbove = AboveEditor(learning,host);
                Console.WriteLine("fixture_focused=" + fixtureFocused + " waiting_keeps_focus=" + waitingFocused +
                    " translation_keeps_focus=" + translatedFocused + " redisplay_keeps_focus=" + redisplayFocused + " no_automatic_focus_loss=" + noAutomaticLoss +
                    " automatic_guard=" + autoGuard + " explicit_activation=" + explicitActivation + " selection_copy_works=" + selectionWorks +
                    " guard_restored=" + guardRestored + " post_selection_show_safe=" + afterSelectionSafe+
                    " waiting_above_editor="+waitingAbove+" result_above_editor="+translatedAbove+" redisplay_above_editor="+redisplayAbove+
                    " after_selection_above_editor="+afterSelectionAbove);
                Console.WriteLine("actual_window_and_english_visible="+actuallyVisible);
                if(!actuallyVisible)return 1;
                return fixtureFocused && waitingFocused && translatedFocused && redisplayFocused && noAutomaticLoss &&
                    autoGuard && explicitActivation && selectionWorks && guardRestored && afterSelectionSafe &&
                    waitingAbove && translatedAbove && redisplayAbove && afterSelectionAbove ? 0 : 1;
            }
        }
        catch (Exception error) { Console.WriteLine("PROBE_FAILED " + error.GetType().Name); return 3; }
    }
    private static void Pump() { for (int i=0; i<5; i++) { Application.DoEvents(); Thread.Sleep(20); } }
    private static bool AboveEditor(Form learning,Form editor)
    {
        if(!learning.Visible||(GetWindowLong(learning.Handle,-20)&8)==0)return false;
        // The private desktop owns all fixture windows; inspect order, not pixels.
        IntPtr before=GetWindow(learning.Handle,3); // GW_HWNDPREV
        for(int i=0;before!=IntPtr.Zero&&i<128;i++,before=GetWindow(before,3))
            if(before==editor.Handle)return false;
        return true;
    }
}
