# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
$ErrorActionPreference='Stop'
$legacyRoot='E:\Qingjian-English'
$stamp=[DateTimeOffset]::Now.ToString('o')
$utf8=[Text.UTF8Encoding]::new($false)
$instruction=@'
## 2026-10-01 最新已确认：独立自研全拼工程

本节优先于下方历史方案。用户已完成需求选择：输入核心自行开发，不采用青简或其他现成输入引擎；允许使用许可明确的现成词库并保留必要说明。只做全拼，Windows 10/11 x64，统一输入接口与整句三空格流程。先跑通本地模型，OpenRouter API 后续接入。新工程为 D:/MansurIME，当前主目标/架构/状态分别见该目录 docs/PRODUCT_GOAL.md、ARCHITECTURE.md、STATUS.md。

此旧工程保留为历史与回退记录，不再扩展其输入引擎，不得依据下方“未批准重写”继续旧路线；不得删除旧许可证、品牌来源和用户词库。旧安装普通中文恢复、学习临时关闭的状态不变。新工程不得复制旧核心/TSF实现。Windows系统操作/Explorer重命名与游戏不纳入当前首版，不进行黑屏复现。用户操作UI，代理后台开发。当前有前台执行者，旧调度任务不得并发修改或部署。

'@
$agentsPath=Join-Path $legacyRoot 'AGENTS.md'
$existing=[IO.File]::ReadAllText($agentsPath)
if(-not $existing.StartsWith('## 2026-10-01 最新已确认：独立自研全拼工程')){
  [IO.File]::WriteAllText($agentsPath,$instruction+"`r`n"+$existing,$utf8)
}
$statePath=Join-Path $legacyRoot 'docs\EXECUTION_STATE.json'
$state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$state.phase='INDEPENDENT_FULL_PINYIN_REBUILD'
$state.next_action='Continue independent D:/MansurIME project; see its PRODUCT_GOAL/ARCHITECTURE/STATUS. Legacy code and installed recovery retained; do not resume old native engine or enable legacy learning. Local first; OpenRouter deferred.'
$state.foreground_setup_in_progress=$true
$state.updated_at=$stamp
[IO.File]::WriteAllText($statePath,($state | ConvertTo-Json -Depth 40)+"`r`n",$utf8)
$handoffPath=Join-Path $legacyRoot 'docs\SESSION_HANDOFF.md'
$handoff=[IO.File]::ReadAllText($handoffPath)
if(-not $handoff.StartsWith('## 2026-10-01 新方向已确认')){
  [IO.File]::WriteAllText($handoffPath,"## 2026-10-01 新方向已确认`r`n`r`n用户已选择独立自研全拼，许可明确的现成字词数据，Windows10/11，先本地翻译朗读。OpenRouter后续。新源码 D:/MansurIME，目标/框架/状态已保存。旧安装与数据保留，不复制旧输入核心，不重新开启旧学习，不继续Explorer重命名实验。旧记录以下保留为历史。`r`n`r`n---`r`n"+$handoff,$utf8)
}
Write-Output 'Legacy handoff updated; product installation and user configuration untouched.'
