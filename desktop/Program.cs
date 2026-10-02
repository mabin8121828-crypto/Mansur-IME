// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test") return SelfTests.Run();
            if (args.Length == 2 && args[0] == "--test-worker-child") return StabilityTests.Child(args[1]);
            if (args.Length == 2 && args[0] == "--render-previews") return PreviewRenderer.Run(args[1]);
            if (args.Length == 2 && args[0] == "--render-learning-previews") return PreviewRenderer.RunLearning(args[1]);
            if (args.Length > 0 && args[0] == "--maintain-startup") return StartupCommand.Run(args);
            string startupStage = "identity";
            try
            {
                if (args.Length == 3 && (args[0] == "--prepare-user" || args[0] == "--prepare-settings"))
                {
                    startupStage = "prepare_user";
                    string sharedPath = args[0] == "--prepare-settings" ? StartupMigration.PrepareSettings(args[1], args[2]) : StartupMigration.Prepare(args[1], args[2]);
                    Console.WriteLine(Json.Write(new { status = "USER_STATE_READY", configuration_path = sharedPath }));
                    return 0;
                }
                SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
                if (args.Length == 1 && args[0] == "--shutdown")
                {
                    Mutex existing;
                    if (!Mutex.TryOpenExisting("Local\\MansurNext.Learning." + user.Value, out existing)) return 0;
                    existing.Dispose();
                    if (!ControlChannel.Send(user, "shutdown")) return 3;
                    // The acknowledgement means accepted. Exit is confirmed by the singleton disappearing.
                    for (int i = 0; i < 100; i++)
                    {
                        if (!Mutex.TryOpenExisting("Local\\MansurNext.Learning." + user.Value, out existing)) return 0;
                        existing.Dispose(); Thread.Sleep(100);
                    }
                    return 4;
                }
                bool showSettings = args.Length == 1 && args[0] == "--show-settings";
                string configPath;
                if (showSettings)
                {
                    if (ControlChannel.Send(user, "show-settings")) return 0;
                    configPath = Path.Combine(UserStateDirectory.Root, "local-models.json");
                }
                else if (args.Length == 2 && args[0] == "--config") configPath = Path.GetFullPath(args[1]);
                else { Console.Error.WriteLine("Usage: --config <JSON path> | --prepare-user/--prepare-settings <source config> <preferences snapshot> | --show-settings | --shutdown | --self-test"); return 2; }
                startupStage = "singleton";
                bool created;
                using (var mutex = new Mutex(true, "Local\\MansurNext.Learning." + user.Value, out created))
                {
                    if (!created) return showSettings ? 3 : 0;
                    startupStage = "visual_styles";
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    startupStage = "broker";
                    // The settings surface must survive an absent or broken model configuration.
                    // Only the worker factory loads executable/model paths, on its own lifecycle thread.
                    using (var context = new BrokerContext(configPath, user, showSettings)) { startupStage = "running"; Application.Run(context); }
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException ||
                                          error is ArgumentException || error is System.ComponentModel.Win32Exception)
            {
                // Configuration failures contain no user input and do not touch the installed IME.
                SaveStartupFailure(startupStage, error);
                Console.Error.WriteLine("Mansur learning backend could not start. Check the explicit configuration and local model files.");
                return 1;
            }
        }
        private static void SaveStartupFailure(string stage, Exception error)
        {
            try
            {
                var directory = UserStateDirectory.Root;
                Directory.CreateDirectory(directory);
                // Fixed phase/type and numeric codes only: never serialize an exception message, command line or input.
                File.WriteAllText(Path.Combine(directory, "startup-failure.json"), Json.Write(new {
                    format_version = 1, pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    updated_utc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    stage, exception_type = error.GetType().Name, hresult = error.HResult,
                    native_error = error is System.ComponentModel.Win32Exception ? (int?)((System.ComponentModel.Win32Exception)error).NativeErrorCode : null
                }), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
