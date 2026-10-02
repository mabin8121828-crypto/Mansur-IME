// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class DomesticSettingsProbe
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out int required);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    static object Create(Assembly assembly, string name, params object[] values)
    { return Activator.CreateInstance(assembly.GetType("Mansur.Next.Desktop." + name, true), Hidden, null, values, null); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, Hidden).GetValue(owner); }
    static void Call(object owner, string method, params object[] args) { owner.GetType().GetMethod(method, Hidden).Invoke(owner, args); Application.DoEvents(); }
    static void Save(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(path, ImageFormat.Png); } }
    [STAThread] static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        Console.SetOut(new StreamWriter(args[1], false) { AutoFlush = true });
        var name = new StringBuilder(256); int required;
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out required) || !name.ToString().StartsWith("MansurFocusProbe", StringComparison.Ordinal)) return 2;
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var assembly = Assembly.LoadFrom(args[0]); string folder = Path.GetDirectoryName(args[1]);
            foreach (int theme in new[] { 1, 2 }) {
                var values = Create(assembly, "PreviewRenderer+PreviewValues"); values.GetType().GetField("Theme", Hidden).SetValue(values, theme);
                var store = Create(assembly, "SettingsStore", values);
                using (var settings = (Form)Create(assembly, "SettingsForm", store, null, new Action(delegate {}), Create(assembly, "PreviewRenderer+PreviewModels", true), null, Create(assembly, "PreviewRenderer+PreviewStartup"))) {
                    settings.Show(); Call(settings, "SelectModelsPage");
                    var model = (Control)Field(settings, "models"); var title = (Label)Field(model, "apiTitle"); var viewport = (Panel)Field(model, "serviceViewport");
                    foreach (string brand in new[] { "qwen", "zhipu", "kimi", "doubao", "minimax", "tokenhub", "hunyuan", "baidu", "stepfun", "spark" }) {
                        Call(model, "SelectService", "translation-" + brand);
                        if (!IsWindowVisible(settings.Handle) || !model.Visible || !title.Visible || title.Text.Length == 0 || ((TextBox)Field(model,"apiKey")).Text.Length != 0 || viewport.DisplayRectangle.Height <= viewport.ClientSize.Height)
                            throw new InvalidOperationException("domestic-settings-visible-or-isolation-failed-" + brand);
                        Console.WriteLine("VISIBLE theme=" + theme + " provider=" + brand + " title=" + title.Text);
                        if (brand == "qwen") Save(settings, Path.Combine(folder, "qwen-" + theme + ".png"));
                    }
                    ((Control)model.Parent.Parent).Focus();
                    ((ScrollableControl)model.Parent.Parent).AutoScrollPosition = Point.Empty;
                    Call(Field(model,"capability"), "set_SelectedIndex", 1); Call(model, "SelectService", "speech-stepfun");
                    settings.Size = settings.MinimumSize; Application.DoEvents();
                    if (!title.Visible || title.Text != "阶跃星辰") throw new InvalidOperationException("speech-settings-not-visible");
                    Save(settings, Path.Combine(folder, "speech-compact-" + theme + ".png")); settings.Close();
                }
            }
            Console.WriteLine("DOMESTIC_SETTINGS_PRIVATE_PASS: own visible settings on a private desktop; no desktop switch, network, key, model or saved settings."); return 0;
        } catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
