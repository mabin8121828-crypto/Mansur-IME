# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version='0.1.0-local.32'
$package=Join-Path $root "dist\MansurNext-$version"
if(Test-Path -LiteralPath $package){throw 'Package output already exists. Use a new trial version instead of overwriting a reviewed package.'}
$copies=@(
    @('LICENSE','LICENSE'),
    @('COPYRIGHT.md','COPYRIGHT.md'),
    @('NOTICE','NOTICE'),
    @('THIRD_PARTY_NOTICES.md','THIRD_PARTY_NOTICES.md'),
    @('build\x64\windows\Release\mansur_next_tsf.dll','bin\x64\mansur_next_tsf.dll'),
    @('build\Win32\windows\Release\mansur_next_tsf.dll','bin\x86\mansur_next_tsf.dll'),
    @('build\x64\Release\mansur_register.exe','bin\x64\mansur_register.exe'),
    @('build\Win32\Release\mansur_register.exe','bin\x86\mansur_register.exe'),
    @('build\x64\Release\mansur_lexicon_compile.exe','bin\x64\mansur_lexicon_compile.exe'),
    @('data\generated\base.mlex','bin\x64\data\base.mlex'),
    @('data\generated\base.mlex','bin\x86\data\base.mlex'),
    @('build\desktop\MansurNext.Desktop.exe','bin\MansurNext.Desktop.exe'),
    @('build\desktop\MansurNext.Desktop.exe.config','bin\MansurNext.Desktop.exe.config'),
    @('desktop\iconassets\product\Mansur.ico','bin\Mansur.ico'),
    @('desktop\iconassets\product\SOURCE.txt','notices\product-icon-source.txt'),
    @('learning\worker.py','learning\worker.py'),
    @('learning\openrouter_provider.py','learning\openrouter_provider.py'),
    @('learning\api_provider.py','learning\api_provider.py'),
    @('learning\api_stream.py','learning\api_stream.py'),
    @('learning\selection_study.py','learning\selection_study.py'),
    @('data\generated\NOTICE-AOSP.txt','notices\NOTICE-AOSP.txt'),
    @('data\generated\NOTICE-JIEBA.txt','notices\NOTICE-JIEBA.txt'),
    @('desktop\iconassets\lucide\LICENSE','notices\NOTICE-LUCIDE.txt'),
    @('desktop\iconassets\lucide\REVISION.txt','notices\lucide-revision.txt'),
    @('desktop\iconassets\services\LICENSE','notices\NOTICE-LOBE-ICONS.txt'),
    @('desktop\iconassets\services\sources.json','notices\service-icons-sources.json'),
    @('data\generated\lexicon-manifest.json','notices\lexicon-manifest.json'),
    @('installer\Manage-Trial.ps1','Manage-Trial.ps1'),
    @('installer\Update-Input.ps1','Update-Input.ps1'),
    @('installer\Restart-Companion.ps1','Restart-Companion.ps1'),
    @('installer\Start-Companion.ps1','Start-Companion.ps1'),
    @('scripts\collect_health.ps1','collect_health.ps1'),
    @('docs\LOCAL_TRIAL.md','README.md'),
    @('docs\lexicon-example.tsv','lexicon-example.tsv')
)
foreach($pair in $copies){if(-not (Test-Path -LiteralPath (Join-Path $root $pair[0]) -PathType Leaf)){throw "Missing build output: $($pair[0])"}}
New-Item -ItemType Directory -Path $package -Force | Out-Null
foreach($pair in $copies) {
    $target=Join-Path $package $pair[1]
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $pair[0]) -Destination $target
}
$modelConfig=[ordered]@{
    python=''
    worker=('C:\Program Files\MansurNext\'+$version+'\learning\worker.py')
    llama_server=''
    translation_model=''
    voice_model_dir=''
    translation_provider='local'; speech_provider='local'; translation_service='translation-openrouter'; speech_service='speech-openai'; openrouter_model=''; translation_timeout_seconds=30
    device='auto'; gpu_layers=99; threads=4
}
$modelConfig | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'local-models.example.json') -Encoding UTF8
foreach($entry in @(@('安装试用版.cmd','Install'),@('启动英文伴读.cmd','Start'),@('退出试用安装.cmd','Uninstall'))) {
    $batch="@echo off`r`nchcp 65001 >nul`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Manage-Trial.ps1`" -Action $($entry[1])`r`npause`r`n"
    [IO.File]::WriteAllText((Join-Path $package $entry[0]),$batch,[Text.UTF8Encoding]::new($false))
}
$updateBatch="@echo off`r`nchcp 65001 >nul`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Update-Input.ps1`"`r`npause`r`n"
[IO.File]::WriteAllText((Join-Path $package '更新现有试用版.cmd'),$updateBatch,[Text.UTF8Encoding]::new($false))
$settingsBatch="@echo off`r`nchcp 65001 >nul`r`nstart `"`" `"%ProgramFiles%\MansurNext\$version\bin\MansurNext.Desktop.exe`" --show-settings`r`n"
[IO.File]::WriteAllText((Join-Path $package '打开输入法设置.cmd'),$settingsBatch,[Text.UTF8Encoding]::new($false))
$files=@(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($package.Length+1);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
[ordered]@{version=$version;created_utc=[DateTime]::UtcNow.ToString('o');scope='trial; independently configurable local or OpenAI-compatible translation and speech; models separate';files=$files} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8
Compress-Archive -LiteralPath $package -DestinationPath ($package+'.zip')
Get-Item -LiteralPath $package,($package+'.zip') | Select-Object FullName,Length
