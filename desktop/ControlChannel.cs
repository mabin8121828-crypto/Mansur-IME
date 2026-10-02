// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Mansur.Next.Desktop
{
    internal sealed class ControlChannel : IDisposable
    {
        private readonly SecurityIdentifier user;
        private readonly Action<string> receive;
        private readonly Thread thread;
        private readonly object gate = new object();
        private NamedPipeServerStream active;
        private volatile bool stopping;
        internal static string Name(SecurityIdentifier user) { return Name(user, System.Diagnostics.Process.GetCurrentProcess().SessionId); }
        internal static string Name(SecurityIdentifier user, int session) { return "MansurNext.Control." + user.Value + ".Session." + session.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        internal static bool Valid(string command) { return command == "shutdown" || command == "show-settings" || command == "show-toolbar"; }
        internal ControlChannel(SecurityIdentifier sid, Action<string> onCommand)
        { user = sid; receive = onCommand; thread = new Thread(Listen) { IsBackground = true, Name = "Desktop control" }; thread.Start(); }
        internal static bool Send(SecurityIdentifier sid, string command)
        {
            if (!Valid(command)) return false;
            try
            {
                using (var pipe = new NamedPipeClientStream(".", Name(sid), PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(1500);
                    byte[] data = Encoding.ASCII.GetBytes(command + "\n");
                    var send = pipe.WriteAsync(data, 0, data.Length);
                    if (!send.Wait(1500)) return false;
                    return PipeListener.ReadLine(pipe, 16, 1500) == "accepted";
                }
            }
            catch (Exception error) when (error is IOException || error is TimeoutException || error is UnauthorizedAccessException || error is AggregateException || error is FormatException) { return false; }
        }
        private void Listen()
        {
            while (!stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(Name(user), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        1024, 1024, PipeListener.Security(user), HandleInheritability.None);
                    lock (gate) { if (stopping) { pipe.Dispose(); return; } active = pipe; }
                    pipe.WaitForConnection();
                    string command = PipeListener.ReadLine(pipe, 32, 1500);
                    if (Valid(command) && !stopping)
                    {
                        byte[] ack = Encoding.ASCII.GetBytes("accepted\n");
                        var sent = pipe.WriteAsync(ack, 0, ack.Length);
                        if (sent.Wait(1000)) receive(command);
                    }
                }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is UnauthorizedAccessException ||
                                               error is FormatException || error is TimeoutException || error is DecoderFallbackException || error is AggregateException)
                { if (pipe == null && !stopping) Thread.Sleep(250); }
                finally { lock (gate) active = null; if (pipe != null) pipe.Dispose(); }
            }
        }
        public void Dispose()
        {
            stopping = true;
            lock (gate) if (active != null) active.Dispose();
            if (Thread.CurrentThread != thread) thread.Join(600);
        }
    }
}
