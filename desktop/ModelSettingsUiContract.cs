// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mansur.Next.Desktop
{
    internal sealed class ModelSettingsDraft
    {
        internal string TranslationProvider = "local";
        internal string SpeechProvider = "local", TranslationServiceId = ApiServices.LegacyId, SpeechServiceId = "speech-openai";
        internal System.Collections.Generic.List<ApiServiceProfile> Services = ApiServices.Defaults();
        internal string Python = "", LlamaServer = "", TranslationModel = "", VoiceModelDir = "", ApiModel = "";
        // Transient UI input only. Read() never returns a decrypted saved key.
        internal string NewApiKey = "";
        internal bool RemoveApiKey;
        internal ModelSettingsDraft Copy()
        {
            return new ModelSettingsDraft { TranslationProvider = TranslationProvider, Python = Python, LlamaServer = LlamaServer,
                TranslationModel = TranslationModel, VoiceModelDir = VoiceModelDir, ApiModel = ApiModel, NewApiKey = NewApiKey, RemoveApiKey = RemoveApiKey,
                SpeechProvider = SpeechProvider, TranslationServiceId = TranslationServiceId, SpeechServiceId = SpeechServiceId, Services = Services.ConvertAll(p => p.Copy()) };
        }
    }
    internal sealed class ModelSettingsState
    {
        internal ModelSettingsDraft Draft = new ModelSettingsDraft();
        internal bool HasSavedApiKey, RestartRequired;
        internal string ActiveProvider = "", StatusText = "";
        internal System.Collections.Generic.Dictionary<string, bool> SavedKeys = new System.Collections.Generic.Dictionary<string, bool>();
    }
    internal interface IApiServiceSettings
    {
        Task<string[]> FetchModelsAsync(ApiServiceProfile profile, CancellationToken cancellationToken);
    }
    internal sealed class ModelSettingsResult
    {
        internal bool Success, RestartRequired;
        internal string Message = "";
    }
    internal interface IModelSettingsService
    {
        ModelSettingsState Read();
        ModelSettingsResult Validate(ModelSettingsDraft draft);
        ModelSettingsResult Save(ModelSettingsDraft draft);
        Task<ModelSettingsResult> CheckAsync(ModelSettingsDraft draft, CancellationToken cancellationToken);
    }
}
