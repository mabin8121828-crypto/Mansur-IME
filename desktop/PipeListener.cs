// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mansur.Next.Desktop
{
    internal sealed class PipeListener : IDisposable
    {
        private readonly string name;
        private readonly SecurityIdentifier user;
        private readonly Action<string> receive;
        private readonly List<NamedPipeServerStream> pipes = new List<NamedPipeServerStream>();
        private readonly Thread[] threads = new Thread[2];
        private volatile bool stopping;
        internal PipeListener(SecurityIdentifier userSid, Action<string> onMessage)
            : this(userSid, onMessage, null) { }
        // An explicit private name is used only by the isolated transport host.
        internal PipeListener(SecurityIdentifier userSid, Action<string> onMessage, string privateName)
        {
            user = userSid;
            name = privateName ?? "MansurNext.Learning." + user.Value;
            receive = onMessage;
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(Listen) { IsBackground = true, Name = "Learning pipe " + i };
                threads[i].Start();
            }
        }
        internal static PipeSecurity Security(SecurityIdentifier user)
        {
            var result = new PipeSecurity();
            result.SetAccessRuleProtection(true, false);
            result.SetOwner(user);
            result.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            result.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return result;
        }
        private void Listen()
        {
            while (!stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 4096, 4096, Security(user), HandleInheritability.None);
                    lock (pipes)
                    {
                        if (stopping) { pipe.Dispose(); return; }
                        pipes.Add(pipe);
                    }
                    pipe.WaitForConnection();
                    string line = ReadLine(pipe, 65536, 2000);
                    if (!stopping && line.Length != 0)
                    {
                        receive(line);
                        Acknowledge(pipe, 250);
                    }
                }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is UnauthorizedAccessException ||
                                               error is FormatException || error is DecoderFallbackException || error is TimeoutException)
                {
                    // Malformed/stalled clients affect only their connection. Never log the request.
                    if (pipe == null && !stopping) Thread.Sleep(250);
                }
                finally
                {
                    if (pipe != null) { lock (pipes) pipes.Remove(pipe); pipe.Dispose(); }
                }
            }
        }
        internal static void Acknowledge(Stream stream, int timeoutMs)
        {
            // A single fixed byte confirms that a complete line reached receive.
            // It carries no text and does not claim model or playback success.
            // Older write-only clients remain valid; their close can make this
            // best-effort acknowledgement fail after receive already completed.
            var ack = new byte[] { 0x06 };
            Task write = stream.WriteAsync(ack, 0, ack.Length);
            if (Task.WhenAny(write, Task.Delay(timeoutMs)).GetAwaiter().GetResult() != write)
            {
                stream.Dispose();
                write.ContinueWith(task => { var observed = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException();
            }
            write.GetAwaiter().GetResult();
        }
        internal static string ReadLine(Stream stream, int maximum, int timeoutMs)
        {
            var timer = Stopwatch.StartNew();
            using (var collected = new MemoryStream())
            {
                var buffer = new byte[4096];
                while (true)
                {
                    int remaining = timeoutMs - (int)timer.ElapsedMilliseconds;
                    if (remaining <= 0) throw new TimeoutException();
                    Task<int> read = stream.ReadAsync(buffer, 0, buffer.Length);
                    if (Task.WhenAny(read, Task.Delay(remaining)).GetAwaiter().GetResult() != read)
                    {
                        stream.Dispose();
                        read.ContinueWith(task => { var observed = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        throw new TimeoutException();
                    }
                    int count = read.GetAwaiter().GetResult();
                    if (count == 0) throw new FormatException("Expected newline.");
                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] == 10)
                        {
                            var data = collected.ToArray();
                            int length = data.Length;
                            if (length > 0 && data[length - 1] == 13) length--;
                            return new UTF8Encoding(false, true).GetString(data, 0, length);
                        }
                        if (collected.Length >= maximum) throw new FormatException("Line too long.");
                        collected.WriteByte(buffer[i]);
                    }
                }
            }
        }
        public void Dispose()
        {
            stopping = true;
            lock (pipes) foreach (var pipe in pipes) pipe.Dispose();
            foreach (var thread in threads) thread.Join(500);
        }
    }
}
