// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mansur.Next.Desktop
{
    // Isolated files, synthetic credentials and injected HTTP responses only.
    internal static class ModelProviderTests
    {
        private const string TestKey = "only-a-synthetic-provider-test-key";
        private sealed class FakeProbe : IOpenRouterProbe
        {
            internal readonly List<string> Paths = new List<string>();
            internal bool CorrectKey = true, PublicModels = true, Limits = true;
            internal string Model = "fake/test-model", Error = "";
            internal bool Block;
            public async Task<Dictionary<string, object>> GetAsync(string path, string key, int maximum, CancellationToken cancel)
            {
                Paths.Add(path); CorrectKey &= path != "/api/v1/key" || key == TestKey;
                PublicModels &= path != "/api/v1/models" || key == null;
                Limits &= maximum == (path == "/api/v1/key" ? 65536 : 4 * 1024 * 1024);
                if (Block) await Task.Delay(5000, cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                if (Error.Length > 0) throw new ModelConfigurationException(Error);
                return path == "/api/v1/key" ? new Dictionary<string, object> { { "data", new Dictionary<string, object>() } } :
                    new Dictionary<string, object> { { "data", new object[] { new Dictionary<string, object> { { "id", Model } } } } };
            }
        }
        private sealed class ReadyEndpoint : IWorkerEndpoint
        {
            internal readonly Action<Dictionary<string, object>> Emit;
            internal ReadyEndpoint(Action<Dictionary<string, object>> emit) { Emit = emit; }
            public bool Send(object value) { return true; }
            public void Dispose() { }
        }
        private sealed class FakeProxy : IWebProxy
        {
            internal Uri Route, Requested;
            public ICredentials Credentials { get; set; }
            public Uri GetProxy(Uri destination) { Requested = destination; return Route ?? destination; }
            public bool IsBypassed(Uri destination) { return Route == null; }
        }
        private static bool Reject(Action action, string code)
        { try { action(); return false; } catch (ModelConfigurationException error) { return error.Code == code; } }
        internal static void Run(Action<bool, string> check)
        {
            var fakeProxy = new FakeProxy();
            check(ApiProxySelection.From(fakeProxy).Direct && fakeProxy.Requested == ApiProxySelection.Target && ApiProxySelection.From(null).ToWebProxy() == null,
                "models-proxy-direct-is-explicit-and-resolves-fixed-chat-target");
            fakeProxy.Route = new Uri("http://127.0.0.1:8123");
            var route = ApiProxySelection.From(fakeProxy);
            check(!route.Direct && route.Host == "127.0.0.1" && route.Port == 8123 && route.ToWebProxy().GetProxy(ApiProxySelection.Target) == fakeProxy.Route,
                "models-proxy-pac-result-pins-identical-worker-and-check-route");
            check(route.ToWebProxy().Credentials == null && route.ToWebProxy().GetProxy(new Uri("https://openrouter.ai/api/v1/key")) ==
                route.ToWebProxy().GetProxy(new Uri("https://openrouter.ai/api/v1/models")), "models-proxy-both-check-paths-share-route-without-credentials");
            fakeProxy.Route = new Uri("socks5://127.0.0.1:8123");
            check(Reject(() => ApiProxySelection.From(fakeProxy), "api_proxy_unsupported"), "models-proxy-unsupported-scheme-never-falls-back-direct");
            fakeProxy.Route = new Uri("http://username:password@127.0.0.1:8123");
            check(Reject(() => ApiProxySelection.From(fakeProxy), "api_proxy_auth_required"), "models-proxy-uri-credentials-are-rejected");
            fakeProxy.Route = new Uri("http://127.0.0.1:8123"); fakeProxy.Credentials = new NetworkCredential("synthetic", "synthetic");
            check(Reject(() => ApiProxySelection.From(fakeProxy), "api_proxy_auth_required"), "models-proxy-credential-provider-is-rejected");
            fakeProxy.Credentials = null; fakeProxy.Route = new Uri("http://[::1]:8123");
            check(IPAddress.Parse(ApiProxySelection.From(fakeProxy).Host).Equals(IPAddress.IPv6Loopback), "models-proxy-ipv6-roundtrip-without-uri-brackets");
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string directory = Path.GetFullPath(Path.Combine(parent, "provider-tests-" + Guid.NewGuid().ToString("N")));
            if (!directory.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
            Directory.CreateDirectory(directory);
            try
            {
                string files = Path.Combine(directory, "中文 模型路径"); Directory.CreateDirectory(files);
                foreach (string name in new[] { "python.exe", "worker.py", "llama-server.exe", "model.gguf", "kokoro-v1.0.onnx", "voices-v1.0.bin" }) File.WriteAllText(Path.Combine(files, name), "isolated fixture");
                string path = Path.Combine(directory, "config.json"), worker = Path.Combine(files, "worker.py");
                var credentials = new ApiCredentialStore(directory); var probe = new FakeProbe();
                var service = new ModelSettingsService(path, worker, credentials, probe);
                var initial = service.Read();
                check(initial.Draft != null && initial.ActiveProvider == "" && !initial.HasSavedApiKey, "models-missing-config-still-has-editable-draft");
                var local = new ModelSettingsDraft { TranslationProvider = "local", Python = Path.Combine(files, "python.exe"), LlamaServer = Path.Combine(files, "llama-server.exe"),
                    TranslationModel = Path.Combine(files, "model.gguf"), VoiceModelDir = files, ApiModel = "fake/test-model" };
                check(service.Save(local).Success && probe.Paths.Count == 0, "models-local-save-validates-unicode-files-without-network");
                var configuration = Configuration.Load(path);
                check(configuration.Device == "auto" && configuration.GpuLayers == 99 && configuration.Threads == 4, "models-new-configuration-has-portable-auto-gpu-default");
                check(service.Read().ActiveProvider == "" && service.Read().RestartRequired, "models-saved-configuration-is-not-reported-ready");
                configuration.Device = "none"; configuration.GpuLayers = 0; configuration.Threads = 2;
                File.WriteAllText(path, Json.Write(configuration.Serialize()), new UTF8Encoding(false));
                check(service.Save(local).Success && Configuration.Load(path).Threads == 2 && Configuration.Load(path).Device == "none", "models-valid-tuning-survives-ui-save");
                var missing = local.Copy(); missing.TranslationModel = Path.Combine(files, "absent.gguf");
                string original = File.ReadAllText(path);
                check(!service.Save(missing).Success && File.ReadAllText(path) == original, "models-invalid-path-preserves-active-configuration");
                var brokenTranslation = configuration.Serialize(); brokenTranslation["llama_server"] = ""; brokenTranslation["translation_model"] = "";
                File.WriteAllText(path, Json.Write(brokenTranslation));
                check(Configuration.Load(path).VoiceModelDir == files && Reject(() => Configuration.Load(path).Validate(true), "translation_runtime_missing"), "models-runtime-voice-only-load-survives-missing-translator-but-save-validation-remains-strict");
                File.WriteAllText(path, original);
                var api = local.Copy(); api.TranslationProvider = "openrouter"; api.LlamaServer = ""; api.TranslationModel = "";
                check(!service.Validate(api).Success && probe.Paths.Count == 0, "models-api-missing-key-is-explicit-and-offline");
                api.NewApiKey = TestKey;
                check(service.Save(api).Success && Configuration.Load(path).TranslationProvider == "openrouter", "models-api-needs-local-speech-but-not-local-translator");
                byte[] encrypted = credentials.Snapshot();
                check(credentials.Load() == TestKey && !Encoding.UTF8.GetString(encrypted).Contains(TestKey) && !File.ReadAllText(path).Contains(TestKey), "models-dpapi-roundtrip-without-plaintext-config-or-credential");
                var state = service.Read();
                check(state.HasSavedApiKey && state.Draft.NewApiKey == "" && !state.Draft.RemoveApiKey, "models-read-never-refills-secret");
                api.NewApiKey = "";
                var connection = service.CheckAsync(api, CancellationToken.None).GetAwaiter().GetResult();
                check(connection.Success && probe.Paths.SequenceEqual(new[] { "/api/v1/key", "/api/v1/models" }) && probe.CorrectKey && probe.PublicModels && probe.Limits,
                    "models-explicit-check-only-key-and-public-model-list");
                check(service.Read().ActiveProvider == "", "models-key-check-does-not-claim-worker-ready");
                probe.Model = "fake/different-model";
                check(!service.CheckAsync(api, CancellationToken.None).GetAwaiter().GetResult().Success, "models-check-rejects-unknown-exact-model-id");
                probe.Model = api.ApiModel; probe.Error = "api_redirect_rejected";
                check(service.CheckAsync(api, CancellationToken.None).GetAwaiter().GetResult().Message.Contains("重定向"), "models-check-fixed-redirect-failure");
                probe.Error = "api_unauthorized";
                check(service.CheckAsync(api, CancellationToken.None).GetAwaiter().GetResult().Message.Contains("未接受"), "models-check-fixed-authentication-failure");
                probe.Error = ""; probe.Block = true;
                using (var cancellation = new CancellationTokenSource(100))
                    check(service.CheckAsync(api, cancellation.Token).GetAwaiter().GetResult().Message.Contains("取消"), "models-connection-check-cancellation-is-explicit");
                probe.Block = false;
                api.NewApiKey = "another-synthetic-provider-test-key"; original = File.ReadAllText(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    check(!service.Save(api).Success && credentials.Load() == TestKey, "models-config-publish-failure-restores-encrypted-key");
                check(File.ReadAllText(path) == original, "models-config-publish-failure-preserves-original-bytes");
                api.NewApiKey = ""; api.RemoveApiKey = true;
                check(!service.Save(api).Success && credentials.Available, "models-active-api-key-cannot-be-deleted-and-saved-as-ready");
                local.RemoveApiKey = true;
                check(service.Save(local).Success && !credentials.Available, "models-switch-local-can-explicitly-remove-api-key");
                credentials.Save(TestKey); File.WriteAllBytes(Path.Combine(directory, ApiCredentialStore.FileName), new byte[] { 1, 2, 3 });
                check(!credentials.Available && Reject(() => credentials.Load(), "api_key_unreadable"), "models-corrupt-encrypted-key-has-fixed-error");
                File.WriteAllText(path, "{broken", new UTF8Encoding(false));
                check(service.Read().Draft != null && File.ReadAllText(path) == "{broken", "models-broken-json-is-editable-and-not-overwritten-on-read");
                local.RemoveApiKey = false;
                check(service.Save(local).Success && Directory.GetFiles(directory, "*.before-settings.*.json").Any(p => File.ReadAllText(p) == "{broken"), "models-successful-repair-retains-old-config-backup");
                var plain = Configuration.Load(path).Serialize(); plain["api_key"] = TestKey; File.WriteAllText(path, Json.Write(plain));
                check(Reject(() => Configuration.Load(path), "plaintext_key_not_supported"), "models-runtime-rejects-plaintext-key-configuration");
                configuration = new Configuration { TranslationProvider = "openrouter" }; service.NotifyStarting(configuration);
                check(service.Read().ActiveProvider == "", "models-starting-is-not-active");
                service.NotifyReady(); check(service.Read().ActiveProvider == "openrouter" && !service.Read().RestartRequired, "models-ready-event-marks-actual-active-provider");
                service.NotifyReady(false);
                check(service.Read().ActiveProvider == "" && !service.Read().RestartRequired && service.Read().StatusText.Contains("英文朗读已就绪"), "models-voice-ready-does-not-claim-active-translator");
                var launchConfig = new Configuration { Python = "python.exe", Worker = "worker.py", VoiceModelDir = files, TranslationProvider = "openrouter", ApiModel = "" };
                foreach (string failureCode in new[] { "api_proxy_auth_required", "api_proxy_unsupported", "api_proxy_resolution_failed" })
                {
                    var start = WorkerProcess.StartInfo(launchConfig, () => { throw new ModelConfigurationException(failureCode); });
                    check(start.Arguments.Contains("--translation-config-error " + WorkerProcess.Quote(failureCode)) && !start.Arguments.Contains("--api-proxy-host"), "models-voice-survives-" + failureCode + "-without-direct-fallback");
                }
                var apiIncomplete = Json.Parse(original); apiIncomplete["translation_provider"] = "openrouter"; apiIncomplete["openrouter_model"] = "";
                File.WriteAllText(path, Json.Write(apiIncomplete));
                check(Configuration.Load(path).TranslationProvider == "openrouter" && Reject(() => Configuration.Load(path).Validate(true), "api_model_missing"), "models-runtime-voice-only-load-survives-unconfigured-api-model");
                service.NotifyUnavailable(); check(service.Read().ActiveProvider == "", "models-failure-clears-active-provider");
                check(Json.Parse("{\"data\":\"" + new string('a', 100000) + "\"}", 4 * 1024 * 1024).ContainsKey("data"), "models-list-parser-honors-larger-explicit-bound");
                check(!Configuration.ValidApiModel("bad model") && !Configuration.ValidApiModel("missing-provider") && Configuration.ValidApiModel("provider/model:free"), "models-model-id-validation-is-bounded");

                int attempts = 0; WorkerFailureNotice failure = null; ReadyEndpoint endpoint = null;
                using (var supervisor = new WorkerSupervisor((events, failed) => {
                    if (Interlocked.Increment(ref attempts) == 1) throw new ModelConfigurationException("python_missing");
                    endpoint = new ReadyEndpoint(events); return endpoint;
                }, (epoch, e) => { }, notice => failure = notice, (id, code) => { }, (epoch, phase) => { }))
                {
                    supervisor.Start();
                    check(SpinWait.SpinUntil(() => failure != null, 2000) && !failure.Recovering && failure.Code == "configuration_unavailable" && attempts == 1,
                        "models-invalid-config-stops-without-retry-loop");
                    supervisor.Restart();
                    if (!SpinWait.SpinUntil(() => endpoint != null, 2000)) throw new InvalidOperationException("model test restart timeout");
                    endpoint.Emit(new Dictionary<string, object> { { "event", "ready" }, { "request_id", null } });
                    check(supervisor.Send(new { op = "cancel" }, null) == WorkerSendResult.Accepted && attempts == 2, "models-save-triggered-manual-restart-recovers-after-config-failure");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
