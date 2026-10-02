// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mansur.Next.Desktop
{
    internal sealed class ApiServiceProfile
    {
        internal string Id = "", Capability = "translation", Name = "", BaseUrl = "", Model = "", Voice = "", AudioFormat = "wav";
        internal int SampleRate = 24000;
        internal bool Stream = true;
        // Only editor drafts carry these fields. They are never serialized.
        internal string NewKey = "";
        internal bool RemoveKey;
        internal ApiServiceProfile Copy(bool transient = true)
        { return new ApiServiceProfile { Id = Id, Capability = Capability, Name = Name, BaseUrl = BaseUrl, Model = Model, Voice = Voice, AudioFormat = AudioFormat, SampleRate = SampleRate, Stream = Stream, NewKey = transient ? NewKey : "", RemoveKey = transient && RemoveKey }; }
        internal Dictionary<string, object> Serialize()
        { return new Dictionary<string, object> { { "id", Id }, { "capability", Capability }, { "name", Name }, { "base_url", BaseUrl }, { "model", Model }, { "voice", Voice }, { "audio_format", AudioFormat }, { "sample_rate", SampleRate }, { "stream", Stream } }; }
    }
    internal static class ApiServices
    {
        internal const string LegacyId = "translation-openrouter";
        internal const int MaximumProfiles = 64;
        internal static bool ValidId(string id) { return id != null && Regex.IsMatch(id, @"\A(?:translation|speech)-[a-z0-9-]{1,48}\z"); }
        internal static bool ValidModel(string value)
        { return value != null && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:/@-]{0,199}\z"); }
        internal static Uri Endpoint(string value)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port < 1 || uri.Port > 65535)
                throw new ModelConfigurationException("api_base_url_invalid");
            foreach (char ch in value) if (Char.IsControl(ch) || ch == '\\') throw new ModelConfigurationException("api_base_url_invalid");
            return uri;
        }
        internal static void Validate(ApiServiceProfile profile, bool active = false)
        {
            if (profile == null || !ValidId(profile.Id) || (profile.Capability != "translation" && profile.Capability != "speech") ||
                !profile.Id.StartsWith(profile.Capability + "-", StringComparison.Ordinal) || String.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 60 || profile.Name.Any(Char.IsControl) || profile.Model == null || profile.Voice == null)
                throw new ModelConfigurationException("api_service_invalid");
            Endpoint(profile.BaseUrl);
            if ((active || profile.Model.Length > 0) && !ValidModel(profile.Model)) throw new ModelConfigurationException("api_model_missing");
            if (profile.Capability == "speech")
            {
                if ((active && String.IsNullOrWhiteSpace(profile.Voice)) || profile.Voice.Length > 200 || profile.Voice.Any(Char.IsControl)) throw new ModelConfigurationException("api_voice_missing");
                if ((profile.AudioFormat != "wav" && profile.AudioFormat != "pcm") || profile.SampleRate < 8000 || profile.SampleRate > 96000) throw new ModelConfigurationException("api_audio_format_invalid");
                if (profile.Id == "speech-stepfun" && !new[] { 8000, 16000, 22050, 24000, 48000 }.Contains(profile.SampleRate)) throw new ModelConfigurationException("api_audio_format_invalid");
            }
        }
        internal static List<ApiServiceProfile> Defaults()
        {
            var values = new List<ApiServiceProfile>();
            foreach (string capability in new[] { "translation", "speech" })
            {
                values.Add(new ApiServiceProfile { Id = capability + "-openrouter", Capability = capability, Name = "OpenRouter", BaseUrl = "https://openrouter.ai/api/v1", AudioFormat = capability == "speech" ? "pcm" : "wav" });
                values.Add(new ApiServiceProfile { Id = capability + "-openai", Capability = capability, Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = capability == "speech" ? "gpt-4o-mini-tts" : "", Voice = capability == "speech" ? "coral" : "" });
                values.Add(new ApiServiceProfile { Id = capability + "-siliconflow", Capability = capability, Name = "硅基流动", BaseUrl = "https://api.siliconflow.cn/v1" });
                if (capability == "translation")
                { values.Add(new ApiServiceProfile { Id = "translation-deepseek", Capability = capability, Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1" }); ServicePresets.AddTranslation(values); }
                else values.Add(new ApiServiceProfile { Id = "speech-stepfun", Capability = capability, Name = "阶跃星辰", BaseUrl = "https://api.stepfun.com/v1", Model = "step-tts-mini", Voice = "cixingnansheng" });
            }
            return values;
        }
        internal static List<ApiServiceProfile> Read(Dictionary<string, object> json)
        {
            var values = Defaults(); object raw;
            if (!json.TryGetValue("api_services", out raw)) return values;
            var entries = raw as object[]; if (entries == null || entries.Length > MaximumProfiles) throw new ModelConfigurationException("api_service_invalid");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object entry in entries)
            {
                var fields = entry as Dictionary<string, object>; if (fields == null || fields.ContainsKey("api_key") || fields.ContainsKey("new_key")) throw new ModelConfigurationException("plaintext_key_not_supported");
                var value = new ApiServiceProfile { Id = Configuration.Optional(fields, "id", ""), Capability = Configuration.Optional(fields, "capability", ""), Name = Configuration.Optional(fields, "name", ""),
                    BaseUrl = Configuration.Optional(fields, "base_url", ""), Model = Configuration.Optional(fields, "model", ""), Voice = Configuration.Optional(fields, "voice", ""), AudioFormat = Configuration.Optional(fields, "audio_format", "wav") };
                if (fields.ContainsKey("sample_rate")) value.SampleRate = (int)Json.Integer(fields, "sample_rate", 8000, 96000);
                if (fields.TryGetValue("stream", out raw)) { if (!(raw is bool)) throw new ModelConfigurationException("api_service_invalid"); value.Stream = (bool)raw; }
                Validate(value); if (!seen.Add(value.Id)) throw new ModelConfigurationException("api_service_invalid");
                int index = values.FindIndex(p => p.Id == value.Id); if (index < 0) values.Add(value); else values[index] = value;
            }
            if (values.Count > MaximumProfiles) throw new ModelConfigurationException("api_service_invalid");
            return values;
        }
        internal static ApiServiceProfile Find(IEnumerable<ApiServiceProfile> values, string id, string capability)
        {
            var value = values.FirstOrDefault(p => p.Id == id && p.Capability == capability);
            if (value == null) throw new ModelConfigurationException("api_service_invalid"); return value;
        }
    }
}
