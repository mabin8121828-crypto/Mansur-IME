// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mansur.Next.Desktop
{
    internal interface IStartupService
    {
        StartupState Read();
        StartupResult SetEnabled(bool enabled);
        StartupResult Refresh(string targetExecutable);
    }
    internal sealed class StartupState
    {
        internal bool Configured, CanChange;
        internal string Message = "";
    }
    internal sealed class StartupResult
    {
        internal bool Success;
        internal string Message = "";
    }
    internal sealed class StartupService : IStartupService
    {
        internal const string LinkFileName = "MansurNext Learning.lnk";
        internal const string DescriptionMarker = "MansurNext.UserStartup.v1";
        internal const int StartupShowCommand = 7; // Supported Shell-link SW_SHOWMINNOACTIVE; the broker has no startup main window.
        private const string RegistrationPath = @"Software\Classes\CLSID\{E17225F9-B37A-4A39-A6FA-6EC6971CB481}\InprocServer32";
        private readonly object gate = new object();
        private readonly string executable, stateRoot, startupFolder, productRoot, linkPath;
        private readonly Func<string, bool> registered;
        private readonly Action<string, string, bool> publish;
        internal static IStartupService CreateForCurrentUser()
        { return CreateForCurrentUser(() => new StartupService()); }
        internal static IStartupService CreateForCurrentUser(Func<IStartupService> create)
        {
            try { return create() ?? new UnavailableStartupService(); }
            catch (Exception error) when (Recoverable(error)) { return new UnavailableStartupService(); }
        }
        private sealed class UnavailableStartupService : IStartupService
        {
            private const string Notice = "无法确认登录启动状态。请检查当前用户启动文件夹的位置和访问权限，再重新打开设置；现有启动项未更改。";
            public StartupState Read() { return new StartupState { CanChange = false, Message = Notice }; }
            public StartupResult SetEnabled(bool enabled) { return new StartupResult { Success = false, Message = Notice }; }
            public StartupResult Refresh(string targetExecutable) { return new StartupResult { Success = false, Message = Notice }; }
        }
        internal StartupService() : this(typeof(Program).Assembly.Location, UserStateDirectory.Root,
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MansurNext"), Registered, Publish) { }
        internal StartupService(string targetExecutable, string userStateRoot, string startupDirectory, string installedProductRoot,
            Func<string, bool> registeredTarget, Action<string, string, bool> publishFile = null)
        {
            executable = FullPath(targetExecutable); stateRoot = FullPath(userStateRoot); startupFolder = FullPath(startupDirectory);
            productRoot = FullPath(installedProductRoot).TrimEnd(Path.DirectorySeparatorChar); linkPath = Path.Combine(startupFolder, LinkFileName);
            registered = registeredTarget ?? throw new ArgumentNullException("registeredTarget"); publish = publishFile ?? Publish;
        }
        private static string FullPath(string path)
        {
            if (!SharedSettingsValues.AbsolutePath(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Local absolute startup path required.");
            return Path.GetFullPath(path);
        }
        private bool TargetOwned(string path, bool requireFiles)
        {
            if (!SharedSettingsValues.AbsolutePath(path) || !String.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return false;
            string boundary = productRoot + Path.DirectorySeparatorChar;
            if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) return false;
            string relative = path.Substring(boundary.Length);
            if (!Regex.IsMatch(relative, @"\A0\.1\.0-local\.[0-9]{1,9}\\bin\\MansurNext\.Desktop\.exe\z", RegexOptions.IgnoreCase)) return false;
            if (requireFiles && !File.Exists(path)) return false;
            return NoReparse(path);
        }
        private static bool NoReparse(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        private string Arguments { get { return "--config " + WorkerProcess.Quote(Path.Combine(stateRoot, "local-models.json")); } }
        private StartupLink ForTarget(string target)
        { return new StartupLink { Target = target, Arguments = Arguments, Description = DescriptionMarker, WorkingDirectory = Path.GetDirectoryName(target), ShowCommand = StartupShowCommand, Icon = target, IconIndex = 0, HotKey = 0 }; }
        private bool Owned(StartupLink value)
        {
            return TargetOwned(value.Target, false) && value.Arguments == Arguments && value.Description == DescriptionMarker &&
                String.Equals(value.WorkingDirectory, Path.GetDirectoryName(value.Target), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(value.Icon, value.Target, StringComparison.OrdinalIgnoreCase) && value.ShowCommand == StartupShowCommand && value.IconIndex == 0 && value.HotKey == 0 &&
                (value.Flags & (0x200U | 0x400U | 0x1000U | 0x2000U | 0x4000U | 0x20000U | 0x800000U | 0x2000000U)) == 0;
        }
        private bool Exists { get { return File.Exists(linkPath) || Directory.Exists(linkPath); } }
        private byte[] OwnedBytes()
        {
            if (!NoReparse(startupFolder) || !NoReparse(linkPath) || Directory.Exists(linkPath)) throw new IOException("startup_entry_conflict");
            byte[] bytes = SharedSettingsValues.ReadBounded(linkPath, 131072);
            if (!Owned(ShellLinkInterop.Read(linkPath))) throw new IOException("startup_entry_conflict");
            // Detect any change during COM reading before treating the entry as ours.
            if (!bytes.SequenceEqual(SharedSettingsValues.ReadBounded(linkPath, 131072))) throw new IOException("startup_entry_changed");
            return bytes;
        }
        public StartupState Read()
        {
            lock (gate)
            {
                try
                {
                    if (!NoReparse(startupFolder)) return new StartupState { Message = "启动文件夹路径不可安全使用，未作更改。" };
                    if (Exists)
                    {
                        OwnedBytes();
                        return new StartupState { Configured = true, CanChange = true, Message = "已配置登录启动。Windows 的启动项管理仍可单独禁用它。" };
                    }
                    bool allowed = TargetOwned(executable, true) && registered(executable);
                    return new StartupState { CanChange = allowed, Message = allowed ? "未开启登录启动；保存勾选后才会创建启动项。" : "当前后台不是已注册的安装版本，请从当前安装版本打开设置。" };
                }
                catch (Exception error) when (Recoverable(error)) { return new StartupState { Message = "同名启动项已被修改或无法读取，已保留原文件。" }; }
            }
        }
        public StartupResult SetEnabled(bool enabled)
        {
            lock (gate)
            {
                if (enabled) return Write(executable, false);
                try
                {
                    if (!Exists) return Result(true, "登录启动已关闭。");
                    byte[] expected = OwnedBytes();
                    if (!expected.SequenceEqual(SharedSettingsValues.ReadBounded(linkPath, 131072))) return Result(false, "启动项刚被更改，已保留原文件。");
                    File.Delete(linkPath);
                    return Result(true, "登录启动已关闭。");
                }
                catch (Exception error) when (Recoverable(error)) { return Result(false, "无法安全移除启动项，已保留原文件。"); }
            }
        }
        public StartupResult Refresh(string targetExecutable)
        {
            lock (gate)
            {
                // Updating an installation must never opt the user into login startup.
                if (!Exists) return Result(true, "登录启动未开启，未创建启动项。");
                return Write(targetExecutable, true);
            }
        }
        private StartupResult Write(string target, bool onlyExisting)
        {
            string temporary = null;
            try
            {
                if (!TargetOwned(target, true) || !registered(target)) return Result(false, "目标不是两种架构当前注册的安装版本，启动项未更改。");
                if (!NoReparse(startupFolder)) return Result(false, "启动文件夹路径不可安全使用，未作更改。");
                bool exists = Exists; if (onlyExisting && !exists) return Result(true, "登录启动未开启，未创建启动项。");
                byte[] previous = exists ? OwnedBytes() : null;
                if (exists && String.Equals(ShellLinkInterop.Read(linkPath).Target, target, StringComparison.OrdinalIgnoreCase)) return Result(true, "登录启动已配置为当前版本。");
                Directory.CreateDirectory(startupFolder);
                temporary = Path.Combine(startupFolder, ".MansurNext-startup-" + Guid.NewGuid().ToString("N") + ".tmp");
                ShellLinkInterop.WriteNew(temporary, ForTarget(target));
                if (!Owned(ShellLinkInterop.Read(temporary))) throw new IOException("startup_staging_invalid");
                if (exists)
                {
                    if (!previous.SequenceEqual(OwnedBytes())) throw new IOException("startup_entry_changed");
                }
                else if (Exists) throw new IOException("startup_entry_changed");
                publish(temporary, linkPath, exists);
                return Result(true, "已配置登录后自动启动学习后台；不会改变默认输入法。");
            }
            catch (Exception error) when (Recoverable(error)) { return Result(false, "启动项保存失败，原文件已保留。"); }
            finally { if (temporary != null) SharedSettingsValues.TryDelete(temporary); }
        }
        private static void Publish(string source, string destination, bool replace)
        { if (replace) File.Replace(source, destination, null); else File.Move(source, destination); }
        private static bool Registered(string target)
        {
            string versionRoot = Path.GetDirectoryName(Path.GetDirectoryName(target));
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string expected = Path.Combine(versionRoot, "bin", view == RegistryView.Registry64 ? "x64" : "x86", "mansur_next_tsf.dll");
                if (!File.Exists(expected) || !NoReparse(expected)) return false;
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var entry = machine.OpenSubKey(RegistrationPath, false))
                    if (entry == null || !String.Equals(entry.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        private static bool Recoverable(Exception error)
        { return SharedSettingsValues.Recoverable(error) || error is COMException || error is InvalidCastException || error is NotSupportedException; }
        private static StartupResult Result(bool success, string message) { return new StartupResult { Success = success, Message = message }; }
    }
}
