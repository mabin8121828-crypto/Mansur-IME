// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Mansur.Next.Desktop
{
    // Endpoints checked against official documentation on 2026-10-02.
    // A preset is available for configuration, never automatically enabled.
    internal static class ServicePresets
    {
        internal static void AddTranslation(List<ApiServiceProfile> values)
        {
            Add(values, "qwen", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1");
            Add(values, "zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4");
            Add(values, "kimi", "Kimi", "https://api.moonshot.cn/v1");
            Add(values, "doubao", "豆包", "https://ark.cn-beijing.volces.com/api/v3");
            Add(values, "minimax", "MiniMax", "https://api.minimax.cn/v1");
            Add(values, "tokenhub", "腾讯 TokenHub", "https://tokenhub.tencentmaas.com/v1");
            Add(values, "hunyuan", "腾讯混元", "https://api.hunyuan.cloud.tencent.com/v1");
            Add(values, "baidu", "百度千帆", "https://qianfan.baidubce.com/v2");
            Add(values, "stepfun", "阶跃星辰", "https://api.stepfun.com/v1");
            Add(values, "spark", "讯飞星火", "https://spark-api-open.xf-yun.com/v1");
        }
        private static void Add(List<ApiServiceProfile> values, string brand, string name, string url)
        { values.Add(new ApiServiceProfile { Id = "translation-" + brand, Name = name, BaseUrl = url }); }
        internal static bool HasCatalog(string id)
        {
            // Only expose directory requests confirmed in vendor documentation.
            // Other presets use console model IDs instead of probing an invented route.
            switch (id)
            {
                case "translation-qwen": case "translation-zhipu": case "translation-doubao":
                case "translation-minimax": case "translation-tokenhub": case "translation-hunyuan":
                case "translation-spark": case "translation-stepfun": case "speech-stepfun": return false;
                default: return true;
            }
        }
        internal static string Description(string id)
        {
            switch (id)
            {
                case "translation-qwen": return "使用百炼对应地域的密钥。支持控制台的业务空间专属地址。";
                case "translation-zhipu": return "使用智谱通用 API Key 和已开通的 GLM 模型；Coding Plan 地址与密钥不通用。";
                case "translation-kimi": return "使用 Kimi 开放平台 API Key；填入账号可用的模型 ID。";
                case "translation-doubao": return "使用火山方舟 API Key；模型 ID 可填已开通的模型或 ep- 开头的推理接入点。";
                case "translation-minimax": return "此处为 MiniMax 国内通用 API。思考内容与英文结果分开处理；海外账号请修改 Base URL。";
                case "translation-tokenhub": return "腾讯新用户优先使用 TokenHub；填写该平台 API Key 和已开通模型 ID。";
                case "translation-hunyuan": return "供已有混元 API 账号使用；新开通的模型服务请在腾讯 TokenHub 中配置。";
                case "translation-baidu": return "使用千帆 v2 通用 API Key；不使用旧版 OAuth Access Token 或 AK/SK。";
                case "translation-stepfun": return "使用阶跃开放平台通用 API Key；不要填写订阅制 Step Plan 的调用地址。";
                case "translation-spark": return "密钥框填写对应模型的 HTTP APIPassword。X2 请把 Base URL 改为 https://spark-api-open.xf-yun.com/x2。";
                case "speech-stepfun": return "使用阶跃语音合成模型和该模型支持的音色 ID；返回 WAV 或 PCM。";
                default: return "";
            }
        }
        internal static string Docs(string id)
        {
            switch (id)
            {
                case "translation-qwen": return "https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope";
                case "translation-zhipu": return "https://docs.bigmodel.cn/cn/guide/develop/openai/introduction";
                case "translation-kimi": return "https://platform.kimi.com/docs/get-api-key";
                case "translation-doubao": return "https://docs.volcengine.com/docs/ark/compatible-with-openai-sdk?lang=zh";
                case "translation-minimax": return "https://platform.minimax.cn/docs/api-reference/text-openai-api";
                case "translation-tokenhub": return "https://cloud.tencent.com/document/product/1823/132061";
                case "translation-hunyuan": return "https://cloud.tencent.com/document/product/1729/111007";
                case "translation-baidu": return "https://cloud.baidu.com/doc/qianfan-docs/s/Fm9l6ocai";
                case "translation-stepfun": return "https://platform.stepfun.com/docs/zh/api-reference/chat/chat-completion-create";
                case "translation-spark": return "https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html";
                case "speech-stepfun": return "https://platform.stepfun.com/docs/zh/api-reference/audio/create-audio";
                default: return "";
            }
        }
    }
}
