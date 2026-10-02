# 从源码构建

这是开发中的Windows输入法，当前公开源码状态为Alpha。构建成功与干净电脑
安装验收是两项不同检查。本说明不要求已有青简或历史Mansur安装。

## 环境

- Windows 10/11 x64，Visual Studio 2022或Build Tools，C++桌面开发工具、
  Windows SDK、CMake与MSBuild/Roslyn。
- .NET Framework 4.8。使用系统Windows PowerShell 5.1构建桌面组件。
- 推荐Python 3.12 x64，加入PATH。纯Python检查已在3.14通过，但本地朗读
  基线kokoro-onnx 0.6.1要求Python >=3.10,<3.14，不能使用3.14运行该依赖。
  源码检查与本地模型运行兼容性须分别核对。
- 首次生成词库需要访问固定AOSP和jieba上游地址，下载校验SHA256。
- 4项本机TLS代理隔离检查使用Git for Windows自带的OpenSSL；没有该工具时
  会明确跳过这4项，不应把跳过写成通过。其他纯Python检查不需要真实API账户。

在解压或克隆后的仓库根目录执行，路径没有固定盘符要求：

```powershell
python scripts/prepare_lexicon.py --download
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Architecture x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Architecture Win32
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build_desktop.ps1
python -m unittest discover -s tests -p test_*.py
```

`build.ps1`构建并运行C++检查；`build_desktop.ps1`编译桌面EXE并运行无界面
检查。这些步骤不安装输入法、不更改默认输入法、不填写真实API密钥。
涉及真实编辑器窗口的检查只能在脚本保护的私有Windows桌面中显式执行。

生成运行用二进制词库：

```powershell
build/x64/Release/mansur_lexicon_compile.exe data/generated/syllables.tsv data/generated/base.mlex
```

输出位于`build/x64`、`build/Win32`、`build/desktop`和`data/generated`，
不属于公开源码提交内容。生成词库时保留NOTICE文件及来源清单。

## 学习服务

翻译和朗读可以独立使用API或本地模型。API使用者填写自己的厂家凭证，
源码包不提供开发者密钥。厂家设置说明见[DOMESTIC_PROVIDERS.md](DOMESTIC_PROVIDERS.md)。

全API路径仍需要可执行的Python worker，但不需要本地模型权重。选择本地
翻译需要兼容的llama-server与GGUF；选择本地朗读需要Kokoro-ONNX、ONNX Runtime、
NumPy及相应权重/音色文件。这些依赖与模型没有随源码包分发，许可需要单独核对。
设置可以在模型缺失时打开，学习失败不应阻断基本输入。

固定模型下载链接、运行组件版本、SHA256、Python依赖及设置位置见
[本地模型下载与配置](LOCAL_MODELS.md)与[下载清单](local-model-downloads.json)。
本地朗读依赖包含独立许可组件；不能把整套环境视为项目MIT代码。

## 安装件

`installer`与`scripts/package_trial.ps1`提供已有试用安装流程。源码中的
试用版本仍沿用local.32，冻结的历史包不可覆盖；制作下一版时必须统一修改
打包、安装、更新及观察脚本中的版本/前置版本，重新验证manifest与安装流程。
仅运行构建不代表生成了可在任意电脑安装的发行版。

未来二进制发行必须包含根LICENSE、版权说明以及涉及的第三方许可；模型/运行库
若另行打包，应重新核对全部依赖的分发条件。首次公开推荐开发预览源码，正式
安装发行前仍需干净Windows环境的安装、更新、卸载与数据保留验收。
