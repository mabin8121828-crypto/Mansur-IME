// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;

namespace Mansur.Next.Desktop
{
    internal static class EnglishWritebackTests
    {
        private sealed class Fake : IEnglishWritebackTransport
        {
            internal readonly Queue<EnglishWritebackResult> Answers = new Queue<EnglishWritebackResult>();
            internal readonly List<int> Operations = new List<int>();
            public EnglishWritebackResult Send(EnglishWritebackTarget target, int operation, int mode, string text)
            { Operations.Add(operation); return Answers.Count > 0 ? Answers.Dequeue() : EnglishWritebackResult.Pending; }
        }
        internal static void Run(Action<bool, string> check)
        {
            const string token = "0123456789abcdef0123456789abcdef";
            var source = new Dictionary<string, object> { { "endpoint", "1234" }, { "token", token } };
            var target = EnglishWritebackTarget.ParseOptional(source, "42:{11111111-1111-1111-1111-111111111111}");
            check(target != null && target.Endpoint.ToInt64() == 1234 && target.ProcessId == 42,
                "writeback-capability-parses-bounded-native-endpoint-and-sender");
            check(EnglishWritebackTarget.ParseOptional(source, "not-a-process") == null &&
                EnglishWritebackTarget.ParseOptional(new Dictionary<string, object> { { "endpoint", "-1" }, { "token", token } }, "42:{11111111-1111-1111-1111-111111111111}") == null &&
                !EnglishWritebackTarget.ValidToken(token.ToUpperInvariant()), "writeback-malformed-optional-capability-does-not-authorize-editor-write");
            byte[] packet = EnglishWritebackPacket.Create(target, 2, 1, "Hello");
            check(packet.Length == 58 && BitConverter.ToUInt32(packet, 0) == 1 && BitConverter.ToUInt32(packet, 4) == 2 &&
                BitConverter.ToUInt32(packet, 8) == 1 && BitConverter.ToUInt32(packet, 12) == 5 &&
                Encoding.ASCII.GetString(packet, 16, 32) == token && Encoding.Unicode.GetString(packet, 48, 10) == "Hello",
                "writeback-packet-matches-fixed-cross-architecture-wire-contract");
            var fake = new Fake(); fake.Answers.Enqueue(EnglishWritebackResult.Pending); fake.Answers.Enqueue(EnglishWritebackResult.Unknown);
            fake.Answers.Enqueue(EnglishWritebackResult.Pending); fake.Answers.Enqueue(EnglishWritebackResult.Written);
            check(EnglishWritebackCommand.Execute(fake, target, 0, "Hello", () => true, () => { }) == EnglishWritebackResult.Written &&
                String.Join(",", fake.Operations) == "1,2,3,3", "writeback-send-timeout-only-queries-status-and-never-retries-mutation");
            fake = new Fake(); fake.Answers.Enqueue(EnglishWritebackResult.Pending); fake.Answers.Enqueue(EnglishWritebackResult.Pending); fake.Answers.Enqueue(EnglishWritebackResult.Unavailable);
            check(EnglishWritebackCommand.Execute(fake, target, 1, "Hello", () => true, () => { }) == EnglishWritebackResult.Unknown,
                "writeback-disappearing-endpoint-after-apply-remains-ambiguous");
            fake = new Fake(); fake.Answers.Enqueue(EnglishWritebackResult.Pending); int checks = 0;
            check(EnglishWritebackCommand.Execute(fake, target, 0, "Hello", () => ++checks == 1, () => { }) == EnglishWritebackResult.Unavailable && fake.Operations.Count == 1,
                "writeback-new-request-before-apply-cancels-without-mutation");
            fake = new Fake(); fake.Answers.Enqueue(EnglishWritebackResult.Pending);
            check(EnglishWritebackCommand.Execute(fake, target, 0, "Hello", () => true, () => { }, 2) == EnglishWritebackResult.Unknown &&
                String.Join(",", fake.Operations) == "1,2,3,3", "writeback-pending-timeout-never-offers-automatic-repeat");
            fake = new Fake(); fake.Answers.Enqueue(EnglishWritebackResult.Unavailable);
            check(EnglishWritebackCommand.Execute(fake, target, 1, "Hello", () => true, () => { }) == EnglishWritebackResult.Unavailable &&
                String.Join(",", fake.Operations) == "1", "writeback-click-on-expired-lease-returns-feedback-without-applying");
            check(EnglishWritebackFeedback.Message(EnglishWritebackResult.Written).Length == 0 &&
                EnglishWritebackFeedback.Message(EnglishWritebackResult.Unavailable).Contains("未写入英文") &&
                EnglishWritebackFeedback.Message(EnglishWritebackResult.Rejected).Contains("未写入英文") &&
                EnglishWritebackFeedback.Message(EnglishWritebackResult.Unknown).Contains("不会重复写入"),
                "writeback-failed-click-has-visible-feedback-including-unknown-no-retry");
            var runtime = new RuntimeStatus(12345); runtime.Accepted(1); runtime.WritebackClick();
            check(Json.Parse(runtime.Snapshot())["writeback_state"].Equals("checking") &&
                Json.Parse(runtime.Snapshot())["writeback_clicks"].Equals(1), "writeback-diagnostic-counts-explicit-click-not-idle-probes");
            runtime.WritebackFinished(EnglishWritebackResult.Rejected); runtime.Cancel(); runtime.Accepted(2);
            check(Json.Parse(runtime.Snapshot())["writeback_state"].Equals("rejected") &&
                Json.Parse(runtime.Snapshot())["writeback_clicks"].Equals(1), "writeback-fixed-result-survives-feedback-typing-and-next-request");
            runtime.WritebackFinished((EnglishWritebackResult)99);
            check(Json.Parse(runtime.Snapshot())["writeback_state"].Equals("unknown"), "writeback-diagnostics-only-allow-fixed-labels");
            runtime.WritebackTrace(WritebackCheck.NoCapability, 42, 19); runtime.Cancel();
            check(Json.Parse(runtime.Snapshot())["writeback_check"].Equals("NoCapability") &&
                Json.Parse(runtime.Snapshot())["writeback_target_pid"].Equals(42) &&
                Json.Parse(runtime.Snapshot())["writeback_foreground_pid"].Equals(19), "writeback-trace-contains-fixed-stage-and-numeric-identities-only");
            runtime.WritebackTrace((WritebackCheck)999, 0, 0);
            check(Json.Parse(runtime.Snapshot())["writeback_check"].Equals("NativeUnknown"), "writeback-trace-rejects-unrecognized-stage");
        }
    }
}
