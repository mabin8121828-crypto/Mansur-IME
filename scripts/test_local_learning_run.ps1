# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CompanionPath,
    [Parameter(Mandatory=$true)][string]$ConfigurationPath,
    [Parameter(Mandatory=$true)][string]$DictionaryPath
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach($taskPath in @($CompanionPath,$ConfigurationPath,$DictionaryPath)) {
    if(-not [IO.Path]::IsPathRooted($taskPath) -or -not (Test-Path -LiteralPath $taskPath -PathType Leaf)) {throw 'An existing absolute fixture input path is required.'}
}
$taskOutput=Join-Path $taskRoot ('build\verification\local-learning-run-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskOutput)|Out-Null
$taskCompiler=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
foreach($taskName in @('LocalLearningRunProbe','LearningFocusLauncher')) {
    $taskArguments=@('/nologo','/target:exe','/platform:x64',('/out:'+(Join-Path $taskOutput ($taskName+'.exe'))))
    if($taskName -eq 'LocalLearningRunProbe') {$taskArguments+=@('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll')}
    $taskArguments+=Join-Path $taskRoot ('tests\desktop\'+$taskName+'.cs')
    & $taskCompiler @taskArguments
    if($LASTEXITCODE -ne 0){throw 'Private local-learning fixture compilation failed.'}
}
# Compilation affects only the dedicated verification target, never the installed TIP.
$taskCmake=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if(-not (Test-Path -LiteralPath $taskCmake)) {throw 'Visual Studio CMake is unavailable.'}
& $taskCmake -S $taskRoot -B (Join-Path $taskRoot 'build\x64')
if($LASTEXITCODE -ne 0){throw 'Core fixture configuration failed.'}
& $taskCmake --build (Join-Path $taskRoot 'build\x64') --config Release --target mansur_local_learning_source
if($LASTEXITCODE -ne 0){throw 'Core fixture compilation failed.'}
$taskSource=Join-Path $taskOutput 'source.json'
& (Join-Path $taskRoot 'build\x64\Release\mansur_local_learning_source.exe') $DictionaryPath $taskSource
if($LASTEXITCODE -ne 0){throw 'Fixed source confirmation failed.'}
Write-Output ('LOCAL_LEARNING_RUN_START evidence='+$taskOutput)
& (Join-Path $taskOutput 'LearningFocusLauncher.exe') (Join-Path $taskOutput 'LocalLearningRunProbe.exe') $CompanionPath $ConfigurationPath $taskSource $taskOutput
$taskCode=$LASTEXITCODE
$taskMeta=[ordered]@{actual_exit=$taskCode;companion_sha256=(Get-FileHash -LiteralPath $CompanionPath -Algorithm SHA256).Hash;
    scope='Fixed source only; private desktop never switched; own IPC/model/popup/audio; clipboard replaced by test sink. Not user application acceptance.'}
[IO.File]::WriteAllText((Join-Path $taskOutput 'execution.json'),($taskMeta|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
if($taskCode -ne 0){throw ('Local learning run failed; see '+$taskOutput)}
Write-Output ('LOCAL_LEARNING_RUN_PASS evidence='+$taskOutput)
