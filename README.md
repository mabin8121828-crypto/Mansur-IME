# Mansur

中文输入，也是一次英语学习机会。

Mansur是开发中的Windows输入法：正常输入中文或英文，快速三空格确认后显示英文
并朗读；在英文浮窗中选词，可以显式查释义或慢速朗读。

**公开署名：Mansur · 许可证：MIT · 当前阶段：Alpha**

## 功能

- 独立开发的中文输入核心：全拼、简拼/混拼、可选模糊音、分页候选与个人短词记忆。
- 轻按Shift切换中英，正常英文保留原文；学习后台故障不阻断基本输入。
- 中文确认句翻译为英文，英文原文可朗读、拖选和复制；不自动向编辑器写回译文。
- 现有英文浮窗内的选词释义、原文朗读和慢速学习，由用户明确触发。
- 翻译与朗读分别选择本地或API，可配置厂家、模型、音色与各自加密密钥。
- 统一浅/深色主题、可隐藏状态栏，个人词库导入导出。

国内翻译预设包含DeepSeek、千问、智谱GLM、Kimi、豆包、MiniMax、腾讯TokenHub、
混元、百度千帆、阶跃星辰、讯飞星火，另有OpenRouter、OpenAI、硅基流动及自定义
兼容入口。朗读可使用本地Kokoro或已实现的兼容接口，包括阶跃星辰。语言模型入口
不表示同厂的所有原生语音协议均已实现。见[厂家配置](docs/DOMESTIC_PROVIDERS.md)。

## 使用方式

使用本输入法选好中文词，或输入英文后，快速按三次空格，确认提交并学习。
Enter可只提交而不朗读。复制译文后由用户自行粘贴，不自动发送消息。

API密钥与模型由使用者自行准备。项目许可证允许商用，API调用仍可能收费，
模型及第三方运行库的分发许可也应独立核对。

## 本地模型下载

选择本地翻译可使用 Qwen GGUF 与 llama.cpp，选择本地朗读可使用 Kokoro。
详见[本地模型下载与配置](docs/LOCAL_MODELS.md)：包含固定下载链接、文件校验、
Python 兼容版本和设置步骤。两项服务可以混合使用本地与 API；只下载自己
需要的组件。模型权重在上游下载，不加入源码仓库。

## 构建与开发

先阅读[从源码构建](docs/BUILDING.md)。主要构建入口：

```powershell
python scripts/prepare_lexicon.py --download
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Architecture x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build_desktop.ps1
python -m unittest discover -s tests -p test_*.py
```

需要Windows、Visual Studio 2022 C++/MSBuild工具、Windows SDK、.NET Framework 4.8
和Python。源码不附带个人API凭证、模型权重或旧输入法运行环境。

目录：`core`/`include`为输入核心，`windows`为标准TSF集成，`desktop`为设置、
英文浮窗和音频播放，`learning`为隔离学习进程，`installer`为安装流程。
历史内部文件名、协议和安装路径仍保留MansurNext以兼容现有配置；产品公开名称为Mansur。

## 当前验证范围

local.32已在开发者本机安装并核验，桌面427项、Python97项检查通过，私有Windows
桌面的自有窗口亦有可见性/焦点检查。此为实验版基础，不等于任意Windows版本、
任意应用或全部厂家真实账号验收。干净电脑的完整安装、更新、卸载和数据保留
验证仍需完成；没有对应厂商密钥的预设不能宣称已经真实调用成功。

本项目不收集用户整句历史；学习请求只在明确确认后使用所选服务。
详见[隐私说明](docs/PRIVACY.md)与[贡献说明](CONTRIBUTING.md)。

## 许可与作者署名

Copyright (c) 2026 Mansur。Mansur是项目所有者使用的公开笔名。

项目自有代码按[MIT许可证](LICENSE)开放，允许商业使用、修改、二次分发和收费销售。
分发副本或实质部分时，必须保留上述版权署名和完整MIT许可与免责声明；二进制
分发也应携带许可文本。MIT不强制所有修改公开，也不强制在每个界面或广告署名。

第三方词库、图标与模型保留各自权利及条件，见[第三方说明](THIRD_PARTY_NOTICES.md)
和[版权范围](COPYRIGHT.md)。建议分发页注明“基于Mansur项目，由Mansur发起”，
这属于推荐描述，不是额外的许可限制。

[源码导出与发布准备](docs/OPEN_SOURCE.md)说明如何生成经过允许清单和完整性检查
的公开源码目录；本地导出不执行GitHub上传。
