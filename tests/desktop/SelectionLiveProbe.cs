// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// A normal-user disposable worker, a fixed own-English selection, no editor or audio playback.
internal static class SelectionLiveProbe
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly object Gate = new object();
    private static readonly List<Dictionary<string, object>> Events = new List<Dictionary<string, object>>();
    private static string Failure;
    private static void Receive(Dictionary<string, object> value) { lock (Gate) Events.Add(value); }
    private static void Fail(string code) { lock (Gate) Failure = code; }
    private static Dictionary<string, object> Wait(string kind, long? id)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 60) {
            lock (Gate) {
                if (Failure != null) throw new InvalidOperationException(Failure);
                foreach (var value in Events) {
                    object raw, eventKind;
                    long? valueId = value.TryGetValue("request_id", out raw) && raw != null ? (long?)Convert.ToInt64(raw) : null;
                    if (valueId == id && value.TryGetValue("event", out eventKind)) {
                        if (Object.Equals(eventKind, "error")) throw new InvalidOperationException("fixed_worker_error");
                        if (Object.Equals(eventKind, kind)) return value;
                    }
                }
            }
            Thread.Sleep(20);
        }
        throw new TimeoutException("fixed_selection_timeout");
    }
    private static int Main(string[] args)
    {
        if (args.Length != 3 || !Path.IsPathRooted(args[0]) || !Path.IsPathRooted(args[1]) || !Path.IsPathRooted(args[2])) return 2;
        var json = new JavaScriptSerializer();
        Directory.CreateDirectory(args[2]); object endpoint = null;
        var report = new Dictionary<string, object> { { "status", "FAIL" }, { "scope", "Fixed own English selection via production WorkerProcess and SelectionLearning; API explanation and PCM generation, no playback, editor, clipboard or user UI." } };
        try {
            var assembly = Assembly.LoadFrom(args[0]);
            string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mansur-next", "local-models.json");
            var configValues = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(configPath));
            configValues["worker"] = args[1];
            string temporaryConfig = Path.Combine(args[2], "fixed-config.json");
            File.WriteAllText(temporaryConfig, json.Serialize(configValues), new UTF8Encoding(false));
            var configType = assembly.GetType("Mansur.Next.Desktop.Configuration", true);
            object config = configType.GetMethod("Load", Hidden).Invoke(null, new object[] { temporaryConfig });
            var endpointType = assembly.GetType("Mansur.Next.Desktop.WorkerProcess", true);
            var info = (ProcessStartInfo)endpointType.GetMethod("StartInfo", Hidden).Invoke(null, new object[] { config, null });
            endpoint = Activator.CreateInstance(endpointType, Hidden, null,
                new object[] { info, (Action<Dictionary<string, object>>)Receive, (Action<string>)Fail }, null);
            Wait("ready", null);
            const string source = "Tonight I will land at Victoria Square.";
            var spanType = assembly.GetType("Mansur.Next.Desktop.SelectionSpan", true);
            var stateType = assembly.GetType("Mansur.Next.Desktop.SelectionLearning", true);
            object span = spanType.GetMethod("Create", Hidden).Invoke(null, new object[] { source, source.IndexOf("Victoria", StringComparison.Ordinal), 8 });
            object state = Activator.CreateInstance(stateType, true);
            stateType.GetMethod("Bound", Hidden).Invoke(state, new object[] { span });
            var begin = stateType.GetMethod("Begin", Hidden); var send = endpointType.GetMethod("Send");
            object command = begin.Invoke(state, new object[] { false, "af_heart", 1.0 });
            long id = (long)stateType.GetProperty("Id", Hidden).GetValue(state);
            var clock = Stopwatch.StartNew();
            if (!(bool)send.Invoke(endpoint, new[] { command })) throw new InvalidOperationException("fixed_send_failed");
            var definition = (Dictionary<string, object>)Wait("selection_result", id)["result"];
            stateType.GetMethod("Result", Hidden).Invoke(state, new object[] { definition }); Wait("done", id);
            stateType.GetMethod("Done", Hidden).Invoke(state, null);
            report["meaning_ms"] = clock.ElapsedMilliseconds; report["fixed_definition"] = definition;
            command = begin.Invoke(state, new object[] { true, "af_heart", 0.75 });
            id = (long)stateType.GetProperty("Id", Hidden).GetValue(state); clock.Restart();
            if (!(bool)send.Invoke(endpoint, new[] { command })) throw new InvalidOperationException("fixed_speech_send_failed");
            var text = (string)Wait("translation", id)["text"];
            stateType.GetMethod("ConfirmSpeech", Hidden).Invoke(state, new object[] { text }); Wait("done", id);
            int bytes = 0;
            lock (Gate) foreach (var value in Events) {
                object raw;
                if (value.TryGetValue("request_id", out raw) && raw != null && Convert.ToInt64(raw) == id && Object.Equals(value["event"], "audio"))
                    bytes += ((byte[])stateType.GetMethod("ReadAudio", Hidden).Invoke(state, new object[] { value })).Length;
            }
            stateType.GetMethod("Done", Hidden).Invoke(state, null);
            report["speech_ms"] = clock.ElapsedMilliseconds; report["speech_original_text"] = text == "Victoria";
            report["pcm_bytes"] = bytes; report["slow_speed"] = 0.75; report["status"] = "PASS";
        } catch (Exception error) {
            while (error.InnerException != null) error = error.InnerException;
            report["error_type"] = error.GetType().Name;
            report["fixed_error_code"] = System.Text.RegularExpressions.Regex.IsMatch(error.Message, "^[a-z_]{1,64}$") ? error.Message : "fixed_probe_failed";
        } finally { if (endpoint != null) ((IDisposable)endpoint).Dispose(); }
        File.WriteAllText(Path.Combine(args[2], "selection-live.json"), json.Serialize(report), new UTF8Encoding(false));
        return Object.Equals(report["status"], "PASS") ? 0 : 1;
    }
}
