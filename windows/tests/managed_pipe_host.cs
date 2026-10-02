// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Mansur.Next.Desktop;

internal static class ManagedPipeHost
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var name = "MansurNext.Learning.ManagedTest." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
        int received = 0;
        using (var legacyReceived = new ManualResetEvent(false))
        using (var listener = new PipeListener(WindowsIdentity.GetCurrent().User, line => {
            if (line.Length == 0 || line[line.Length - 1] != '}') throw new FormatException("Incomplete fixture.");
            if (Interlocked.Increment(ref received) == 2) legacyReceived.Set();
        }, name))
        {
            var info = new ProcessStartInfo(args[0], "--managed-client " + name) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var client = Process.Start(info))
            {
                if (!client.WaitForExit(10000)) throw new TimeoutException("Private native fixture did not finish.");
                Console.Write(client.StandardOutput.ReadToEnd());
                Console.Error.Write(client.StandardError.ReadToEnd());
                if (client.ExitCode != 0 || Volatile.Read(ref received) != 1) return 1;
            }
            // A previous native version connects with write-only access. It is
            // still accepted by the new duplex listener and needs no response.
            using (var legacy = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous))
            {
                legacy.Connect(2000);
                var bytes = Encoding.UTF8.GetBytes("{\"fixture\":\"legacy-write-only\"}\n");
                legacy.Write(bytes, 0, bytes.Length);
                if (!legacyReceived.WaitOne(2000)) throw new TimeoutException("Write-only compatibility fixture was not received.");
            }
        }
        Console.WriteLine("Production C# listener/native ACK and legacy write-only compatibility passed. Private pipe only; no UI/model.");
        return 0;
    }
}
