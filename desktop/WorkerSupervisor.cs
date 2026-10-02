// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Mansur.Next.Desktop
{
    internal enum WorkerSendResult { Accepted, Waiting, Unavailable }
    internal sealed class WorkerFailureNotice
    {
        internal long Epoch;
        internal long? DispatchedRequest;
        internal string Code;
        internal bool Recovering;
    }
    internal sealed class WorkerSupervisor : IDisposable
    {
        private sealed class Pending { internal object Command; internal long? Id; internal long Deadline; }
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread lifecycle;
        private readonly Func<Action<Dictionary<string, object>>, Action<string>, IWorkerEndpoint> factory;
        private readonly Action<long, Dictionary<string, object>> receive;
        private readonly Action<WorkerFailureNotice> failure;
        private readonly Action<long, string> requestFailure;
        private readonly Action<long, string> state;
        private readonly int waitMilliseconds, startMilliseconds, requestMilliseconds;
        private IWorkerEndpoint endpoint;
        private Pending pending;
        private long epoch, startedAt, dispatchedDeadline;
        private long? dispatched;
        private int remainingRetries = 1;
        private bool ready, restart, unavailable, stopping;
        internal WorkerSupervisor(Func<Action<Dictionary<string, object>>, Action<string>, IWorkerEndpoint> create,
            Action<long, Dictionary<string, object>> onEvent, Action<WorkerFailureNotice> onFailure,
            Action<long, string> onRequestFailure, Action<long, string> onState, int pendingTimeout = 30000, int startupTimeout = 120000, int requestTimeout = 90000)
        {
            factory = create; receive = onEvent; failure = onFailure; requestFailure = onRequestFailure; state = onState;
            waitMilliseconds = pendingTimeout; startMilliseconds = startupTimeout; requestMilliseconds = requestTimeout;
            lifecycle = new Thread(Run) { IsBackground = true, Name = "Learning lifecycle" }; lifecycle.Start();
        }
        internal void Start() { lock (gate) { if (stopping) return; restart = true; } wake.Set(); }
        internal bool IsCurrent(long value) { lock (gate) return !stopping && !unavailable && value == epoch; }
        internal bool IsLatestEpoch(long value) { lock (gate) return !stopping && value == epoch; }
        internal WorkerSendResult Send(object command, long? requestId)
        {
            lock (gate)
            {
                if (stopping || unavailable) return WorkerSendResult.Unavailable;
                pending = new Pending { Command = command, Id = requestId, Deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * waitMilliseconds / 1000 };
                wake.Set(); return ready ? WorkerSendResult.Accepted : WorkerSendResult.Waiting;
            }
        }
        internal void Restart()
        {
            long? dropped;
            lock (gate)
            {
                if (stopping) return;
                dropped = pending == null ? null : pending.Id; pending = null;
                remainingRetries = 1; ready = false; unavailable = false; restart = true; epoch++; dispatched = null;
            }
            if (dropped.HasValue) requestFailure(dropped.Value, "worker_restarted");
            wake.Set();
        }
        private void Event(long generation, Dictionary<string, object> message)
        {
            object kind, id;
            long? completed = null;
            bool global = !message.TryGetValue("request_id", out id) || id == null;
            if (global && message.TryGetValue("event", out kind) && Object.Equals(kind, "error"))
            { object code; Failed(generation, message.TryGetValue("code", out code) ? code as string : "worker_unavailable"); return; }
            lock (gate)
            {
                if (stopping || unavailable || generation != epoch) return;
                if (global && message.TryGetValue("event", out kind) && Object.Equals(kind, "ready")) ready = true;
                if (!global && message.TryGetValue("event", out kind) && (Object.Equals(kind, "done") || Object.Equals(kind, "error")) &&
                    (id is int || id is long) && dispatched == Convert.ToInt64(id)) completed = dispatched;
            }
            receive(generation, message);
            // Keep the active ID until the consumer has accepted completion. A simultaneous pipe failure
            // must still identify that request if its final event was discarded by the epoch check.
            if (completed.HasValue) lock (gate) { if (generation == epoch && dispatched == completed) dispatched = null; }
            wake.Set();
        }
        internal void RejectProtocol(long generation) { Failed(generation, "worker_protocol_error"); }
        private void Failed(long generation, string code)
        {
            WorkerFailureNotice notice; long? rejected = null;
            lock (gate)
            {
                if (stopping || generation != epoch || unavailable) return;
                bool recover = remainingRetries > 0 && code != "configuration_unavailable" && code != "api_key_missing" && code != "api_key_unreadable" &&
                    code != "api_proxy_auth_required" && code != "api_proxy_unsupported" && code != "api_proxy_resolution_failed";
                if (recover) remainingRetries--;
                ready = false; unavailable = !recover; restart = recover; epoch++;
                notice = new WorkerFailureNotice { Epoch = epoch, DispatchedRequest = dispatched, Code = code, Recovering = recover };
                dispatched = null;
                if (!recover) { rejected = pending == null ? null : pending.Id; pending = null; }
            }
            failure(notice);
            if (rejected.HasValue) requestFailure(rejected.Value, "worker_unavailable");
            wake.Set();
        }
        private void Run()
        {
            while (true)
            {
                IWorkerEndpoint retire = null; bool start = false, quit; long generation;
                Pending outgoing = null; long? timedOut = null; bool startupExpired = false, requestExpired = false;
                lock (gate)
                {
                    quit = stopping;
                    if (quit || restart || unavailable)
                    {
                        retire = endpoint; endpoint = null;
                        if (restart && !quit) { restart = false; start = true; epoch++; startedAt = Stopwatch.GetTimestamp(); }
                    }
                    generation = epoch;
                    if (pending != null && pending.Id.HasValue && Stopwatch.GetTimestamp() > pending.Deadline)
                    { timedOut = pending.Id; pending = null; }
                    if (!quit && !start && endpoint != null && ready && pending != null)
                    { outgoing = pending; pending = null; dispatched = outgoing.Id; dispatchedDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * requestMilliseconds / 1000; }
                    startupExpired = !quit && !start && endpoint != null && !ready && !unavailable &&
                        Stopwatch.GetTimestamp() - startedAt > Stopwatch.Frequency * startMilliseconds / 1000;
                    requestExpired = !quit && !start && dispatched.HasValue && Stopwatch.GetTimestamp() > dispatchedDeadline;
                }
                if (retire != null) try { retire.Dispose(); } catch (Exception error) when (error is InvalidOperationException || error is System.IO.IOException) { }
                if (quit) return;
                if (timedOut.HasValue) requestFailure(timedOut.Value, "request_wait_timeout");
                if (startupExpired) { Failed(generation, "worker_start_timeout"); continue; }
                if (requestExpired) { Failed(generation, "worker_request_timeout"); continue; }
                if (start)
                {
                    state(generation, "starting");
                    IWorkerEndpoint created = null;
                    try
                    {
                        created = factory(value => Event(generation, value), code => Failed(generation, code));
                        lock (gate)
                        {
                            if (!stopping && !unavailable && generation == epoch) { endpoint = created; created = null; }
                        }
                    }
                    catch (ModelConfigurationException error)
                    { Failed(generation, error.Code.StartsWith("api_proxy_", StringComparison.Ordinal) ? error.Code : "configuration_unavailable"); }
                    catch (Exception error) when (error is FormatException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
                    { Failed(generation, "configuration_unavailable"); }
                    catch (Exception error) when (error is System.IO.FileNotFoundException || error is System.IO.DirectoryNotFoundException)
                    { Failed(generation, "configuration_unavailable"); }
                    catch (Exception error) when (error is System.IO.IOException || error is ArgumentException || error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
                    { Failed(generation, "worker_unavailable"); }
                    finally { if (created != null) created.Dispose(); }
                    continue;
                }
                if (outgoing != null)
                {
                    IWorkerEndpoint target; lock (gate) target = !stopping && !unavailable && generation == epoch ? endpoint : null;
                    if (target != null)
                        try { if (!target.Send(outgoing.Command)) Failed(generation, "worker_unavailable"); }
                        catch (Exception error) when (error is System.IO.IOException || error is FormatException || error is ArgumentException || error is InvalidOperationException)
                        { Failed(generation, "worker_unavailable"); }
                }
                wake.WaitOne(100);
            }
        }
        // RequestStop is nonblocking; Join belongs to process teardown after the WinForms loop exits.
        internal void RequestStop()
        { lock (gate) { stopping = true; ready = false; pending = null; epoch++; } wake.Set(); }
        public void Dispose() { RequestStop(); if (Thread.CurrentThread != lifecycle) lifecycle.Join(8000); }
    }
}
