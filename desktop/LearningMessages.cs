// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;

namespace Mansur.Next.Desktop
{
    internal static class LearningMessages
    {
        // Only fixed codes cross this boundary. Never display server response bodies,
        // exception messages, input text, credential paths, or authorization headers.
        internal static string ForError(string code)
        {
            switch (code)
            {
                case "invalid_text":
                    return "文字已提交；本次没有可供英语学习的中英文文字。";
                case "configuration_unavailable":
                case "configuration_invalid":
                    return "学习服务尚未配置好。请打开设置中的模型与 API，检查并保存配置；中文输入可继续。";
                case "api_key_missing":
                case "api_key_unavailable":
                case "api_key_unreadable":
                case "api_key_invalid":
                case "openrouter_key_missing":
                    return "请在模型与 API 设置中填写或重新保存对应服务的密钥；文字已保留。";
                case "api_unauthorized":
                case "openrouter_unauthorized":
                    return "API 密钥未通过验证，请在设置中检查对应服务；文字已保留。";
                case "api_forbidden":
                    return "模型服务拒绝此请求，请检查服务权限、地区限制和系统代理；文字已保留。";
                case "api_payment_required":
                case "api_credit_required":
                case "openrouter_payment_required":
                    return "API 服务账户额度不足，请检查账户或切回本地模型；文字已保留。";
                case "api_rate_limited":
                case "openrouter_rate_limited":
                    return "API 服务请求较多，请稍后重试；文字已保留。";
                case "api_timeout":
                case "openrouter_timeout":
                case "translation_timeout":
                    return "学习服务等待超时，请检查网络或模型设置后重试；文字已保留。";
                case "api_model_not_found":
                case "api_model_missing":
                    return "请在模型与 API 设置中核对对应服务的模型 ID；文字已保留。";
                case "api_connection_failed":
                case "api_redirect_rejected":
                    return "暂时无法连接 API 服务，请检查网络或切回本地模型；文字已保留。";
                case "api_base_url_invalid": case "api_service_invalid": case "api_voice_missing": case "api_audio_format_invalid":
                    return "API 配置不完整，请检查对应服务的地址、模型、音色和音频格式；文字已保留。";
                case "voice_audio_invalid":
                    return "朗读服务返回了不支持的音频。请选支持 WAV 或 PCM 的模型并核对格式；文字已保留。";
                case "api_proxy_auth_required":
                    return "当前代理需要身份验证，此版本暂不支持；请调整系统代理后重试，文字已保留。";
                case "api_proxy_unsupported":
                    return "当前代理类型不受支持，请使用无需身份验证的 HTTP 代理或直连；文字已保留。";
                case "api_proxy_resolution_failed":
                    return "暂时无法确定系统代理路径，请检查 Windows 代理设置；文字已保留。";
                case "api_proxy_connect_failed":
                    return "代理未建立安全连接，请检查网络代理后重试；文字已保留。";
                default:
                    return "学习服务暂不可用。请在设置中检查模型与 API，或重启学习服务；中文输入可继续。";
            }
        }
    }
}
