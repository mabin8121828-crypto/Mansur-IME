# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source=Join-Path $workspace 'installer\Restart-Companion.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Installer script did not parse.'}
foreach($name in @('Test-LegacyLocalConfiguration','Find-LegacyLocalConfiguration')) {
    $function=$ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true) | Where-Object {$_.Name -eq $name}
    if(@($function).Count -ne 1){throw 'Expected one pure configuration helper.'}
    . ([scriptblock]::Create($function.Extent.Text))
}
$testRoot=[IO.Path]::GetFullPath((Join-Path $workspace ('build\installer-check-'+[Guid]::NewGuid().ToString('N'))))
$allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'build')).TrimEnd('\')+'\'
if(-not $testRoot.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid test directory.'}
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$checks=0
function Check([bool]$Condition,[string]$Name) {if(-not $Condition){throw $Name};$script:checks++}
try {
    $userRoot=Join-Path $testRoot 'state'; [IO.Directory]::CreateDirectory($userRoot) | Out-Null
    $fakeFile=Join-Path $testRoot 'fixture.exe'; [IO.File]::WriteAllBytes($fakeFile,[byte[]]@(1))
    $oldWorker=Join-Path $testRoot 'local.4\learning\worker.py'
    $local=[pscustomobject]@{python=$fakeFile;llama_server=$fakeFile;translation_model=$fakeFile;voice_model_dir=$testRoot;worker=$oldWorker}
    $api=[pscustomobject]@{translation_provider='openrouter';python=$fakeFile;llama_server='';translation_model='';voice_model_dir=$testRoot;worker=(Join-Path $testRoot 'local.6\learning\worker.py')}
    Check (Test-LegacyLocalConfiguration $local) 'valid-local-config'
    $invalidTuning=$local | ConvertTo-Json | ConvertFrom-Json
    $invalidTuning | Add-Member -NotePropertyName threads -NotePropertyValue 0
    Check (-not (Test-LegacyLocalConfiguration $invalidTuning)) 'invalid-legacy-tuning-rejected'
    Check (-not (Test-LegacyLocalConfiguration $api)) 'cloud-only-rejected-by-local-only-version'
    Check (-not (Test-LegacyLocalConfiguration 'invalid-scalar')) 'scalar-rejected'
    $backup=Join-Path $userRoot 'local-models.before-update.fixture.json'
    [IO.File]::WriteAllText($backup,($local | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $selected=Find-LegacyLocalConfiguration $api $oldWorker
    Check ($selected.worker -eq $oldWorker -and $selected.llama_server -eq $fakeFile) 'version-associated-local-backup-selected'
    Check ((Find-LegacyLocalConfiguration $local $oldWorker).worker -eq $oldWorker) 'existing-local-kept'
    $rejected=$false
    try {$null=Find-LegacyLocalConfiguration $api (Join-Path $testRoot 'other-version\worker.py')}catch {$rejected=$true}
    Check $rejected 'other-version-backup-not-silently-used'
    [IO.File]::WriteAllText($backup,'broken backup',[Text.UTF8Encoding]::new($false))
    $rejected=$false
    try {$null=Find-LegacyLocalConfiguration $api $oldWorker}catch {$rejected=$true}
    Check $rejected 'broken-backup-rejected-before-process-or-registration-actions'
    [pscustomobject]@{status='PASS';checks=$checks;scope='Pure installer configuration selection in isolated fixtures; no registration, process control or real user state.'} | ConvertTo-Json
} finally {
    # Exact UUID test directory was resolved beneath the workspace build directory above.
    if(-not ([IO.Path]::GetFullPath($testRoot)).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup boundary changed.'}
    [IO.Directory]::Delete($testRoot,$true)
}
