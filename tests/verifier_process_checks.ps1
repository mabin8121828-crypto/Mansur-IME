# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $workspace 'scripts\Installed-Integrity.ps1')
# Load only the production function definitions; never run the installer gate.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $workspace 'scripts\verify_installed.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Verifier parse failed.'}
foreach($name in @('Invoke-MansurVerifierProcess','Run-Check')) {
    $definition=$ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true) | Where-Object Name -eq $name
    if(@($definition).Count -ne 1){throw 'Expected one production process function.'}
    Invoke-Expression $definition.Extent.Text
}
$evidence=Join-Path $workspace ('build\verification\process-fixture-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$command=Join-Path $env:SystemRoot 'System32\cmd.exe'
$passed=0;$oldNullCount=0
foreach($code in @(0,7,10)) {
    $legacy=Start-Process -FilePath $command -ArgumentList ('/d /c exit '+$code) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $evidence ('old-'+$code+'.out')) -RedirectStandardError (Join-Path $evidence ('old-'+$code+'.err'))
    try {if(-not $legacy.WaitForExit(5000)){throw 'Fixed legacy fixture timed out.'};if($null -eq $legacy.ExitCode){$oldNullCount++}} finally {$legacy.Dispose()}
    foreach($attempt in 1..3) {
        $r=Invoke-MansurVerifierProcess $command ('/d /c exit '+$code) (Join-Path $evidence ('new-'+$code+'-'+$attempt+'.out')) (Join-Path $evidence ('new-'+$code+'-'+$attempt+'.err')) 5000
        if($r.reason -or $r.exit_code -isnot [int] -or $r.exit_code -ne $code){throw 'Controlled fast process exit was not captured.'}
        $passed++
    }
    $checks=New-Object 'Collections.Generic.List[object]';$stage='fixture';$status='INCOMPLETE';$threw=$false
    try {Run-Check ('route-'+$code) $command ('/d /c exit '+$code)} catch {$threw=$true}
    $expected=Get-MansurVerificationExitStatus $code
    if($checks.Count -ne 1 -or $checks[0].status -ne $expected -or $checks[0].exit_code -ne $code -or $threw -ne ($code -ne 0)){throw 'Production Run-Check classified fixed exit incorrectly.'}
    if($code -eq 10 -and $checks[0].reason -ne 'PRECONDITION'){throw 'Exit 10 lost its prerequisite reason.'}
    $passed++
}
$streams=Join-Path $evidence 'streams.ps1'
[IO.File]::WriteAllText($streams,"[Console]::Out.Write(('O' * 131072))`r`n[Console]::Error.Write(('E' * 131072))`r`nexit 7`r`n",[Text.UTF8Encoding]::new($false))
$outFile=Join-Path $evidence 'streams.out';$errFile=Join-Path $evidence 'streams.err'
$r=Invoke-MansurVerifierProcess $shell ('-NoProfile -File "'+$streams+'"') $outFile $errFile 5000
if($r.reason -or $r.exit_code -ne 7 -or (Get-Item -LiteralPath $outFile).Length -ne 131072 -or (Get-Item -LiteralPath $errFile).Length -ne 131072){throw 'Concurrent stdout/stderr was lost or blocked.'}
$passed++
$exitScript=Join-Path $evidence 'exit10.ps1'
[IO.File]::WriteAllText($exitScript,'exit 10',[Text.UTF8Encoding]::new($false))
foreach($preserve in @($false,$true)) {
    $inner="& '"+$shell+"' -NoProfile -File '"+$exitScript+"'"
    if($preserve){$inner+='; exit $LASTEXITCODE'}
    $r=Invoke-MansurVerifierProcess $shell ('-NoProfile -Command "'+$inner+'"') (Join-Path $evidence ('wrapper-'+$preserve+'.out')) (Join-Path $evidence ('wrapper-'+$preserve+'.err')) 5000
    $expected=if($preserve){10}else{1}
    if($r.reason -or $r.exit_code -ne $expected){throw 'PowerShell command wrapper exit propagation changed.'}
    $passed++
}
$marker=Join-Path $evidence 'late-completed.txt'
$late=Join-Path $evidence 'late.ps1'
[IO.File]::WriteAllText($late,("[Threading.Thread]::Sleep(600)`r`n[IO.File]::AppendAllText('"+$marker+"','finished')`r`nexit 0"),[Text.UTF8Encoding]::new($false))
$r=Invoke-MansurVerifierProcess $shell ('-NoProfile -File "'+$late+'"') (Join-Path $evidence 'late.out') (Join-Path $evidence 'late.err') 50
if($r.reason -ne 'OBSERVATION_TIMEOUT' -or $null -ne $r.exit_code){throw 'Timeout must remain incomplete.'}
[Threading.Thread]::Sleep(1500)
if(-not (Test-Path -LiteralPath $marker) -or [IO.File]::ReadAllText($marker) -ne 'finished'){throw 'Timed-out fixture was terminated or replayed.'}
$passed++
[pscustomobject]@{status='PASS';checks=$passed;legacy_null_exit_reproductions=$oldNullCount;fixture=$evidence} | ConvertTo-Json
