// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Mansur.Next.Desktop
{
    internal static class StartupServiceTests
    {
        internal static void Run(Action<bool, string> check)
        {
            bool recoverable = true;
            foreach (Exception failure in new Exception[] { new ArgumentException("isolated invalid startup path"),
                new UnauthorizedAccessException("isolated inaccessible startup path"), new System.Security.SecurityException("isolated startup restriction") })
            {
                var unavailable = StartupService.CreateForCurrentUser(() => { throw failure; });
                var state = unavailable.Read();
                recoverable &= !state.CanChange && state.Message.Contains("无法确认") && !state.Message.Contains("关闭") &&
                    !unavailable.SetEnabled(true).Success && !unavailable.SetEnabled(false).Success && !unavailable.Refresh("unused").Success;
            }
            check(recoverable, "startup-initialization-failure-disables-only-startup-without-claiming-off-or-writing");
            // Shell links are COM objects. A private STA performs these isolated reads/writes.
            Exception failed = null;
            var thread = new Thread(() => { try { RunSta(check); } catch (Exception error) { failed = error; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(10000)) throw new InvalidOperationException("startup isolated checks timed out");
            if (failed != null) throw failed;
        }
        private static void RunSta(Action<bool, string> check)
        {
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.GetFullPath(Path.Combine(parent, "startup-tests-" + Guid.NewGuid().ToString("N")));
            if (!root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid startup test directory.");
            Directory.CreateDirectory(root);
            try
            {
                ProgramFilesIconRoundtrip(root, check);
                string product = Path.Combine(root, "Program Files 模拟", "MansurNext");
                string startup = Path.Combine(root, "用户 启动目录");
                string state = Path.Combine(root, "用户 配置", ".mansur-next");
                string oldExe = Path.Combine(product, "0.1.0-local.4", "bin", "MansurNext.Desktop.exe");
                string newExe = Path.Combine(product, "0.1.0-local.7", "bin", "MansurNext.Desktop.exe");
                foreach (string path in new[] { oldExe, newExe }) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "never executable fixture"); }
                string registered = newExe;
                var service = new StartupService(newExe, state, startup, product, target => target == registered);
                string link = Path.Combine(startup, StartupService.LinkFileName);
                var empty = service.Read();
                check(!empty.Configured && empty.CanChange && !Directory.Exists(startup), "startup-read-default-off-does-not-create-folder-or-entry");
                check(service.Refresh(newExe).Success && !Directory.Exists(startup), "startup-update-never-enables-opted-out-startup");
                check(service.SetEnabled(true).Success && File.Exists(link), "startup-explicit-enable-writes-real-com-link-in-isolated-unicode-directory");
                var saved = ShellLinkInterop.Read(link);
                check(saved.Target == newExe && saved.Description == StartupService.DescriptionMarker && saved.ShowCommand == 7 &&
                    saved.Arguments == "--config " + WorkerProcess.Quote(Path.Combine(state, "local-models.json")) && saved.WorkingDirectory == Path.GetDirectoryName(newExe),
                    "startup-link-target-marker-nonactivating-state-and-fixed-user-config-roundtrip");
                check(service.Read().Configured && service.Read().CanChange, "startup-read-recognizes-only-owned-configuration");
                byte[] original = File.ReadAllBytes(link);
                check(service.SetEnabled(true).Success && original.SequenceEqual(File.ReadAllBytes(link)), "startup-repeated-enable-does-not-rewrite-existing-link");
                var stale = new StartupService(oldExe, state, startup, product, target => target == registered);
                check(!stale.SetEnabled(true).Success && original.SequenceEqual(File.ReadAllBytes(link)), "startup-old-window-cannot-rebind-newly-registered-version");
                registered = oldExe;
                check(service.Refresh(oldExe).Success && ShellLinkInterop.Read(link).Target == oldExe, "startup-rollback-refresh-follows-matching-registered-old-version");
                registered = newExe;
                check(service.Refresh(newExe).Success && ShellLinkInterop.Read(link).Target == newExe, "startup-update-refresh-follows-matching-new-version");
                registered = "";
                check(service.SetEnabled(false).Success && !File.Exists(link), "startup-uninstall-can-remove-owned-link-after-registration-removed");
                check(!service.SetEnabled(true).Success && !File.Exists(link), "startup-unregistered-install-cannot-enable");
                registered = newExe;
                service.SetEnabled(true); original = File.ReadAllBytes(link);
                string external = Path.Combine(root, "MansurNext.Desktop.exe"); File.WriteAllText(external, "never executable fixture");
                check(!service.Refresh(external).Success && original.SequenceEqual(File.ReadAllBytes(link)), "startup-outside-product-target-is-rejected");
                var otherUser = new StartupService(newExe, state + "-other", startup, product, target => target == registered);
                check(!otherUser.Read().CanChange && !otherUser.SetEnabled(false).Success && original.SequenceEqual(File.ReadAllBytes(link)), "startup-other-user-config-arguments-are-never-modified");
                var guarded = new StartupService(newExe, state, startup, product, target => target == registered,
                    (source, destination, replace) => { throw new IOException("synthetic publish failure"); });
                registered = oldExe;
                check(!guarded.Refresh(oldExe).Success && original.SequenceEqual(File.ReadAllBytes(link)), "startup-atomic-publish-failure-preserves-old-link");
                check(Directory.GetFiles(startup, ".MansurNext-startup-*").Length == 0, "startup-failed-staging-is-cleaned");
                registered = newExe;
                using (var locked = new FileStream(link, FileMode.Open, FileAccess.Read, FileShare.Read))
                    check(!service.SetEnabled(false).Success, "startup-locked-entry-delete-failure-is-explicit");
                check(original.SequenceEqual(File.ReadAllBytes(link)), "startup-failed-removal-keeps-original-file");
                saved = ShellLinkInterop.Read(link); saved.Description = "user modified"; File.Delete(link); ShellLinkInterop.WriteNew(link, saved);
                byte[] modified = File.ReadAllBytes(link);
                check(!service.Read().CanChange && !service.Refresh(newExe).Success && !service.SetEnabled(false).Success && modified.SequenceEqual(File.ReadAllBytes(link)), "startup-changed-description-is-preserved-on-refresh-and-remove");
                File.Delete(link); saved.Description = StartupService.DescriptionMarker; saved.Arguments += " --show-settings"; ShellLinkInterop.WriteNew(link, saved);
                modified = File.ReadAllBytes(link);
                check(!service.SetEnabled(true).Success && !service.SetEnabled(false).Success && modified.SequenceEqual(File.ReadAllBytes(link)), "startup-user-modified-arguments-are-preserved");
                File.Delete(link); saved.Arguments = "--config " + WorkerProcess.Quote(Path.Combine(state, "local-models.json")); saved.Flags |= 0x2000; ShellLinkInterop.WriteNew(link, saved);
                modified = File.ReadAllBytes(link);
                check(!service.Read().CanChange && !service.SetEnabled(false).Success && modified.SequenceEqual(File.ReadAllBytes(link)), "startup-elevation-modified-shortcut-is-not-treated-as-owned");
                File.WriteAllText(link, "unrelated same-name file"); modified = File.ReadAllBytes(link);
                check(!service.Read().CanChange && !service.SetEnabled(true).Success && !service.SetEnabled(false).Success && modified.SequenceEqual(File.ReadAllBytes(link)), "startup-malformed-or-foreign-same-name-file-is-preserved");
                File.Delete(link); Directory.CreateDirectory(link);
                check(!service.SetEnabled(true).Success && Directory.Exists(link), "startup-same-name-directory-is-preserved");
                Directory.Delete(link); service.SetEnabled(true); File.Delete(newExe); registered = "";
                check(service.Read().Configured && service.SetEnabled(false).Success, "startup-owned-link-can-be-removed-after-executable-was-removed");
                check(service.SetEnabled(false).Success, "startup-already-disabled-removal-is-idempotent");
                check(!Directory.Exists(state), "startup-service-does-not-create-or-modify-model-settings");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static void ProgramFilesIconRoundtrip(string isolatedRoot, Action<bool, string> check)
        {
            // The actual special-folder prefix triggers Shell's expandable-icon
            // behavior; all shortcut writes remain beneath the isolated test root.
            // The target need not exist and is never created, launched or changed.
            string product = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MansurNext");
            string target = Path.Combine(product, "0.1.0-local.7", "bin", "MansurNext.Desktop.exe");
            string startup = Path.Combine(isolatedRoot, "真实ProgramFiles目标的隔离启动目录");
            string state = Path.Combine(isolatedRoot, "真实ProgramFiles目标的隔离配置目录");
            Directory.CreateDirectory(startup);
            string path = Path.Combine(startup, StartupService.LinkFileName);
            var value = new StartupLink { Target = target, Arguments = "--config " + WorkerProcess.Quote(Path.Combine(state, "local-models.json")),
                Description = StartupService.DescriptionMarker, WorkingDirectory = Path.GetDirectoryName(target),
                ShowCommand = StartupService.StartupShowCommand, Icon = target, IconIndex = 0, HotKey = 0 };
            ShellLinkInterop.WriteNew(path, value);
            var actual = ShellLinkInterop.Read(path);
            check(actual.Target == target && actual.Icon == target && actual.Arguments == value.Arguments &&
                actual.Description == value.Description && actual.WorkingDirectory == value.WorkingDirectory &&
                actual.ShowCommand == value.ShowCommand && actual.IconIndex == 0 && actual.HotKey == 0,
                "startup-real-programfiles-icon-roundtrip-keeps-explicit-fields-in-isolated-directory");
            check((actual.Flags & 0x4000U) == 0, "startup-real-programfiles-icon-does-not-retain-shell-added-expandable-icon-flag");
            var service = new StartupService(target, state, startup, product, candidate => candidate == target);
            check(service.Read().Configured && service.Read().CanChange,
                "startup-real-programfiles-icon-passes-unchanged-strict-owned-check");
            File.Delete(path);
            value.Flags = actual.Flags | 0x2000U;
            ShellLinkInterop.WriteNew(path, value);
            var elevated = ShellLinkInterop.Read(path);
            byte[] original = File.ReadAllBytes(path);
            check((elevated.Flags & 0x2000U) != 0 && !service.Read().CanChange && !service.SetEnabled(false).Success &&
                original.SequenceEqual(File.ReadAllBytes(path)),
                "startup-real-programfiles-icon-normalization-does-not-strip-elevation-or-adopt-foreign-flags");
        }
    }
}
