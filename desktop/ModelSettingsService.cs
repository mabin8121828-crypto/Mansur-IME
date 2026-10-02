// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mansur.Next.Desktop
{
    internal sealed class ModelSettingsService : IModelSettingsService, IApiServiceSettings
    {
        private readonly object gate = new object();
        private readonly string configurationPath, packageWorker;
        private readonly ApiCredentialStore credentials;
        private readonly IOpenRouterProbe probe;
        private readonly IApiModelCatalog catalog;
        private string activeProvider = "", pendingProvider = "", pendingSpeech = "local", runtimeStatus = "保存并应用后准备学习后台。";
        private bool restartRequired;
        internal ModelSettingsService(string configPath)
            : this(configPath, Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "learning", "worker.py"))) { }
        internal ModelSettingsService(string configPath, string workerPath)
            : this(configPath, workerPath, new ApiCredentialStore(UserStateDirectory.Root), new OpenRouterConnectionCheck()) { }
        internal ModelSettingsService(string configPath, string workerPath, ApiCredentialStore keyStore, IOpenRouterProbe connectionProbe, IApiModelCatalog modelCatalog = null)
        {
            configurationPath = Path.GetFullPath(configPath); packageWorker = Path.GetFullPath(workerPath);
            credentials = keyStore; probe = connectionProbe; catalog = modelCatalog ?? new ApiModelCatalog();
        }
        internal void NotifyStarting(Configuration config)
        { lock (gate) { pendingProvider = config.TranslationProvider; pendingSpeech = config.SpeechProvider; activeProvider = ""; runtimeStatus = "正在准备学习后台…"; } }
        internal void NotifyReady(bool translationReady = true, bool voiceReady = true)
        { lock (gate) { activeProvider = translationReady ? pendingProvider : ""; restartRequired = false;
            runtimeStatus = !voiceReady ? "朗读暂不可用；英文仍可生成、查看和复制。请检查朗读配置。" : !translationReady ? "英文朗读已就绪；中文翻译将在首次确认时准备。" :
                (pendingSpeech == "local" ? "本地朗读已就绪；" : "API 朗读配置已准备；") + (activeProvider == "local" ? "中文使用本地翻译。" : activeProvider == "openrouter" ? "中文使用 OpenRouter 翻译。" : "中文使用已选 API 服务翻译。"); } }
        internal void NotifyUnavailable()
        { lock (gate) { activeProvider = ""; runtimeStatus = "学习后台暂不可用，请检查配置后保存并应用。"; } }
        public ModelSettingsState Read()
        {
            lock (gate)
            {
                var state = new ModelSettingsState { HasSavedApiKey = credentials.Available, ActiveProvider = activeProvider, RestartRequired = restartRequired, StatusText = runtimeStatus };
                try
                {
                    if (!File.Exists(configurationPath)) { state.StatusText = "尚未配置模型，请填写路径后保存并应用。"; return state; }
                    var json = ReadDictionary();
                    state.Draft = new ModelSettingsDraft {
                        TranslationProvider = Display(json, "translation_provider", "local"),
                        Python = Display(json, "python", ""), LlamaServer = Display(json, "llama_server", ""),
                        TranslationModel = Display(json, "translation_model", ""), VoiceModelDir = Display(json, "voice_model_dir", ""),
                        ApiModel = Display(json, "openrouter_model", ""), SpeechProvider = Display(json, "speech_provider", "local"),
                        TranslationServiceId = Display(json, "translation_service", ApiServices.LegacyId), SpeechServiceId = Display(json, "speech_service", "speech-openai"), Services = ApiServices.Read(json)
                    };
                    if (!json.ContainsKey("api_services")) ApiServices.Find(state.Draft.Services, ApiServices.LegacyId, "translation").Model = state.Draft.ApiModel;
                    foreach (var profile in state.Draft.Services) state.SavedKeys[profile.Id] = KeyStore(profile.Id).Available;
                }
                catch (Exception error) when (Expected(error)) { state.StatusText = "现有配置无法读取；原文件已保留，可重新填写后保存修复。"; }
                return state;
            }
        }
        private Dictionary<string, object> ReadDictionary()
        { return Json.Parse(new UTF8Encoding(false, true).GetString(SharedSettingsValues.ReadBounded(configurationPath, 65536)).TrimStart('\ufeff'), 65536); }
        private static string Display(Dictionary<string, object> source, string key, string fallback)
        { object raw; string value = source.TryGetValue(key, out raw) ? raw as string : null; return value != null && value.Length <= 32760 ? value : fallback; }
        private Configuration Build(ModelSettingsDraft draft)
        {
            if (draft == null) throw new ModelConfigurationException("configuration_unavailable");
            Configuration result;
            try { result = Configuration.Load(configurationPath); } catch (Exception error) when (Expected(error)) { result = new Configuration { Device = "auto", GpuLayers = 99 }; }
            result.TranslationProvider = Clean(draft.TranslationProvider); result.Python = Clean(draft.Python);
            result.LlamaServer = Clean(draft.LlamaServer); result.TranslationModel = Clean(draft.TranslationModel);
            result.VoiceModelDir = Clean(draft.VoiceModelDir); result.ApiModel = Clean(draft.ApiModel); result.Worker = packageWorker;
            result.SpeechProvider = Clean(draft.SpeechProvider); result.TranslationServiceId = draft.TranslationServiceId; result.SpeechServiceId = draft.SpeechServiceId;
            result.Services = draft.Services.ConvertAll(p => p.Copy(false));
            if (result.TranslationProvider == "openrouter") ApiServices.Find(result.Services, ApiServices.LegacyId, "translation").Model = result.ApiModel;
            if (result.Services.Count > ApiServices.MaximumProfiles) throw new ModelConfigurationException("api_service_invalid");
            var ids = new HashSet<string>(); foreach (var profile in result.Services) { ApiServices.Validate(profile); if (!ids.Add(profile.Id)) throw new ModelConfigurationException("api_service_invalid"); }
            result.Validate(true);
            return result;
        }
        private static string Clean(string value) { return (value ?? "").Trim(); }
        private ApiCredentialStore KeyStore(string id) { return id == ApiServices.LegacyId ? credentials : new ApiCredentialStore(credentials.DirectoryPath, id); }
        private string ProfileKey(ApiServiceProfile profile)
        {
            if (profile.RemoveKey) throw new ModelConfigurationException("api_key_missing");
            if (String.IsNullOrWhiteSpace(profile.NewKey)) return KeyStore(profile.Id).Load();
            if (!ApiCredentialStore.Valid(profile.NewKey.Trim())) throw new ModelConfigurationException("api_key_invalid"); return profile.NewKey.Trim();
        }
        private List<ApiServiceProfile> KeyEdits(ModelSettingsDraft draft)
        {
            var profiles = draft.Services.ConvertAll(p => p.Copy());
            var legacy = ApiServices.Find(profiles, ApiServices.LegacyId, "translation");
            if (draft.RemoveApiKey || !String.IsNullOrWhiteSpace(draft.NewApiKey)) { legacy.NewKey = Clean(draft.NewApiKey); legacy.RemoveKey = draft.RemoveApiKey; }
            return profiles;
        }
        private string ResolveKey(ModelSettingsDraft draft)
        {
            if (draft.RemoveApiKey) throw new ModelConfigurationException("api_key_missing");
            string fresh = Clean(draft.NewApiKey);
            if (fresh.Length == 0) return credentials.Load();
            if (!ApiCredentialStore.Valid(fresh)) throw new ModelConfigurationException("api_key_invalid");
            return fresh;
        }
        public ModelSettingsResult Validate(ModelSettingsDraft draft)
        {
            lock (gate)
            {
                try
                {
                    Configuration config = Build(draft);
                    if (draft.RemoveApiKey && !String.IsNullOrWhiteSpace(draft.NewApiKey)) throw new ModelConfigurationException("api_key_action_conflict");
                    if (!String.IsNullOrWhiteSpace(draft.NewApiKey) && !ApiCredentialStore.Valid(Clean(draft.NewApiKey))) throw new ModelConfigurationException("api_key_invalid");
                    if (config.TranslationProvider == "openrouter") ResolveKey(draft);
                    var profiles = KeyEdits(draft);
                    foreach (var profile in profiles) { if (profile.RemoveKey && !String.IsNullOrWhiteSpace(profile.NewKey)) throw new ModelConfigurationException("api_key_action_conflict"); if (!String.IsNullOrWhiteSpace(profile.NewKey) && !ApiCredentialStore.Valid(profile.NewKey.Trim())) throw new ModelConfigurationException("api_key_invalid"); }
                    if (config.TranslationProvider == "compatible") ProfileKey(ApiServices.Find(profiles, config.TranslationServiceId, "translation"));
                    if (config.SpeechProvider == "compatible") ProfileKey(ApiServices.Find(profiles, config.SpeechServiceId, "speech"));
                    return Result(true, "配置格式和所需文件检查通过；API 尚未发送请求，保存并应用后准备学习后台。");
                }
                catch (Exception error) when (Expected(error)) { return Failure(error); }
            }
        }
        public ModelSettingsResult Save(ModelSettingsDraft draft)
        {
            lock (gate)
            {
                ModelSettingsResult validation = Validate(draft); if (!validation.Success) return validation;
                var oldKeys = new Dictionary<string, byte[]>();
                string temporary = configurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Configuration config = Build(draft); byte[] bytes = new UTF8Encoding(false, true).GetBytes(Json.Write(config.Serialize()));
                    if (bytes.Length > 65536) throw new ModelConfigurationException("configuration_unavailable");
                    Directory.CreateDirectory(Path.GetDirectoryName(configurationPath));
                    SharedSettingsValues.WriteNew(temporary, bytes);
                    foreach (var profile in KeyEdits(draft))
                    {
                        if (!profile.RemoveKey && String.IsNullOrWhiteSpace(profile.NewKey)) continue;
                        var keyStore = KeyStore(profile.Id); oldKeys[profile.Id] = keyStore.Snapshot();
                        if (profile.RemoveKey) keyStore.Restore(null); else keyStore.Save(Clean(profile.NewKey));
                    }
                    if (File.Exists(configurationPath))
                    {
                        string backup = configurationPath + ".before-settings." + Guid.NewGuid().ToString("N") + ".json";
                        File.Replace(temporary, configurationPath, backup);
                    }
                    else File.Move(temporary, configurationPath);
                    restartRequired = true;
                    return new ModelSettingsResult { Success = true, RestartRequired = true, Message = "设置已保存，正在请求重新准备学习后台；中文输入不受影响。" };
                }
                catch (Exception error) when (Expected(error))
                {
                    bool restored = true;
                    foreach (var entry in oldKeys)
                        try { KeyStore(entry.Key).Restore(entry.Value); }
                        catch (Exception restore) when (Expected(restore)) { restored = false; }
                    if (!restored) return Result(false, "设置未保存，密钥恢复未能确认。请重新填写密钥后再试。");
                    return Failure(error);
                }
                finally { SharedSettingsValues.TryDelete(temporary); }
            }
        }
        public async Task<ModelSettingsResult> CheckAsync(ModelSettingsDraft draft, CancellationToken cancellationToken)
        {
            ModelSettingsDraft copy = draft == null ? null : draft.Copy();
            ModelSettingsResult validation;
            try { validation = await Task.Run(() => Validate(copy), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Result(false, "检查已取消。"); }
            if (!validation.Success) return validation;
            if (copy.TranslationProvider == "compatible" || copy.SpeechProvider == "compatible")
            {
                try { foreach (var profile in copy.Services) if ((copy.TranslationProvider == "compatible" && profile.Id == copy.TranslationServiceId) || (copy.SpeechProvider == "compatible" && profile.Id == copy.SpeechServiceId)) await FetchModelsAsync(profile, cancellationToken).ConfigureAwait(false);
                    bool directoryChecked = copy.Services.Exists(p => ServicePresets.HasCatalog(p.Id) && ((copy.TranslationProvider == "compatible" && p.Id == copy.TranslationServiceId) || (copy.SpeechProvider == "compatible" && p.Id == copy.SpeechServiceId)));
                    return Result(true, directoryChecked ? "已读取支持目录的服务；其他服务仅检查配置。实际翻译或朗读以确认输入后的结果为准。" : "配置检查通过，未验证网络、密钥有效性或模型权限。实际结果以确认输入后的调用为准。"); }
                catch (OperationCanceledException) { return Result(false, "连接检查已取消或超时。"); }
                catch (Exception error) when (Expected(error)) { return Failure(error); }
            }
            if (copy.TranslationProvider != "openrouter") return validation;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(15000);
                try
                {
                    string key; lock (gate) key = ResolveKey(copy);
                    IOpenRouterProbe selected = probe;
                    if (probe is OpenRouterConnectionCheck)
                        selected = new OpenRouterConnectionCheck(await Task.Run(() => ApiProxySelection.Resolve(timeout.Token), timeout.Token).ConfigureAwait(false));
                    Dictionary<string, object> authentication = await selected.GetAsync("/api/v1/key", key, 65536, timeout.Token).ConfigureAwait(false);
                    object raw;
                    if (!authentication.TryGetValue("data", out raw) || !(raw is Dictionary<string, object>)) throw new ModelConfigurationException("api_response_invalid");
                    var models = await selected.GetAsync("/api/v1/models", null, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
                    object[] entries = models.TryGetValue("data", out raw) ? raw as object[] : null;
                    if (entries == null) throw new ModelConfigurationException("api_response_invalid");
                    bool found = false;
                    foreach (object entry in entries)
                    {
                        var value = entry as Dictionary<string, object>; object id;
                        if (value != null && value.TryGetValue("id", out id) && Object.Equals(id, Clean(copy.ApiModel))) { found = true; break; }
                    }
                    if (!found) throw new ModelConfigurationException("api_model_not_found");
                    return Result(true, "密钥验证通过，已找到该模型。未提交翻译或生成计费内容；实际翻译仍以三空格确认时的结果为准。");
                }
                catch (OperationCanceledException) { return Result(false, cancellationToken.IsCancellationRequested ? "检查已取消。" : "连接检查超时，请稍后重试。"); }
                catch (Exception error) when (Expected(error)) { return Failure(error); }
                finally { if (copy != null) copy.NewApiKey = ""; }
            }
        }
        public async Task<string[]> FetchModelsAsync(ApiServiceProfile profile, CancellationToken cancellationToken)
        { string key; var copy = profile.Copy(); lock (gate) { ApiServices.Validate(copy); key = ProfileKey(copy); } return await catalog.FetchAsync(copy, key, cancellationToken).ConfigureAwait(false); }
        private static bool Expected(Exception error)
        { return SharedSettingsValues.Recoverable(error) || error is CryptographicException || error is System.Net.WebException; }
        private static ModelSettingsResult Result(bool success, string message)
        { return new ModelSettingsResult { Success = success, Message = message }; }
        private static ModelSettingsResult Failure(Exception error)
        {
            var configuration = error as ModelConfigurationException;
            return Result(false, Message(configuration == null ? "configuration_unavailable" : configuration.Code));
        }
        internal static string Message(string code)
        {
            switch (code)
            {
                case "python_missing": return "请选择已包含 Kokoro 依赖的 Python 可执行文件。";
                case "worker_missing": return "当前安装包缺少学习组件，请修复安装包。";
                case "translation_runtime_missing": return "请选择存在的 llama-server 程序。";
                case "translation_model_missing": return "请选择存在的本地翻译模型文件。";
                case "voice_model_missing": return "朗读目录需要同时包含 kokoro-v1.0.onnx 和 voices-v1.0.bin。";
                case "invalid_translation_provider": case "invalid_speech_provider": return "请选择本地模型或兼容的 API 服务。";
                case "api_model_missing": return "请填写服务提供的模型 ID；OpenRouter 通常使用供应商/模型名。";
                case "api_key_missing": return "请填写此服务的 API 密钥；密钥仅加密保存在当前 Windows 用户下。";
                case "api_key_unreadable": return "已存密钥在当前用户下无法解密，请重新填写。";
                case "api_key_invalid": return "API 密钥格式不正确，请检查是否误粘贴了其他内容。";
                case "api_key_action_conflict": return "请只选择更换密钥或删除密钥中的一项。";
                case "api_unauthorized": return "服务未接受此密钥，请检查密钥及权限。";
                case "api_forbidden": return "服务拒绝此请求，请检查服务权限、地区限制和系统代理。";
                case "api_credit_required": return "服务返回余额或额度不足，请检查账户。";
                case "api_rate_limited": return "服务请求过于频繁，请稍后重试。";
                case "api_model_not_found": return "公开模型列表中未找到此模型 ID，请核对拼写。";
                case "api_timeout": return "服务连接超时，请稍后重试。";
                case "api_redirect_rejected": return "服务返回了重定向，本次连接已停止。";
                case "api_response_invalid": case "api_response_too_large": return "服务返回的数据不符合预期，请稍后重试。";
                case "api_connection_failed": return "暂时无法安全连接服务，请检查网络。";
                case "api_base_url_invalid": return "请填写 HTTPS 接口地址；本机接口可使用 localhost 的 HTTP 地址。不要在地址中填写密钥。";
                case "api_service_invalid": return "服务配置不完整，请检查服务名称和接口地址。";
                case "api_voice_missing": return "请填写所选朗读模型支持的音色 ID。";
                case "api_audio_format_invalid": return "请选择 WAV 或 PCM 音频格式，并检查采样率。";
                case "api_proxy_auth_required": return "当前网络代理需要身份验证；此版本不支持代理认证，请调整代理后重试。";
                case "api_proxy_unsupported": return "当前代理类型不受支持；请使用无需身份验证的 HTTP 代理或直连。";
                case "api_proxy_resolution_failed": return "暂时无法确定 Windows 代理路径，请检查系统代理后重试。";
                case "api_proxy_connect_failed": return "网络代理未建立安全连接，请检查代理后重试。";
                case "plaintext_key_not_supported": return "配置文件不能保存明文密钥，请在 API 密钥框重新填写并保存。";
                default: return "配置检查或保存失败；请检查路径、文件权限及配置格式，原文件已保留。";
            }
        }
    }
}
