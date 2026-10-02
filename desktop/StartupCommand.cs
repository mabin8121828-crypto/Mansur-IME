// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace Mansur.Next.Desktop
{
    // Installer calls this short-lived helper through a normal-user WMI process.
    // Startup folder writes must not inherit the tool application's MSIX view.
    internal static class StartupCommand
    {
        internal static bool ValidResultPath(string path, string stateRoot)
        {
            try
            {
                if (!Path.IsPathRooted(path)) return false;
                string full = Path.GetFullPath(path), root = Path.GetFullPath(stateRoot).TrimEnd('\\');
                if (!String.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)) return false;
                string name = Path.GetFileName(full);
                const string prefix = "startup-operation-";
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) return false;
                Guid nonce;
                return Guid.TryParseExact(name.Substring(prefix.Length, name.Length - prefix.Length - 5), "N", out nonce);
            }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is NotSupportedException) { return false; }
        }

        internal static int Run(string[] args)
        {
            if (args.Length < 3 || args.Length > 4) return 2;
            string action = args[1], resultPath = args[args.Length - 1];
            if ((action != "inspect" && action != "remove" && action != "refresh") ||
                args.Length != (action == "refresh" ? 4 : 3) || !ValidResultPath(resultPath, UserStateDirectory.Root)) return 2;
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
                if (File.Exists(resultPath) || Directory.Exists(resultPath)) return 3;
                var service = StartupService.CreateForCurrentUser();
                StartupResult result;
                if (action == "inspect")
                {
                    var state = service.Read();
                    result = new StartupResult { Success = true, Message = state.Message };
                }
                else result = action == "remove" ? service.SetEnabled(false) : service.Refresh(args[2]);
                var current = service.Read();
                string json = Json.Write(new {
                    schema = 1, action, success = result.Success, message = result.Message,
                    configured = current.Configured, can_change = current.CanChange,
                    pid = Process.GetCurrentProcess().Id, session = Process.GetCurrentProcess().SessionId,
                    sid = identity.User.Value, executable = Process.GetCurrentProcess().MainModule.FileName,
                    updated_utc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                });
                // Publish only after closing the file so a parent can clean up a
                // complete receipt without racing this helper's write handle.
                string temporary = resultPath + ".tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                        output.Write(bytes, 0, bytes.Length);
                        output.Flush();
                    }
                    File.Move(temporary, resultPath);
                }
                finally { SharedSettingsValues.TryDelete(temporary); }
                return result.Success ? 0 : 1;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
                                          error is System.Runtime.InteropServices.COMException || error is InvalidOperationException ||
                                          error is System.ComponentModel.Win32Exception)
            {
                // No raw exception, account name, key, user text or command is logged.
                Console.Error.WriteLine("STARTUP_MAINTENANCE_FAILED");
                return 1;
            }
        }
    }
}
