// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class StartupSettingsUiTests
    {
        private sealed class Values : ISettingsValues
        {
            internal readonly Dictionary<string, object> Data = new Dictionary<string, object>();
            internal bool FailNextWrite;
            internal Action BeforeWrite;
            public object Get(string name) { object value; return Data.TryGetValue(name, out value) ? value : null; }
            public void Set(string name, object value) { if (BeforeWrite != null) BeforeWrite(); if (FailNextWrite) { FailNextWrite = false; throw new IOException("fixed-test-failure"); } Data[name] = value; }
            public void Delete(string name) { Data.Remove(name); }
        }
        private sealed class Startup : IStartupService
        {
            internal bool Configured, CanChange = true, FailRead, FailSet, ThrowSet;
            internal int Reads, Sets;
            internal Action BeforeSet;
            public StartupState Read() { Reads++; if (FailRead) throw new IOException(); return new StartupState { Configured = Configured, CanChange = CanChange, Message = Configured ? "已登记登录启动。" : "尚未登记登录启动。" }; }
            public StartupResult SetEnabled(bool enabled)
            {
                Sets++; if (BeforeSet != null) BeforeSet();
                if (ThrowSet) throw new System.Runtime.InteropServices.COMException();
                if (FailSet) return new StartupResult { Message = "启动项权限不足。" };
                Configured = enabled; return new StartupResult { Success = true, Message = enabled ? "已登记登录启动。" : "已关闭登录启动。" };
            }
            public StartupResult Refresh(string targetExecutable) { throw new InvalidOperationException("The settings UI must never refresh an installation target."); }
        }
        private sealed class Pump : SynchronizationContext, IDisposable
        {
            private readonly Queue<Action> pending = new Queue<Action>();
            private readonly AutoResetEvent available = new AutoResetEvent(false);
            public override void Post(SendOrPostCallback action, object value) { lock (pending) pending.Enqueue(() => action(value)); available.Set(); }
            internal void Wait(Task task)
            {
                var clock = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (clock.ElapsedMilliseconds > 5000) throw new InvalidOperationException("startup-ui-test-timeout");
                    Action action = null; lock (pending) if (pending.Count != 0) action = pending.Dequeue();
                    if (action != null) action(); else available.WaitOne(10);
                }
                task.GetAwaiter().GetResult();
            }
            public void Dispose() { available.Dispose(); }
        }
        private static T Field<T>(SettingsForm form, string name)
        { return (T)typeof(SettingsForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
        private static string AllText(Control parent)
        { string value = parent.Text + "\n"; foreach (Control child in parent.Controls) value += AllText(child); return value; }
        internal static void Run(Action<bool, string> check)
        {
            bool autoInstall = WindowsFormsSynchronizationContext.AutoInstall; var previous = SynchronizationContext.Current;
            using (var pump = new Pump())
            {
                WindowsFormsSynchronizationContext.AutoInstall = false; SynchronizationContext.SetSynchronizationContext(pump);
                try
                {
                    var rememberValues = new Values(); int rememberApplied = 0;
                    using (var form = new SettingsForm(new SettingsStore(rememberValues), null, () => rememberApplied++, startup: new Startup()))
                    {
                        string help = AllText(form);
                        check(help.Contains("英文模式保留大小写与空格") && help.Contains("Enter 保留字母") && help.Contains("英文保留原文，不经过翻译") && help.Contains("Enter 只提交，不朗读"),
                            "settings-help-explains-literal-English-and-confirmation-without-translation");
                        form.SelectPage(1); var remember = Field<CheckBox>(form, "autoRemember");
                        check(remember.Checked && remember.Text == "记住常用字词" && rememberValues.Data.Count == 0,
                            "auto-remember-ui-default-is-on-and-opening-is-readonly");
                        remember.Checked = false;
                        check(rememberValues.Data.Count == 0, "auto-remember-ui-choice-needs-explicit-save");
                        pump.Wait(form.SavePreferencesAsync());
                        check(Object.Equals(rememberValues.Get("AutoRemember"), 0) && rememberApplied == 1,
                            "auto-remember-ui-saves-off-before-applying");
                        rememberValues.FailNextWrite = true; remember.Checked = true; pump.Wait(form.SavePreferencesAsync());
                        check(Object.Equals(rememberValues.Get("AutoRemember"), 0) && rememberApplied == 1 && Field<Label>(form, "notice").Text.Contains("设置未保存"),
                            "auto-remember-ui-save-failure-keeps-off-and-reports-failure");
                    }
                    using (var form = new SettingsForm(new SettingsStore(rememberValues), null, delegate { }))
                        check(!Field<CheckBox>(form, "autoRemember").Checked, "auto-remember-ui-reopens-with-saved-off-state");
                    var values = new Values(); var service = new Startup(); int applied = 0;
                    using (var form = new SettingsForm(new SettingsStore(values), null, () => applied++, startup: service))
                    {
                        var toggle = Field<CheckBox>(form, "loginStartup");
                        check(service.Reads == 1 && service.Sets == 0 && !toggle.Checked && values.Data.Count == 0, "startup-ui-open-is-readonly-and-default-off");
                        toggle.Checked = true;
                        check(service.Sets == 0, "startup-ui-toggle-needs-explicit-save");
                        form.SelectPage(0); pump.Wait(form.SavePreferencesAsync());
                        check(service.Sets == 0 && toggle.Checked, "startup-ui-saving-other-page-does-not-enable-startup");
                        bool preferencesFirst = false;
                        Field<NumericUpDown>(form, "speed").Value = 125;
                        service.BeforeSet = () => preferencesFirst = Object.Equals(values.Get("SpeedPercent"), 125);
                        form.SelectPage(2); pump.Wait(form.SavePreferencesAsync());
                        check(service.Sets == 1 && service.Configured && preferencesFirst && applied == 2, "startup-ui-persists-preferences-before-startup-and-applies-once");
                        pump.Wait(form.SavePreferencesAsync());
                        check(service.Sets == 1, "startup-ui-unchanged-save-does-not-rewrite-startup");
                        toggle.Checked = false; pump.Wait(form.SavePreferencesAsync());
                        check(service.Sets == 2 && !service.Configured, "startup-ui-explicit-disable-removes-configuration");
                        values.FailNextWrite = true; toggle.Checked = true; pump.Wait(form.SavePreferencesAsync());
                        check(service.Sets == 2 && !service.Configured && Field<Label>(form, "notice").Text.Contains("设置未保存"), "startup-ui-preference-failure-leaves-startup-untouched");
                        service.FailSet = true; pump.Wait(form.SavePreferencesAsync());
                        string notice = Field<Label>(form, "notice").Text;
                        check(service.Sets == 3 && !service.Configured && toggle.Checked && notice.Contains("朗读设置已保存") && notice.Contains("登录启动未保存"), "startup-ui-startup-failure-reports-partial-save-and-retains-intent");
                        service.FailSet = false; service.ThrowSet = true; pump.Wait(form.SavePreferencesAsync());
                        check(Field<Label>(form, "notice").Text.Contains("登录启动未保存") && Field<Button>(form, "save").Enabled, "startup-ui-service-exception-does-not-escape-or-disable-retry");
                        service.ThrowSet = false; pump.Wait(form.SavePreferencesAsync());
                        check(service.Configured && Field<Label>(form, "startupStatus").Text == "已登记登录启动。", "startup-ui-retry-can-complete-explicit-choice");
                    }
                    service = new Startup { FailRead = true }; values = new Values();
                    using (var form = new SettingsForm(new SettingsStore(values), null, delegate { }, startup: service))
                    {
                        form.SelectPage(2); pump.Wait(form.SavePreferencesAsync());
                        check(!Field<CheckBox>(form, "loginStartup").Enabled && service.Sets == 0 && values.Data.Count > 0, "startup-ui-read-failure-keeps-other-preferences-usable");
                    }
                    service = new Startup { Configured = true, CanChange = false };
                    using (var form = new SettingsForm(new SettingsStore(new Values()), null, delegate { }, startup: service))
                    {
                        form.SelectPage(2); pump.Wait(form.SavePreferencesAsync());
                        check(Field<CheckBox>(form, "loginStartup").Checked && !Field<CheckBox>(form, "loginStartup").Enabled && service.Sets == 0,
                            "startup-ui-unowned-or-protected-entry-is-not-modified");
                    }
                    using (var writesAllowed = new ManualResetEventSlim(false))
                    {
                        values = new Values { BeforeWrite = () => { if (!writesAllowed.Wait(3000)) throw new IOException("fixed-test-timeout"); } };
                        service = new Startup();
                        using (var form = new SettingsForm(new SettingsStore(values), null, delegate { }, startup: service))
                        {
                            form.SelectPage(2); Field<CheckBox>(form, "loginStartup").Checked = true;
                            Task saving = form.SavePreferencesAsync(); Task repeated = form.SavePreferencesAsync();
                            try
                            {
                                var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                                typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { closing });
                                check(closing.Cancel && !Field<Button>(form, "save").Enabled && !Field<Panel>(form, "content").Enabled,
                                    "startup-ui-inflight-save-blocks-editing-and-user-close");
                            }
                            finally { writesAllowed.Set(); }
                            pump.Wait(saving); pump.Wait(repeated);
                            check(service.Sets == 1 && Field<Button>(form, "save").Enabled,
                                "startup-ui-repeated-save-is-single-flight-and-recovers-controls");
                        }
                    }
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = autoInstall; }
            }
        }
    }
}
