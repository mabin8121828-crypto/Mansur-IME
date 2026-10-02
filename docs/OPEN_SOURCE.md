# 开源与分发

公开署名：**Mansur**（项目所有者的笔名）。项目自有代码使用标准MIT许可证，
允许商业使用、修改和二次分发，无需向原作者另行付费或申请商用授权。

二次分发必须保留`Copyright (c) 2026 Mansur`以及完整MIT许可与免责声明。
二进制包也应携带这些文本，例如放在LICENSE文件或法律声明目录。推荐在
分发页说明“基于Mansur项目，由Mansur发起”，但这条推荐不是附加许可条件。
MIT不要求每个界面或广告展示作者名称，也不要求公开所有修改。

第三方数据和图标保留各自的作者与许可证。模型许可和API服务费用与项目MIT
许可分开。详见根[COPYRIGHT.md](../COPYRIGHT.md)和
[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)。

## 当前公开仓库

项目已由所有者明确授权发布到
[Mansur-IME](https://github.com/mabin8121828-crypto/Mansur-IME)。
首页、海报、图文手册及反馈入口均在仓库中；当前定位为 Alpha 源码。
公开素材来源见[图片说明](media/README.md)。

## 后续发布准备

1. 使用`python scripts/export_open_source.py`生成允许清单内的源码目录和ZIP。
   输出仅在`output/open-source`内创建新目录，不覆盖既有导出，不包含Git历史。
2. 查看导出manifest，确认署名、许可证及第三方许可齐全；检查没有本机配置、
   凭证、个人截图和历史诊断。只纳入明确批准的固定演示图片与海报，
   并保留来源和摘要清单。程序检查常见密钥模式；人工核对仍有价值。
3. 在导出目录运行[构建步骤](BUILDING.md)。独立构建通过不能代替干净电脑
   安装、升级、卸载及真人应用验收，首次公开定位为Alpha实验项目。
4. 按项目所有者授权的账号、仓库与范围发布。本地导出和 MIT 许可本身
   不自动授权另建仓库、扩大共享或上传个人数据。

可公开署笔名；私下保留账号控制、源文件、开发过程及版本快照等关联证据。
公开署名不要求将身份证号、住址或身份证照片放入源码。软件著作权登记
不属于本项目当前实施计划。

## 官方依据

- [MIT许可证](https://opensource.org/license/mit)
- [GitHub仓库许可说明](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)
- [开源定义与商业使用](https://opensource.org/faq)

许可证文本保留官方英文原文；本页中文是说明，不修改许可证条件。
