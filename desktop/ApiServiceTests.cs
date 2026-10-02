// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mansur.Next.Desktop
{
    internal static class ApiServiceTests
    {
        private sealed class Catalog : IApiModelCatalog
        {
            internal readonly List<string> Calls = new List<string>();
            public Task<string[]> FetchAsync(ApiServiceProfile profile, string key, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Calls.Add(profile.Id + ":" + (key == "fixed-translation-key" ? "translation" : key == "fixed-speech-key" ? "speech" : "unexpected")); return Task.FromResult(new[] { "fixed/model" }); }
        }
        private static bool Reject(Action action, string code)
        { try { action(); return false; } catch (ModelConfigurationException e) { return e.Code == code; } }
        internal static void Run(Action<bool, string> check)
        {
            foreach (string url in new[] { "http://example.org/v1", "https://key@example.org/v1", "https://example.org/v1?key=bad", "https://example.org/#key", "https://example.org/\r\nbad", "file:///C:/models", "https://example.org\\evil" })
                check(Reject(() => ApiServices.Endpoint(url), "api_base_url_invalid"), "api-service-rejects-url-" + Array.IndexOf(new[] { "http://example.org/v1", "https://key@example.org/v1", "https://example.org/v1?key=bad", "https://example.org/#key", "https://example.org/\r\nbad", "file:///C:/models", "https://example.org\\evil" }, url));
            check(ApiServices.Endpoint("https://example.org/v1").Scheme == "https" && ApiServices.Endpoint("http://127.0.0.1:8765/v1").IsLoopback, "api-service-https-and-local-http-supported");
            var defaults = ApiServices.Defaults();
            check(defaults.Count == 18 && defaults.Select(p => p.Id).Distinct().Count() == 18 && defaults.All(p => p.NewKey.Length == 0 && !p.RemoveKey), "api-domestic-presets-unique-and-have-no-secret-or-activation-side-effects");
            var legacyRows = defaults.Where(p => ServicePresets.Description(p.Id).Length == 0).Select(p => (object)p.Serialize()).ToList();
            var edited = ApiServices.Find(defaults, "translation-deepseek", "translation").Copy(); edited.BaseUrl = "https://example.org/owned/v1"; edited.Model = "owned/model";
            legacyRows[legacyRows.FindIndex(r => (string)((Dictionary<string, object>)r)["id"] == edited.Id)] = edited.Serialize();
            for (int i = legacyRows.Count; i < 32; i++) legacyRows.Add(new ApiServiceProfile { Id = "translation-custom-" + i, Name = "自定义 " + i, BaseUrl = "https://example.org/v1", Model = "owned/model" }.Serialize());
            var migrated = ApiServices.Read(new Dictionary<string, object> { { "api_services", legacyRows.ToArray() } });
            check(migrated.Count == 43 && migrated.Exists(p => p.Id == edited.Id && p.Model == edited.Model && p.BaseUrl == edited.BaseUrl) && migrated.Count(p => p.Id.Contains("-custom-")) == 25, "api-domestic-migration-preserves-full-old-32-service-config-and-custom-endpoints");
            foreach (var preset in defaults.Where(p => !ServicePresets.HasCatalog(p.Id)))
            {
                var candidate = preset.Copy(); candidate.Model = "fixed/model";
                check(new ApiModelCatalog().FetchAsync(candidate, "fixed-fixture-key", CancellationToken.None).GetAwaiter().GetResult().Length == 0, "api-manual-preset-check-makes-no-inference-or-invented-directory-request-" + preset.Id);
            }
            check(Reject(() => new ApiModelCatalog().FetchAsync(ApiServices.Find(defaults, "translation-qwen", "translation"), "fixed-fixture-key", CancellationToken.None).GetAwaiter().GetResult(), "api_model_missing"), "api-manual-preset-check-requires-model-id");
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MansurApiServiceTests-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string name in new[] { "python.exe", "worker.py" }) File.WriteAllText(Path.Combine(root, name), "fixed fixture");
                string config = Path.Combine(root, "models.json");
                var catalog = new Catalog();
                var service = new ModelSettingsService(config, Path.Combine(root, "worker.py"), new ApiCredentialStore(root), null, catalog);
                var draft = new ModelSettingsDraft { TranslationProvider = "compatible", SpeechProvider = "compatible", TranslationServiceId = "translation-openai", SpeechServiceId = "speech-openai",
                    Python = Path.Combine(root, "python.exe"), VoiceModelDir = "", LlamaServer = "", TranslationModel = "" };
                var text = ApiServices.Find(draft.Services, draft.TranslationServiceId, "translation"); text.Model = "fixed-translation-model"; text.NewKey = "fixed-translation-key";
                var voice = ApiServices.Find(draft.Services, draft.SpeechServiceId, "speech"); voice.NewKey = "fixed-speech-key";
                check(service.Save(draft).Success && catalog.Calls.Count == 0, "api-service-cloud-only-save-needs-no-local-models-and-makes-no-request");
                var saved = Configuration.Load(config); saved.Validate(true);
                check(saved.TranslationServiceId == text.Id && saved.SpeechServiceId == voice.Id && saved.SpeechProvider == "compatible", "api-service-independent-config-roundtrip");
                string json = File.ReadAllText(config);
                check(!json.Contains("fixed-translation-key") && !json.Contains("fixed-speech-key") && !json.Contains("new_key") && !json.Contains("api_key"), "api-service-json-excludes-all-secrets");
                var textStore = new ApiCredentialStore(root, text.Id); var voiceStore = new ApiCredentialStore(root, voice.Id);
                check(textStore.Load() == text.NewKey && voiceStore.Load() == voice.NewKey && !textStore.Snapshot().SequenceEqual(voiceStore.Snapshot()), "api-service-dpapi-keys-separated-by-capability");
                byte[] voiceSnapshot = voiceStore.Snapshot(); voiceStore.Restore(textStore.Snapshot());
                check(Reject(() => voiceStore.Load(), "api_key_unreadable"), "api-service-copied-key-cannot-decrypt-as-other-service"); voiceStore.Restore(voiceSnapshot);
                var state = service.Read();
                check(state.Draft.Services.All(p => p.NewKey.Length == 0) && state.SavedKeys[text.Id] && state.SavedKeys[voice.Id], "api-service-read-only-key-presence-never-plaintext");
                service.FetchModelsAsync(text.Copy(false), CancellationToken.None).GetAwaiter().GetResult();
                service.FetchModelsAsync(voice.Copy(false), CancellationToken.None).GetAwaiter().GetResult();
                check(catalog.Calls.SequenceEqual(new[] { text.Id + ":translation", voice.Id + ":speech" }), "api-service-model-fetch-uses-own-key-only");
                text.NewKey = "fixed-translation-replacement"; voice.NewKey = "fixed-speech-replacement";
                using (var locked = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read))
                    check(!service.Save(draft).Success && textStore.Load() == "fixed-translation-key" && voiceStore.Load() == "fixed-speech-key", "api-service-publish-failure-rolls-back-all-encrypted-keys");
                check(File.ReadAllText(config) == json, "api-service-publish-failure-preserves-original-config");
                text.NewKey = voice.NewKey = "";
                var launch = WorkerProcess.StartInfo(Configuration.Load(config), () => ApiProxySelection.From(null));
                var streamConfig = Configuration.Load(config); ApiServices.Find(streamConfig.Services, text.Id, "translation").Stream = false;
                var roundtrip = ApiServices.Read(streamConfig.Serialize());
                check(!ApiServices.Find(roundtrip, text.Id, "translation").Stream && ApiServices.Find(roundtrip, voice.Id, "speech").Stream, "api-service-streaming-choice-roundtrips-independently");
                check(WorkerProcess.StartInfo(streamConfig, () => ApiProxySelection.From(null)).Arguments.Contains("--translation-api-no-stream"), "api-service-nonstream-choice-reaches-worker-without-retry");
                check(launch.Arguments.Contains("--translation-api-base-url") && launch.Arguments.Contains("--speech-api-base-url") && launch.Arguments.Contains("--speech-api-key-file") &&
                    !launch.Arguments.Contains("fixed-translation-key") && !launch.Arguments.Contains("fixed-speech-key"), "api-service-worker-cli-carries-key-paths-only");
                saved.Services.Find(p => p.Id == text.Id).Model = "";
                launch = WorkerProcess.StartInfo(saved, () => ApiProxySelection.From(null));
                check(launch.Arguments.Contains("--translation-config-error") && launch.Arguments.Contains("--speech-api-model"), "api-service-missing-translation-model-keeps-configured-speech-usable");
                var bad = draft.Copy(); ApiServices.Find(bad.Services, voice.Id, "speech").Voice = "";
                check(!service.Validate(bad).Success && catalog.Calls.Count == 2, "api-service-active-speech-requires-voice-without-network");
                var legacy = new ApiCredentialStore(root); legacy.Save("fixed-legacy-key");
                check(new ApiCredentialStore(root, ApiServices.LegacyId).Load() == "fixed-legacy-key", "api-service-legacy-openrouter-key-stays-compatible");
                var fields = saved.Serialize(); var row = text.Copy(false).Serialize(); row["api_key"] = "fixed-invalid";
                fields["api_services"] = new object[] { row };
                check(Reject(() => ApiServices.Read(fields), "plaintext_key_not_supported"), "api-service-profile-plaintext-key-rejected");
                check(Reject(() => ApiServices.Find(draft.Services, voice.Id, "translation"), "api_service_invalid"), "api-service-cross-capability-selection-rejected");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
