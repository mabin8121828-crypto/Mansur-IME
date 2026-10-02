# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$CompanionPath)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not [IO.Path]::IsPathRooted($CompanionPath) -or -not (Test-Path -LiteralPath $CompanionPath -PathType Leaf)) {
    throw 'A built companion executable is required.'
}
$taskCompiler=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if(-not (Test-Path -LiteralPath $taskCompiler)) {throw 'Visual Studio C# compiler is unavailable.'}
$taskOutput=Join-Path $taskRoot ('build\verification\learning-focus-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
foreach($taskName in @('LearningFocusProbe','LearningFocusLauncher')) {
    $taskArguments=@('/nologo','/target:exe','/platform:x64',('/out:'+(Join-Path $taskOutput ($taskName+'.exe'))))
    if($taskName -eq 'LearningFocusProbe') {$taskArguments+=@('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll')}
    $taskArguments+=Join-Path $taskRoot ('tests\desktop\'+$taskName+'.cs')
    & $taskCompiler @taskArguments
    if($LASTEXITCODE -ne 0){throw 'Private desktop fixture compilation failed.'}
}
# The launcher creates a private desktop but never switches to it. Only its own
# disposable editor/windows receive focus. The clipboard is an injected sink.
& (Join-Path $taskOutput 'LearningFocusLauncher.exe') (Join-Path $taskOutput 'LearningFocusProbe.exe') $CompanionPath (Join-Path $taskOutput 'focus.log')
$taskCode=$LASTEXITCODE
$taskResult=[ordered]@{schema=1;status=$(if($taskCode -eq 0){'PASS'}else{'FAIL'});actual_exit=$taskCode;
    companion_sha256=(Get-FileHash -LiteralPath $CompanionPath -Algorithm SHA256).Hash;
    scope='Private-desktop waiting/result/redisplay: no activation and topmost/Z-order above own editor, explicit selection/keyboard-copy. No user desktop switch, input injection, real clipboard or model.'}
[IO.File]::WriteAllText((Join-Path $taskOutput 'result.json'),($taskResult|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
if($taskCode -ne 0){throw 'Learning-window focus regression failed; see the isolated fixture log.'}
Write-Output ('LEARNING_FOCUS_PASS evidence='+$taskOutput)
