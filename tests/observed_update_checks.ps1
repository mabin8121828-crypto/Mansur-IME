# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $workspace 'scripts\run_update_observed.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Observed update script parse failed.'}
# Load isolated helpers only. The real updater and its main block never run.
foreach($name in @('Invoke-MansurUpdateChild','Publish-MansurUpdateReceipt')) {
    $definition=$ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true) | Where-Object Name -eq $name
    if(@($definition).Count -ne 1){throw 'Expected one observed update helper.'}
    Invoke-Expression $definition.Extent.Text
}
$folder=Join-Path $workspace ('build\verification\update-observer-fixture-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($folder) | Out-Null
$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$command=Join-Path $env:SystemRoot 'System32\cmd.exe'
$passed=0
foreach($code in @(0,7,10)) {
    $r=Invoke-MansurUpdateChild $command ('/d /c exit '+$code) (Join-Path $folder ('cmd-'+$code+'.out')) (Join-Path $folder ('cmd-'+$code+'.err')) 5000
    if($r.reason -or $r.exit_code -isnot [int] -or $r.exit_code -ne $code){throw 'Fast child exit missing.'}
    $passed++
    $scriptPath=Join-Path $folder ('exit-'+$code+'.ps1')
    [IO.File]::WriteAllText($scriptPath,("[Console]::Out.Write('fixture')`r`n[Console]::Error.Write('fixture-error')`r`nexit "+$code),[Text.UTF8Encoding]::new($false))
    $outFile=Join-Path $folder ('ps-'+$code+'.out');$errFile=Join-Path $folder ('ps-'+$code+'.err')
    $r=Invoke-MansurUpdateChild $shell ('-NoProfile -File "'+$scriptPath+'"') $outFile $errFile 5000
    if($r.reason -or $r.exit_code -ne $code -or [IO.File]::ReadAllText($outFile) -ne 'fixture' -or [IO.File]::ReadAllText($errFile) -ne 'fixture-error'){throw 'File child result lost.'}
    $passed++
}
$scriptPath=Join-Path $folder 'streams.ps1'
[IO.File]::WriteAllText($scriptPath,"[Console]::Out.Write(('O'*131072))`r`n[Console]::Error.Write(('E'*131072))`r`nexit 0",[Text.UTF8Encoding]::new($false))
$outFile=Join-Path $folder 'streams.out';$errFile=Join-Path $folder 'streams.err'
$r=Invoke-MansurUpdateChild $shell ('-NoProfile -File "'+$scriptPath+'"') $outFile $errFile 5000
if($r.reason -or $r.exit_code -ne 0 -or (Get-Item -LiteralPath $outFile).Length -ne 131072 -or (Get-Item -LiteralPath $errFile).Length -ne 131072){throw 'Stream draining blocked or lost output.'}
$passed++
$running=Join-Path $folder 'running.json';$result=Join-Path $folder 'result.json'
Publish-MansurUpdateReceipt ([ordered]@{stage='running';run_id='fixture'}) $running
$hash=(Get-FileHash -LiteralPath $running).Hash
Publish-MansurUpdateReceipt ([ordered]@{stage='completed';run_id='fixture';child=$r}) $result
if((Get-FileHash -LiteralPath $running).Hash -ne $hash -or (Get-Content -LiteralPath $result -Raw | ConvertFrom-Json).child.exit_code -ne 0){throw 'Terminal publication overwrote running evidence.'}
$passed++
$hash=(Get-FileHash -LiteralPath $result).Hash;$rejected=$false
try {Publish-MansurUpdateReceipt ([ordered]@{stage='replacement'}) $result}catch{$rejected=$true}
if(-not $rejected -or (Get-FileHash -LiteralPath $result).Hash -ne $hash){throw 'Existing terminal receipt was overwritten.'}
$passed++
[pscustomobject]@{status='PASS';checks=$passed;fixture=$folder;actual_update_invoked=$false} | ConvertTo-Json
