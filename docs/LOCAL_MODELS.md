# 本地模型下载与配置

翻译与英文朗读可以分别选择本地模型或 API。下面列出当前项目使用的固定下载
基线，方便独立电脑复现；源码不附带模型权重，也不会自动下载这些文件。

## 需要下载哪些内容

| 翻译方式 | 朗读方式 | 需要准备 |
| --- | --- | --- |
| API | API | Python 运行程序、两项服务各自的凭证；不需要本地模型 |
| API | 本地 | Python、Kokoro 依赖、朗读模型和音色文件 |
| 本地 | API | Python、Qwen GGUF、llama.cpp 运行组件及朗读服务凭证 |
| 本地 | 本地 | 下方全部本地组件；不需要模型服务 API 密钥 |

API 设置见[厂家配置](DOMESTIC_PROVIDERS.md)。翻译和朗读独立配置，不必两项
都使用同一厂家。这里只介绍 Windows x64 本地运行；其他架构尚未验收。

## 1. 本地翻译：Qwen 与 llama.cpp

### 翻译模型

- 基础模型：[Qwen3-4B-Instruct-2507 官方模型页](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507)，Apache-2.0。
- GGUF 量化发布者：Unsloth；[固定版本目录](https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/tree/a06e946bb6b655725eafa393f4a9745d460374c9)。这是量化转换文件，不是 Qwen 官方直接发布的 GGUF。
- 下载：[Qwen3-4B-Instruct-2507-Q5_K_M.gguf](https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/a06e946bb6b655725eafa393f4a9745d460374c9/Qwen3-4B-Instruct-2507-Q5_K_M.gguf)，2,889,514,080 字节，约 2.89 GB。

下载后保留在自己的模型目录，例如 `C:\MansurModels\translation\`。无需改名，
在设置里选中该 GGUF 文件即可。不要把大模型加入源码仓库或源码 ZIP。

### 翻译运行组件

使用与当前启动参数一致的 llama.cpp **b11146**：

- [版本说明及下载列表](https://github.com/ggml-org/llama.cpp/releases/tag/b11146)
- [Windows x64 Vulkan ZIP](https://github.com/ggml-org/llama.cpp/releases/download/b11146/llama-b11146-bin-win-vulkan-x64.zip)，32,127,004 字节。

完整解压，例如放在 `C:\MansurModels\llama-b11146\`；保留同目录所有 DLL、
许可证和随包声明，不要只复制 `llama-server.exe`。设置中的“翻译运行组件”
指向解压目录内的 `llama-server.exe`。此版本基线不表示任意最新版本都兼容。

运行组件包含可选 Vulkan 加速，本项目也有 CPU 路径。不同电脑的模型速度、
显存占用和翻译质量仍需实际验证，不以成功下载代替性能或质量验收。

## 2. 本地英文朗读：Kokoro

从 [Kokoro-ONNX model-files-v1.0 发布页](https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0)
下载以下两项，放在**同一个文件夹**，保留原文件名：

| 文件 | 用途 | 下载 |
| --- | --- | --- |
| `kokoro-v1.0.onnx` | 朗读模型；325,532,387 字节 | [下载模型](https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx) |
| `voices-v1.0.bin` | 配套音色；28,214,398 字节 | [下载音色](https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin) |

例如保存到 `C:\MansurModels\voice\`，设置中的“朗读模型文件夹”选择这个
文件夹。项目已有 Heart、Bella、Michael、Emma 音色；在“英语朗读”页选择。
基础模型 [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) 标注 Apache-2.0；
ONNX 转换代码的 MIT 许可与模型权重许可是两回事。

## 3. Python 与朗读依赖

学习进程需要 Python；本地朗读还需要 `kokoro-onnx`、ONNX Runtime、NumPy 和
发音处理依赖。可从 [Python 官方 Windows 页面](https://www.python.org/downloads/windows/)
查看 Python x64 的获取方式。

当前本地朗读基线为 **Python 3.12 x64 + kokoro-onnx 0.6.1**，开发机实际运行
过 Python 3.12.9。[kokoro-onnx 0.6.1 的版本要求](https://pypi.org/project/kokoro-onnx/0.6.1/)
是 Python `>=3.10,<3.14`；**不能将 Python 3.14 用于这套本地朗读依赖**。
源码的纯 Python 检查在 3.14 通过，不代表该版本可以运行 Kokoro。

已安装 Python 3.12 且 `py -3.12` 可用时，可为朗读新建独立环境：

```powershell
py -3.12 -m venv C:\MansurModels\voice-runtime
C:\MansurModels\voice-runtime\Scripts\python.exe -m pip install "kokoro-onnx==0.6.1"
```

在设置中将“Python 运行程序”指向
`C:\MansurModels\voice-runtime\Scripts\python.exe`。以上是供使用者执行的配置
步骤；项目没有随源码分发 Python 环境，尚未完成干净电脑逐项安装验收。
固定 Kokoro 包版本不等于锁定全部间接依赖的版本。

这些依赖保留各自许可。特别是
[phonemizer](https://github.com/bootphon/phonemizer/blob/master/LICENSE) 与
[eSpeak NG](https://github.com/espeak-ng/espeak-ng/blob/master/COPYING) 使用 GPL 许可，
不能把现有整套 Python 环境复制到发行包后统称 MIT。若另外制作离线运行包，
需要按实际包含的组件保留许可，并处理相应的源码提供义务。

## 4. 在 Mansur 设置中选择文件

1. 打开“Mansur 输入法设置”，进入“模型与 API”。
2. 点击“运行环境”，填写上面的“Python 运行程序”。
3. 翻译服务选本地时，填写“翻译模型”和“翻译运行组件”。
4. 朗读服务选本地时，填写“朗读模型文件夹”。
5. 保存设置。用固定短句完成一次英文显示与朗读检查，再尝试日常输入。

API 模式填写自己对应服务的地址、模型和密钥；不要在 GitHub 发布个人凭证。
上述路径仅为例子，可以选择其他目录，无需使用开发者电脑的路径。

## 文件校验与版本记录

[下载清单](local-model-downloads.json)记录固定链接、文件大小、SHA256、版本与
许可来源。在 PowerShell 中对下载文件执行：

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath C:\MansurModels\translation\Qwen3-4B-Instruct-2507-Q5_K_M.gguf
```

对其他三项也分别计算，结果与清单中的 `sha256` 比较；ZIP 应在解压前校验。
Qwen 哈希已与 Hugging Face 元数据核对，llama.cpp ZIP 哈希已与 GitHub 发布
元数据核对。两个 Kokoro 文件的发布元数据没有提供摘要，清单使用当前本地
实际文件的校验值；它们是项目复现基线，不冒称上游签名。

三个模型/音色文件合计 3,243,260,865 字节，约 3.24 GB；另需运行组件、Python
依赖及解压空间。源码 ZIP 只保留下载说明和清单。上游链接失效或文件摘要
不匹配时先停止使用该文件，再核对新来源，不静默换成未知镜像。

项目自有代码的商业使用许可与这些模型、依赖的分发条件独立；完整边界见
[第三方说明](../THIRD_PARTY_NOTICES.md)。
