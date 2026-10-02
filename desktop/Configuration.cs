// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Mansur.Next.Desktop
{
    internal sealed class ModelConfigurationException : FormatException
    {
        internal readonly string Code;
        internal ModelConfigurationException(string code) : base(code) { Code = code; }
    }
    internal sealed class Configuration
    {
        internal string Python, Worker, LlamaServer = "", TranslationModel = "", VoiceModelDir;
        internal string Device = "none", TranslationProvider = "local", ApiModel = "";
        internal string SpeechProvider = "local", TranslationServiceId = ApiServices.LegacyId, SpeechServiceId = "speech-openai";
        internal List<ApiServiceProfile> Services = ApiServices.Defaults();
        internal int GpuLayers = 0, Threads = 4, TranslationTimeoutSeconds = 30;
        internal static Configuration Load(string filename)
        {
            if (!SharedSettingsValues.AbsolutePath(filename)) throw new ModelConfigurationException("configuration_unavailable");
            var json = Json.Parse(new UTF8Encoding(false, true).GetString(SharedSettingsValues.ReadBounded(filename, 65536)).TrimStart('\ufeff'), 65536);
            if (json.ContainsKey("api_key") || json.ContainsKey("openrouter_api_key")) throw new ModelConfigurationException("plaintext_key_not_supported");
            var result = new Configuration {
                TranslationProvider = Optional(json, "translation_provider", "local"), ApiModel = Optional(json, "openrouter_model", ""),
                Python = Optional(json, "python", ""), Worker = Optional(json, "worker", ""),
                LlamaServer = Optional(json, "llama_server", ""), TranslationModel = Optional(json, "translation_model", ""),
                VoiceModelDir = Optional(json, "voice_model_dir", ""), Device = Optional(json, "device", "none"),
                SpeechProvider = Optional(json, "speech_provider", "local"), TranslationServiceId = Optional(json, "translation_service", ApiServices.LegacyId),
                SpeechServiceId = Optional(json, "speech_service", "speech-openai"), Services = ApiServices.Read(json)
            };
            if (json.ContainsKey("gpu_layers")) result.GpuLayers = (int)Json.Integer(json, "gpu_layers", 0, 999);
            if (json.ContainsKey("threads")) result.Threads = (int)Json.Integer(json, "threads", 1, 32);
            if (json.ContainsKey("translation_timeout_seconds")) result.TranslationTimeoutSeconds = (int)Json.Integer(json, "translation_timeout_seconds", 5, 90);
            result.Validate(false, false); return result;
        }
        internal static string Optional(Dictionary<string, object> values, string key, string fallback)
        { string value = Json.String(values, key, 32760, false); return value ?? fallback; }
        internal static bool ValidApiModel(string value)
        { return value != null && value.Length <= 160 && value.Contains("/") && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]*\z"); }
        internal void Validate(bool inspectVoiceFiles, bool requireTranslation = true)
        {
            if (TranslationProvider != "local" && TranslationProvider != "openrouter" && TranslationProvider != "compatible") throw new ModelConfigurationException("invalid_translation_provider");
            if (SpeechProvider != "local" && SpeechProvider != "compatible") throw new ModelConfigurationException("invalid_speech_provider");
            RequireFile(Python, "python_missing"); RequireFile(Worker, "worker_missing");
            if (inspectVoiceFiles && SpeechProvider == "local" && (!SharedSettingsValues.AbsolutePath(VoiceModelDir) || !Directory.Exists(VoiceModelDir))) throw new ModelConfigurationException("voice_model_missing");
            if (inspectVoiceFiles && SpeechProvider == "local")
                foreach (string name in new[] { "kokoro-v1.0.onnx", "voices-v1.0.bin" }) RequireFile(Path.Combine(VoiceModelDir, name), "voice_model_missing");
            // Voice-only requests remain usable when translation files or credentials need repair.
            // Saving/checking the full model settings still requires a valid translation configuration.
            if (requireTranslation)
            {
                if (TranslationProvider == "local") { RequireFile(LlamaServer, "translation_runtime_missing"); RequireFile(TranslationModel, "translation_model_missing"); }
                else if (TranslationProvider == "openrouter" && !ValidApiModel(ApiModel)) throw new ModelConfigurationException("api_model_missing");
                else if (TranslationProvider == "compatible") ApiServices.Validate(ApiServices.Find(Services, TranslationServiceId, "translation"), true);
            }
            if (requireTranslation && SpeechProvider == "compatible") ApiServices.Validate(ApiServices.Find(Services, SpeechServiceId, "speech"), true);
            if (Device == null || !Regex.IsMatch(Device, @"\A(?:none|auto|CPU|(?:Vulkan|CUDA|SYCL)[0-9]{1,3}|Metal)\z")) throw new ModelConfigurationException("invalid_device");
            if (Threads < 1 || Threads > 32 || GpuLayers < 0 || GpuLayers > 999 || TranslationTimeoutSeconds < 5 || TranslationTimeoutSeconds > 90)
                throw new ModelConfigurationException("invalid_numeric_configuration");
        }
        private static void RequireFile(string path, string code)
        { if (!SharedSettingsValues.AbsolutePath(path) || !File.Exists(path)) throw new ModelConfigurationException(code); }
        internal Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object> { { "translation_provider", TranslationProvider }, { "python", Python }, { "worker", Worker },
                { "llama_server", LlamaServer }, { "translation_model", TranslationModel }, { "voice_model_dir", VoiceModelDir },
                { "openrouter_model", ApiModel }, { "device", Device }, { "gpu_layers", GpuLayers }, { "threads", Threads }, { "translation_timeout_seconds", TranslationTimeoutSeconds },
                { "speech_provider", SpeechProvider }, { "translation_service", TranslationServiceId }, { "speech_service", SpeechServiceId },
                { "api_services", Services.ConvertAll(p => p.Serialize()).ToArray() } };
        }
    }
}
