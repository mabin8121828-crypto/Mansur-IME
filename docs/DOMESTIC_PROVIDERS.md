# 国内模型服务配置

local.32，核对日期：2026-10-02。新增入口不会自动启用，不会借用其他服务的密钥。中文翻译与英文朗读分别选择服务、模型及当前用户加密密钥。配置入口和接口测试不代表已通过每家真实账户验证。

## 翻译

| 服务 | 默认 Base URL | 配置说明与官方依据 |
| --- | --- | --- |
| 通义千问 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | [百炼兼容接口](https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope)、[思考模式](https://help.aliyun.com/zh/model-studio/deep-thinking)、[请求参数](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions)。旧域名仍可用；可替换为控制台的业务空间专属域名，密钥与地域对应。通用混合思考模型显式关闭可选思考，选词查询也沿用该参数；已识别的强制思考模型保留模式及输出预算。 |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | [OpenAI 兼容](https://docs.bigmodel.cn/cn/guide/develop/openai/introduction)、[思考模式](https://docs.bigmodel.cn/cn/guide/capabilities/thinking-mode)。通用 API 与 Coding Plan 不通用。GLM 4.5/4.6/4.7/5/5.1/5.2关闭可选思考；5.3保留强制思考，不传不支持的关闭参数。 |
| Kimi | `https://api.moonshot.cn/v1` | [快速开始](https://platform.kimi.com/docs/get-api-key)、[参数参考](https://platform.kimi.com/docs/api/models-overview)。K2.6关闭可选思考，不显式传固定温度；K3使用low推理强度；K2.7 Code保留强制思考。 |
| 豆包 | `https://ark.cn-beijing.volces.com/api/v3` | [火山方舟兼容接口](https://docs.volcengine.com/docs/ark/compatible-with-openai-sdk?lang=zh)。填写已开通模型 ID 或自己的 ep- 推理接入点。 |
| MiniMax | `https://api.minimax.cn/v1` | [国内 OpenAI SDK](https://platform.minimax.cn/docs/api-reference/text-openai-api)。显式 reasoning_split，思考与content分开；M3关闭可选思考，强制思考模型保留模式及输出预算。海外账号可修改地址；不读取 M Plan 或其他程序的密钥。 |
| 腾讯 TokenHub | `https://tokenhub.tencentmaas.com/v1` | [官方调用示例](https://cloud.tencent.com/document/product/1823/132061)。腾讯新用户优先配置该平台已开通的模型及密钥。 |
| 腾讯混元 | `https://api.hunyuan.cloud.tencent.com/v1` | [混元兼容接口及迁移说明](https://cloud.tencent.com/document/product/1729/111007)。保留给已有 API 账号；新模型服务按官方指引使用 TokenHub。 |
| 百度千帆 | `https://qianfan.baidubce.com/v2` | [v2 OpenAI SDK](https://cloud.baidu.com/doc/qianfan-docs/s/Fm9l6ocai)、[模型列表](https://cloud.baidu.com/doc/qianfan-api/s/Dmba8k71y)。填写通用 API Key，不是旧版 OAuth Access Token 或 AK/SK。 |
| 阶跃星辰 | `https://api.stepfun.com/v1` | [开放平台](https://platform.stepfun.com/)、[对话接口](https://platform.stepfun.com/docs/zh/api-reference/chat/chat-completion-create)。通用 API 地址，与 Step Plan 订阅服务区分。 |
| 讯飞星火 | `https://spark-api-open.xf-yun.com/v1` | [HTTP接口](https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html)、[X2接口](https://www.xfyun.cn/doc/spark/X1http.html)。密钥框填对应模型的 HTTP APIPassword；X2使用`https://spark-api-open.xf-yun.com/x2`。 |

保留已有 DeepSeek、硅基流动、OpenRouter、OpenAI 和自定义兼容服务。本轮不固定新翻译服务的默认模型，避免未开通、已下线或订阅专用模型被自动使用。模型 ID 从厂商控制台复制。Kimi和百度新入口支持显式请求已文档化的 `/models`；其他新入口暂不探测未经核实的目录路径，“检查配置”仅验证本地格式与密钥可读性，不宣称联网认证成功。选择与打开设置不会请求模型。

## 英文朗读

新增阶跃星辰 `/v1/audio/speech`：[官方文档](https://platform.stepfun.com/docs/zh/api-reference/audio/create-audio)。默认模型`step-tts-mini`、音色`cixingnansheng`，可改为账号可用的模型与音色；请求 WAV/PCM 二进制，不请求音频 URL。显式传采样率，接受8000/16000/22050/24000/48000Hz；播放按完整WAV实际采样率或明确配置的PCM采样率。保留本地Kokoro及已有兼容语音入口。

百炼TTS、豆包TTS、MiniMax原生TTS等与目前二进制兼容接口不同的协议，本轮没有假装成可用朗读入口。语言模型入口不代表该厂商语音接口也已实现。

## 验证范围

- 桌面427项检查通过，包括18个服务的配置、原有32行配置迁移、密钥隔离及102种图标主题/尺寸组合。
- Python97项通过，包括千问/Kimi/GLM/MiniMax参数约束、官方域名限定、阶跃采样率和固定本机音频响应。
- 私有Windows桌面的自有设置窗：10个新增翻译入口浅色/深色实际可见；紧凑朗读配置可见。没有切换到用户桌面，没有调用厂商API或修改用户设置。
- 完整离屏内容可渲染；未显示Form的整窗DrawToBitmap会遗漏嵌套模型内容，不能据此断言真实界面空白。离屏检查与真实窗口检查分别记录。
- 尚无这10家对应账户密钥，因此不宣称真实调用、英译质量、发音质量、费用或延迟已全部验收。强制思考模型可能较慢，保留原30秒默认请求超时及失败后的中文输入。

千问模型模式依据核对日的官方文档识别。Omni等多模态协议没有扩展；新发布的模型、用户修改为其他域名的转发地址继续使用通用请求参数，不能据此认为任意模型自动适配。厂商密钥和订阅产品不一定通用，应使用对应API控制台的凭证。
