// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mansur.Next.Desktop
{
    internal sealed class InputModeSnapshot
    {
        internal readonly IntPtr Endpoint, Foreground;
        internal readonly uint Value;
        internal bool Chinese { get { return (Value & 1) != 0; } }
        internal InputModeSnapshot(IntPtr endpoint, IntPtr foreground, uint value)
        { Endpoint = endpoint; Foreground = foreground; Value = value; }
    }
    internal interface IInputModeTransport
    {
        InputModeSnapshot Query();
        InputModeSnapshot Set(InputModeSnapshot previous, bool chinese);
    }
    internal sealed class NativeInputMode : IInputModeTransport
    {
        private const string EndpointClass = "Mansur.Next.Mode.v1";
        private static readonly uint QueryMessage = RegisterWindowMessage("Mansur.Next.Mode.Query.v1");
        private static readonly uint SetMessage = RegisterWindowMessage("Mansur.Next.Mode.Set.v1");
        public InputModeSnapshot Query()
        {
            IntPtr foreground = GetForegroundWindow(); uint foregroundPid;
            GetWindowThreadProcessId(foreground, out foregroundPid);
            if (foreground == IntPtr.Zero || foregroundPid == 0) return null;
            var timer = Stopwatch.StartNew(); IntPtr endpoint = IntPtr.Zero;
            for (int i = 0; i < 64 && timer.ElapsedMilliseconds < 120; i++)
            {
                endpoint = FindWindowEx(new IntPtr(-3), endpoint, EndpointClass, null);
                if (endpoint == IntPtr.Zero) break;
                uint pid; GetWindowThreadProcessId(endpoint, out pid);
                if (pid != foregroundPid) continue;
                UIntPtr raw;
                if (SendMessageTimeout(endpoint, QueryMessage, UIntPtr.Zero, IntPtr.Zero, 3, 50, out raw) == IntPtr.Zero) continue;
                uint value = unchecked((uint)raw.ToUInt64());
                if (value > 1 && GetForegroundWindow() == foreground) return new InputModeSnapshot(endpoint, foreground, value);
            }
            return null;
        }
        public InputModeSnapshot Set(InputModeSnapshot previous, bool chinese)
        {
            if (previous == null || GetForegroundWindow() != previous.Foreground) return null;
            UIntPtr raw;
            if (SendMessageTimeout(previous.Endpoint, SetMessage, new UIntPtr(previous.Value), new IntPtr(chinese ? 1 : 0), 3, 50, out raw) == IntPtr.Zero) return null;
            uint value = unchecked((uint)raw.ToUInt64());
            return value > 1 && GetForegroundWindow() == previous.Foreground ? new InputModeSnapshot(previous.Endpoint, previous.Foreground, value) : null;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    }
    // A fresh query is mandatory for each explicit click; no registry value is a source of truth.
    internal static class InputModeCommands
    {
        internal static InputModeSnapshot Toggle(IInputModeTransport transport)
        {
            var current = transport.Query();
            if (current == null) return null;
            var applied = transport.Set(current, !current.Chinese);
            return applied != null && applied.Chinese != current.Chinese ? applied : null;
        }
    }
}
