// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace Mansur.Next.Desktop
{
    internal static class StabilityTests
    {
        private sealed class FakeMode : IInputModeTransport
        {
            internal InputModeSnapshot Current, Applied, Received;
            internal int Sets; internal bool Requested;
            public InputModeSnapshot Query() { return Current; }
            public InputModeSnapshot Set(InputModeSnapshot previous, bool chinese) { Received = previous; Requested = chinese; Sets++; return Applied; }
        }
        private sealed class FakeWorker : IWorkerEndpoint
        {
            private readonly Action<Dictionary<string, object>> output;
            private readonly Action<string> failure;
            internal readonly List<string> Commands = new List<string>();
            internal bool Disposed;
            internal FakeWorker(Action<Dictionary<string, object>> onEvent, Action<string> onFailure) { output = onEvent; failure = onFailure; }
            public bool Send(object command) { lock (Commands) { if (Disposed) return false; Commands.Add(Json.Write(command)); return true; } }
            internal int Count { get { lock (Commands) return Commands.Count; } }
            internal string Last { get { lock (Commands) return Commands.LastOrDefault(); } }
            internal void Ready() { Emit(new Dictionary<string, object> { { "event", "ready" }, { "request_id", null } }); }
            internal void Emit(Dictionary<string, object> data) { output(data); }
            internal void Fail() { failure("worker_exited"); }
            public void Dispose() { lock (Commands) Disposed = true; }
        }
        private static void Wait(Func<bool> condition)
        { if (!SpinWait.SpinUntil(condition, 2500)) throw new InvalidOperationException("stability condition timed out"); }
        private static ProcessStartInfo ChildStart(string scenario)
        {
            return new ProcessStartInfo { FileName = typeof(Program).Assembly.Location, Arguments = "--test-worker-child " + scenario,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
        }
        internal static int Child(string scenario)
        {
            if (scenario == "malformed") { Console.WriteLine("invalid-json"); Console.Out.Flush(); Thread.Sleep(5000); return 0; }
            if (scenario != "exit-on-command") return 2;
            Console.WriteLine("{\"event\":\"ready\",\"request_id\":null}"); Console.Out.Flush();
            Console.ReadLine(); return 7;
        }
        internal static void Run(Action<bool, string> check)
        {
            var mode = new FakeMode {
                Current = new InputModeSnapshot(new IntPtr(11), new IntPtr(22), 0xFFFFFFFD),
                Applied = new InputModeSnapshot(new IntPtr(11), new IntPtr(22), 0xFFFFFFFE)
            };
            check(InputModeCommands.Toggle(mode) == mode.Applied && !mode.Requested && mode.Received.Value == 0xFFFFFFFD, "mode-toggle-uses-fresh-endpoint-and-full-uint32-token");
            mode.Applied = null;
            check(InputModeCommands.Toggle(mode) == null, "mode-rejected-stale-focus-is-not-reported-as-English");
            mode.Current = null; int sets = mode.Sets;
            check(InputModeCommands.Toggle(mode) == null && mode.Sets == sets, "mode-no-focused-native-endpoint-does-not-set-global-mode");
            var runtime = new RuntimeStatus(1234); runtime.Ready("Vulkan0"); runtime.Unavailable("worker_exited");
            check(Json.Parse(runtime.Snapshot())["model_ready"].Equals(false), "fatal-worker-exit-clears-model-ready");
            runtime.Unavailable("worker_exited", 2, true); var recovery = Json.Parse(runtime.Snapshot());
            check(recovery["worker_state"].Equals("recovering") && Convert.ToInt64(recovery["worker_epoch"]) == 2 && recovery["automatic_restarts"].Equals(1), "fixed-worker-recovery-diagnostics-have-epoch-and-count-without-text");
            runtime.Ready("CPU"); runtime.Exit();
            check(Json.Parse(runtime.Snapshot())["model_ready"].Equals(false), "shutdown-clears-model-ready");

            using (var childFailed = new ManualResetEvent(false))
            using (var childReady = new ManualResetEvent(false))
            {
                int childId;
                using (var child = new WorkerProcess(ChildStart("exit-on-command"), e => childReady.Set(), code => childFailed.Set()))
                {
                    childId = child.ProcessId;
                    check(childReady.WaitOne(2500) && child.Send(new { op = "learn", request_id = 1 }), "real-fake-child-starts-and-accepts-command");
                    check(childFailed.WaitOne(2500) && !child.Send(new { op = "learn", request_id = 2 }), "exited-child-rejects-follow-up-instead-of-silent-queue");
                }
                bool exited; try { using (var process = Process.GetProcessById(childId)) exited = process.HasExited; } catch (ArgumentException) { exited = true; }
                check(exited, "owned-fake-child-is-reaped");
            }
            using (var malformed = new ManualResetEvent(false))
            using (var child = new WorkerProcess(ChildStart("malformed"), e => { }, code => malformed.Set()))
                check(malformed.WaitOne(2500) && !child.Send(new { op = "learn", request_id = 1 }), "worker-protocol-failure-rejects-new-requests");

            var gate = new object(); var instances = new List<FakeWorker>(); var notices = new List<WorkerFailureNotice>(); int outputs = 0;
            Func<Action<Dictionary<string, object>>, Action<string>, IWorkerEndpoint> factory = (events, failed) => {
                var endpoint = new FakeWorker(events, failed); lock (gate) instances.Add(endpoint); return endpoint;
            };
            Func<int> count = () => { lock (gate) return instances.Count; };
            Func<int, FakeWorker> instance = i => { lock (gate) return instances[i]; };
            using (var supervisor = new WorkerSupervisor(factory, (epoch, e) => Interlocked.Increment(ref outputs),
                notice => { lock (gate) notices.Add(notice); }, (id, code) => { }, (epoch, phase) => { }))
            {
                supervisor.Start(); Wait(() => count() == 1); var first = instance(0); first.Ready();
                check(supervisor.Send(new { op = "learn", request_id = 1 }, 1) == WorkerSendResult.Accepted, "supervisor-ready-accepts-request");
                Wait(() => first.Count == 1); first.Fail(); Wait(() => count() == 2);
                var second = instance(1); second.Ready();
                check(first.Disposed && notices.Count == 1 && notices[0].Recovering && notices[0].DispatchedRequest == 1, "one-unexpected-failure-restarts-owned-worker-and-identifies-failed-request");
                int before = Volatile.Read(ref outputs);
                first.Emit(new Dictionary<string, object> { { "event", "translation" }, { "request_id", 1 }, { "text", "Test." } });
                check(Volatile.Read(ref outputs) == before, "retired-worker-epoch-cannot-publish-stale-event");
                check(second.Count == 0, "automatic-restart-does-not-replay-old-sentence");
                supervisor.Send(new { op = "learn", request_id = 2 }, 2); Wait(() => second.Count == 1);
                check(second.Last.Contains("\"request_id\":2"), "fresh-request-succeeds-after-automatic-restart");
                second.Fail(); Wait(() => supervisor.Send(new { op = "cancel" }, null) == WorkerSendResult.Unavailable);
                check(count() == 2 && !notices.Last().Recovering, "automatic-restart-budget-stops-repeated-failures");
                supervisor.Restart(); Wait(() => count() == 3); var third = instance(2); third.Ready();
                supervisor.Send(new { op = "learn", request_id = 3 }, 3); Wait(() => third.Count == 1);
                check(third.Last.Contains("\"request_id\":3"), "explicit-manual-restart-restores-follow-up-work");
            }
            check(instances.All(i => i.Disposed), "supervisor-disposal-releases-all-owned-endpoints");

            FakeWorker waiting = null; long timedOut = 0;
            using (var supervisor = new WorkerSupervisor((events, failed) => waiting = new FakeWorker(events, failed), (epoch, e) => { }, n => { },
                (id, code) => Interlocked.Exchange(ref timedOut, id), (epoch, phase) => { }, pendingTimeout: 200))
            {
                supervisor.Start(); Wait(() => waiting != null);
                check(supervisor.Send(new { op = "learn", request_id = 10 }, 10) == WorkerSendResult.Waiting, "cold-worker-reports-bounded-waiting-state");
                supervisor.Send(new { op = "learn", request_id = 11 }, 11); waiting.Ready(); Wait(() => waiting.Count == 1);
                check(waiting.Last.Contains("\"request_id\":11"), "cold-start-slot-delivers-only-latest-unsent-request");
            }
            waiting = null;
            using (var supervisor = new WorkerSupervisor((events, failed) => waiting = new FakeWorker(events, failed), (epoch, e) => { }, n => { },
                (id, code) => Interlocked.Exchange(ref timedOut, id), (epoch, phase) => { }, pendingTimeout: 100))
            {
                supervisor.Start(); Wait(() => waiting != null); supervisor.Send(new { op = "learn", request_id = 12 }, 12);
                Wait(() => Interlocked.Read(ref timedOut) == 12); waiting.Ready();
                check(waiting.Count == 0, "timed-out-pending-request-is-not-replayed-after-ready");
            }
            waiting = null;
            using (var supervisor = new WorkerSupervisor((events, failed) => waiting = new FakeWorker(events, failed), (epoch, e) => { }, n => { },
                (id, code) => { }, (epoch, phase) => { }))
            {
                supervisor.Start(); Wait(() => waiting != null); supervisor.Send(new { op = "learn", request_id = 13 }, 13); supervisor.Send(new { op = "cancel" }, null);
                waiting.Ready(); Wait(() => waiting.Count == 1);
                check(waiting.Last == "{\"op\":\"cancel\"}", "cancel-removes-latest-cold-start-request");
            }
            waiting = null; long failedId = 0;
            using (var supervisor = new WorkerSupervisor((events, failed) => waiting = new FakeWorker(events, failed), (epoch, e) => { },
                n => { if (n.DispatchedRequest.HasValue) Interlocked.Exchange(ref failedId, n.DispatchedRequest.Value); },
                (id, code) => { }, (epoch, phase) => { }, requestTimeout: 100))
            {
                supervisor.Start(); Wait(() => waiting != null); waiting.Ready(); supervisor.Send(new { op = "learn", request_id = 14 }, 14);
                Wait(() => Interlocked.Read(ref failedId) == 14);
                check(true, "stalled-active-request-has-finite-watchdog-and-recovery");
            }
            int creations = 0, stopped = 0;
            using (var supervisor = new WorkerSupervisor((events, failed) => { Interlocked.Increment(ref creations); return new FakeWorker(events, failed); },
                (epoch, e) => { }, n => { if (!n.Recovering) Interlocked.Exchange(ref stopped, 1); }, (id, code) => { }, (epoch, phase) => { }, startupTimeout: 100))
            {
                supervisor.Start(); Wait(() => Volatile.Read(ref stopped) == 1);
                check(creations == 2 && supervisor.Send(new { op = "learn", request_id = 15 }, 15) == WorkerSendResult.Unavailable, "startup-timeout-retries-once-then-stops");
            }
        }
    }
}
