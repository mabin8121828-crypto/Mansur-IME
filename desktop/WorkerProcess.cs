// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Mansur.Next.Desktop
{
    internal interface IWorkerEndpoint : IDisposable { bool Send(object command); }
    internal sealed class WorkerProcess : IWorkerEndpoint
    {
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Action<Dictionary<string, object>> receive;
        private readonly Action<string> failed;
        private Process process;
        private StreamWriter input;
        private Thread sender, reader, drain;
        private string pending;
        private volatile bool stopping;
        private bool unavailable;
        internal int ProcessId { get { return process.Id; } }
        internal WorkerProcess(Configuration config, Action<Dictionary<string, object>> onEvent, Action<string> onFailure)
            : this(StartInfo(config), onEvent, onFailure) { }
        internal WorkerProcess(ProcessStartInfo start, Action<Dictionary<string, object>> onEvent, Action<string> onFailure)
        {
            receive = onEvent; failed = onFailure;
            process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new IOException("Worker start failed.");
                input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), 4096) { AutoFlush = true };
                sender = new Thread(SendLoop) { IsBackground = true, Name = "Learning command writer" };
                reader = new Thread(ReadLoop) { IsBackground = true, Name = "Learning event reader" };
                drain = new Thread(DrainError) { IsBackground = true, Name = "Learning stderr drain" };
                sender.Start(); reader.Start(); drain.Start();
            }
            catch { Dispose(); throw; }
        }
        internal static ProcessStartInfo StartInfo(Configuration config, Func<ApiProxySelection> resolveProxy = null)
        {
            string provider = " --translation-provider " + Quote(config.TranslationProvider);
            if (config.TranslationProvider == "openrouter")
            {
                provider += " --openrouter-model " + Quote(config.ApiModel) + " --openrouter-key-file " + Quote(Path.Combine(UserStateDirectory.Root, ApiCredentialStore.FileName));
                try
                {
                    ApiProxySelection proxy = resolveProxy == null ? ApiProxySelection.Resolve(CancellationToken.None) : resolveProxy();
                    if (!proxy.Direct) provider += " --api-proxy-host " + Quote(proxy.Host) + " --api-proxy-port " + proxy.Port.ToString(CultureInfo.InvariantCulture);
                }
                catch (ModelConfigurationException error) when (error.Code == "api_proxy_auth_required" || error.Code == "api_proxy_unsupported" || error.Code == "api_proxy_resolution_failed")
                {
                    // Do not fall back to direct networking; fail only requests needing translation.
                    provider += " --translation-config-error " + Quote(error.Code);
                }
            }
            else if (config.TranslationProvider == "compatible") provider += ServiceArguments(ApiServices.Find(config.Services, config.TranslationServiceId, "translation"), "translation", resolveProxy);
            else provider += " --llama-server " + Quote(config.LlamaServer) + " --translation-model " + Quote(config.TranslationModel);
            provider += " --speech-provider " + Quote(config.SpeechProvider);
            if (config.SpeechProvider == "compatible") provider += ServiceArguments(ApiServices.Find(config.Services, config.SpeechServiceId, "speech"), "speech", resolveProxy);
            return new ProcessStartInfo {
                FileName = config.Python,
                Arguments = "-B -X utf8 -u " + Quote(config.Worker) + provider + " --voice-model-dir " + Quote(config.VoiceModelDir) +
                    " --device " + Quote(config.Device) + " --gpu-layers " + config.GpuLayers.ToString(CultureInfo.InvariantCulture) +
                    " --threads " + config.Threads.ToString(CultureInfo.InvariantCulture) + " --translation-timeout " + config.TranslationTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " --port 0",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, false)
            };
        }
        private static string ServiceArguments(ApiServiceProfile profile, string capability, Func<ApiProxySelection> resolveProxy)
        {
            try { ApiServices.Validate(profile, true); }
            catch (ModelConfigurationException error) { return " --" + capability + "-config-error " + Quote(error.Code); }
            string prefix = " --" + capability + "-api-";
            string keyFile = profile.Id == ApiServices.LegacyId ? ApiCredentialStore.FileName : "service-" + profile.Id + ".key.dpapi";
            string value = prefix + "base-url " + Quote(ApiServices.Endpoint(profile.BaseUrl).AbsoluteUri.TrimEnd('/')) + prefix + "model " + Quote(profile.Model) +
                prefix + "service-id " + Quote(profile.Id) + prefix + "key-file " + Quote(Path.Combine(UserStateDirectory.Root, keyFile));
            if (capability == "translation" && !profile.Stream) value += " --translation-api-no-stream";
            if (capability == "speech") value += prefix + "voice " + Quote(profile.Voice) + prefix + "format " + Quote(profile.AudioFormat) + prefix + "sample-rate " + profile.SampleRate.ToString(CultureInfo.InvariantCulture);
            try {
                var target = new Uri(profile.BaseUrl.TrimEnd('/') + (capability == "speech" ? "/audio/speech" : "/chat/completions"));
                var proxy = resolveProxy == null ? ApiProxySelection.Resolve(target, CancellationToken.None) : resolveProxy();
                if (!proxy.Direct) value += prefix + "proxy-host " + Quote(proxy.Host) + prefix + "proxy-port " + proxy.Port.ToString(CultureInfo.InvariantCulture);
            } catch (ModelConfigurationException error) { value += " --" + capability + "-config-error " + Quote(error.Code); }
            return value;
        }
        // Windows argument quoting: every backslash before a quote/end quote is doubled.
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { result.Append('\\', slashes * 2 + 1); result.Append(ch); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(ch);
            }
            result.Append('\\', slashes * 2); result.Append('"');
            return result.ToString();
        }
        public bool Send(object command)
        {
            string line = Json.Write(command);
            if (Encoding.UTF8.GetByteCount(line) > 8191) throw new FormatException("Worker command too large.");
            lock (gate) { if (stopping || unavailable) return false; pending = line; }
            wake.Set(); return true;
        }
        private void Fail(string code)
        {
            lock (gate) { if (stopping || unavailable) return; unavailable = true; pending = null; }
            failed(code);
        }
        private void SendLoop()
        {
            try
            {
                while (true)
                {
                    wake.WaitOne();
                    string command;
                    lock (gate)
                    {
                        command = stopping ? "{\"op\":\"close\"}" : pending;
                        pending = null;
                    }
                    if (command != null) input.WriteLine(command);
                    if (stopping) return;
                }
            }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is InvalidOperationException)
            { Fail("worker_unavailable"); }
        }
        private void ReadLoop()
        {
            try
            {
                while (!stopping)
                {
                    string line = ReadBounded(process.StandardOutput, 262144);
                    if (line == null) break;
                    receive(Json.Parse(line));
                }
                Fail("worker_exited");
            }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is FormatException ||
                                          error is ArgumentException || error is InvalidOperationException)
            { Fail("worker_protocol_error"); }
        }
        internal static string ReadBounded(TextReader reader, int maximum)
        {
            var line = new StringBuilder();
            while (true)
            {
                int value = reader.Read();
                if (value < 0) { if (line.Length != 0) throw new FormatException("Truncated worker event."); return null; }
                if (value == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= maximum) throw new FormatException("Worker event too large.");
                line.Append((char)value);
            }
        }
        private void DrainError()
        {
            try { var buffer = new char[4096]; while (!stopping && process.StandardError.Read(buffer, 0, buffer.Length) > 0) { } }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is InvalidOperationException) { }
        }
        public void Dispose()
        {
            lock (gate) { if (stopping) return; stopping = true; pending = null; }
            wake.Set();
            // This process was created by us; worker owns a kill-on-close job for its model child.
            try { if (!process.WaitForExit(1800)) process.Kill(); }
            catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception) { }
            try { process.WaitForExit(500); } catch (InvalidOperationException) { }
            // Closing the pipe never flushes a StreamWriter against an unresponsive child.
            try { if (input != null) input.BaseStream.Dispose(); } catch (IOException) { }
            if (sender != null && sender.IsAlive && sender != Thread.CurrentThread) sender.Join(300);
            if (reader != null && reader.IsAlive && reader != Thread.CurrentThread) reader.Join(300);
            if (drain != null && drain.IsAlive && drain != Thread.CurrentThread) drain.Join(300);
            process.Dispose();
            // Threads are background-only; wake remains valid if an OS pipe close is delayed.
        }
    }
}
