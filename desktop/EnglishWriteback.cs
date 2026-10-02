// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Mansur.Next.Desktop
{
    internal sealed class EnglishWritebackTarget
    {
        internal readonly IntPtr Endpoint;
        internal readonly uint ProcessId;
        internal readonly string Token;
        internal EnglishWritebackTarget(IntPtr endpoint, uint processId, string token)
        { Endpoint = endpoint; ProcessId = processId; Token = token; }
        internal static EnglishWritebackTarget ParseOptional(object raw, string sender)
        {
            var fields = raw as Dictionary<string, object>;
            if (fields == null || sender == null) return null;
            object endpoint, token; ulong address; uint pid;
            int colon = sender.IndexOf(':'); Guid identity;
            if (colon <= 0 || !UInt32.TryParse(sender.Substring(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid == 0 ||
                !Guid.TryParseExact(sender.Substring(colon + 1), "B", out identity) ||
                !fields.TryGetValue("endpoint", out endpoint) || !(endpoint is string) ||
                !UInt64.TryParse((string)endpoint, NumberStyles.None, CultureInfo.InvariantCulture, out address) || address == 0 || address > Int64.MaxValue ||
                !fields.TryGetValue("token", out token) || !ValidToken(token as string)) return null;
            return new EnglishWritebackTarget(new IntPtr((long)address), pid, (string)token);
        }
        internal static bool ValidToken(string token)
        {
            if (token == null || token.Length != 32) return false;
            foreach (char c in token) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
    }
    internal enum EnglishWritebackResult { Unavailable = 0, Pending = 1, Written = 2, Rejected = 3, Unknown = 4 }
    internal enum WritebackCheck { None, NoCapability, EndpointIdentity, EndpointClass, ForegroundProcess, TransportTimeout, NativeUnavailable, NativeReady, NativeWritten, NativeRejected, NativeUnknown }
    internal static class EnglishWritebackFeedback
    {
        internal static string Message(EnglishWritebackResult result)
        {
            if (result == EnglishWritebackResult.Written) return "";
            if (result == EnglishWritebackResult.Unknown || result == EnglishWritebackResult.Pending)
                return "暂时无法确认是否写入，请检查输入框。本次不会重复写入。";
            return "未写入英文：原句或输入位置已改变，或软件未提供可写入的位置。请重新确认一句后再点“使用英文”。";
        }
    }
    internal interface IEnglishWritebackTransport
    {
        EnglishWritebackResult Send(EnglishWritebackTarget target, int operation, int mode, string text);
    }
    internal static class EnglishWritebackPacket
    {
        internal const uint Signature = 0x4D4E5752;
        internal static byte[] Create(EnglishWritebackTarget target, int operation, int mode, string text)
        {
            text = text ?? "";
            if (target == null || !EnglishWritebackTarget.ValidToken(target.Token) || operation < 1 || operation > 3 || mode < 0 || mode > 1 ||
                (operation == 2 ? String.IsNullOrWhiteSpace(text) : text.Length != 0) || text.Length > 4096) throw new ArgumentException("Invalid writeback packet.");
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\0' || (Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')) throw new ArgumentException("Invalid writeback text.");
                if (Char.IsHighSurrogate(c)) { if (++i >= text.Length || !Char.IsLowSurrogate(text[i])) throw new ArgumentException("Invalid writeback Unicode."); }
                else if (Char.IsLowSurrogate(c)) throw new ArgumentException("Invalid writeback Unicode.");
            }
            byte[] packet = new byte[48 + text.Length * 2];
            Buffer.BlockCopy(BitConverter.GetBytes(1U), 0, packet, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)operation), 0, packet, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)mode), 0, packet, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)text.Length), 0, packet, 12, 4);
            Encoding.ASCII.GetBytes(target.Token, 0, 32, packet, 16);
            Encoding.Unicode.GetBytes(text, 0, text.Length, packet, 48);
            return packet;
        }
    }
    internal sealed class NativeEnglishWriteback : IEnglishWritebackTransport
    {
        private const string EndpointClass = "Mansur.Next.Writeback.v1";
        internal WritebackCheck LastCheck { get; private set; }
        internal uint TargetPid { get; private set; }
        internal uint ForegroundPid { get; private set; }
        private EnglishWritebackResult Checked(WritebackCheck check, EnglishWritebackResult result)
        { LastCheck = check; return result; }
        public EnglishWritebackResult Send(EnglishWritebackTarget target, int operation, int mode, string text)
        {
            TargetPid = target == null ? 0 : target.ProcessId; ForegroundPid = 0;
            if (target == null) return Checked(WritebackCheck.NoCapability, EnglishWritebackResult.Unavailable);
            uint pid; GetWindowThreadProcessId(target.Endpoint, out pid);
            var name = new StringBuilder(128);
            if (pid != target.ProcessId) return Checked(WritebackCheck.EndpointIdentity, EnglishWritebackResult.Unavailable);
            if (GetClassName(target.Endpoint, name, name.Capacity) == 0 || name.ToString() != EndpointClass)
                return Checked(WritebackCheck.EndpointClass, EnglishWritebackResult.Unavailable);
            IntPtr foreground = GetForegroundWindow(); uint foregroundPid;
            GetWindowThreadProcessId(foreground, out foregroundPid);
            ForegroundPid = foregroundPid;
            if (operation != 3 && (foreground == IntPtr.Zero || foregroundPid != pid))
                return Checked(WritebackCheck.ForegroundProcess, EnglishWritebackResult.Unavailable);
            byte[] bytes = EnglishWritebackPacket.Create(target, operation, mode, text);
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var packet = new CopyData { Kind = new UIntPtr(EnglishWritebackPacket.Signature), Bytes = (uint)bytes.Length, Data = pin.AddrOfPinnedObject() };
                UIntPtr result;
                // WM_COPYDATA is marshalled by Windows across x86/x64; no raw
                // pointer is sent in a registered application message.
                if (SendMessageTimeout(target.Endpoint, 0x004A, UIntPtr.Zero, ref packet, 3, 80, out result) == IntPtr.Zero)
                    return Checked(WritebackCheck.TransportTimeout, EnglishWritebackResult.Unknown);
                ulong code = result.ToUInt64();
                return Checked(code == 0 ? WritebackCheck.NativeUnavailable : code == 1 ? WritebackCheck.NativeReady :
                    code == 2 ? WritebackCheck.NativeWritten : code == 3 ? WritebackCheck.NativeRejected : WritebackCheck.NativeUnknown,
                    code <= 4 ? (EnglishWritebackResult)code : EnglishWritebackResult.Unknown);
            }
            finally { pin.Free(); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct CopyData { internal UIntPtr Kind; internal uint Bytes; internal IntPtr Data; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, ref CopyData packet, uint flags, uint timeout, out UIntPtr result);
    }
    internal static class EnglishWritebackCommand
    {
        // At most one mutation. After a send timeout only observe status; never
        // resend text into an editor with an ambiguous first outcome.
        internal static EnglishWritebackResult Execute(IEnglishWritebackTransport transport, EnglishWritebackTarget target, int mode, string text,
            Func<bool> stillCurrent, Action wait = null, int polls = 50)
        {
            if (target == null || !stillCurrent()) return EnglishWritebackResult.Unavailable;
            if (transport.Send(target, 1, mode, "") != EnglishWritebackResult.Pending || !stillCurrent()) return EnglishWritebackResult.Unavailable;
            var answer = transport.Send(target, 2, mode, text);
            if (answer == EnglishWritebackResult.Written || answer == EnglishWritebackResult.Rejected || answer == EnglishWritebackResult.Unavailable) return answer;
            for (int i = 0; i < polls; i++)
            {
                if (wait == null) Thread.Sleep(30); else wait();
                answer = transport.Send(target, 3, mode, "");
                if (answer == EnglishWritebackResult.Written || answer == EnglishWritebackResult.Rejected) return answer;
                // A missing/destroyed endpoint after mutation was submitted does
                // not prove that nothing was written.
                if (answer == EnglishWritebackResult.Unavailable) return EnglishWritebackResult.Unknown;
            }
            return EnglishWritebackResult.Unknown;
        }
    }
}
