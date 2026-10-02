# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=Join-Path $workspace 'build'
$packageRoot=Join-Path $build ('startup-installer-test-'+[Guid]::NewGuid().ToString('N'))
$version='0.1.0-local.7'
$checks=0
function Import-PureFunction([string]$file,[string]$name) {
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Invalid source syntax.'}
    $matches=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true))
    if($matches.Count -ne 1){throw 'Function selection was not unique.'}
    return [scriptblock]::Create($matches[0].Extent.Text)
}
. (Import-PureFunction (Join-Path $workspace 'installer\Manage-Trial.ps1') 'Verify-RecoveryHelpers')
. (Import-PureFunction (Join-Path $workspace 'installer\Update-Input.ps1') 'Read-Manifest')
. (Import-PureFunction (Join-Path $workspace 'installer\Start-Companion.ps1') 'Convert-MansurTimestampUtc')
function Check([bool]$value,[string]$name){if(-not $value){throw ('FAIL '+$name)};$script:checks++}
function Reject([scriptblock]$action){try{& $action | Out-Null;return $false}catch{return $true}}
try {
    [IO.Directory]::CreateDirectory($packageRoot) | Out-Null
    $files=@('bin\x64\mansur_register.exe','bin\x86\mansur_register.exe','bin\MansurNext.Desktop.exe','bin\MansurNext.Desktop.exe.config','Start-Companion.ps1','Restart-Companion.ps1','README.md','bin\x64\data\base.mlex')
    $entries=@()
    foreach($name in $files){
        $path=Join-Path $packageRoot $name
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path,'isolated fixture, never executed',[Text.Encoding]::UTF8)
        $entries+=@{path=$name;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}
    }
    @{version=$version;files=$entries} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding UTF8
    Verify-RecoveryHelpers -IncludeStartup
    Check $true 'complete-recovery-code-valid'
    [IO.File]::Delete((Join-Path $packageRoot 'README.md'))
    [IO.File]::Delete((Join-Path $packageRoot 'bin\x64\data\base.mlex'))
    Verify-RecoveryHelpers -IncludeStartup
    Check $true 'uninstall-does-not-require-readme-or-dictionary'
    $null=Read-Manifest $packageRoot $version $false $true
    Check $true 'rollback-startup-needs-only-required-code'
    Check (Reject {Read-Manifest $packageRoot $version}) 'ordinary-install-still-rejects-incomplete-package'
    [IO.File]::AppendAllText((Join-Path $packageRoot 'bin\MansurNext.Desktop.exe'),'changed')
    Check (Reject {Verify-RecoveryHelpers -IncludeStartup}) 'uninstall-rejects-modified-maintenance-code'
    Check (Reject {Read-Manifest $packageRoot $version $false $true}) 'rollback-rejects-modified-maintenance-code'
    Verify-RecoveryHelpers
    Check $true 'machine-recovery-only-requires-registration-tools'
    $iso='2026-09-30T23:57:57.1234567Z'
    $expected=[DateTimeOffset]::Parse($iso,[Globalization.CultureInfo]::InvariantCulture).UtcDateTime
    Check ((Convert-MansurTimestampUtc $iso) -eq $expected) 'timestamp-from-ps51-json-string'
    Check ((Convert-MansurTimestampUtc $expected) -eq $expected) 'timestamp-from-ps7-json-datetime'
    Check ((Convert-MansurTimestampUtc ([DateTimeOffset]$expected)) -eq $expected) 'timestamp-offset-value'
    [pscustomobject]@{status='PASS';checks=$checks;scope='isolated fixture files and timestamps only; no registry, startup folder, processes or model changes'} | ConvertTo-Json
} finally {
    $resolved=[IO.Path]::GetFullPath($packageRoot)
    if(-not $resolved.StartsWith(([IO.Path]::GetFullPath($build).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase) -or
       [IO.Path]::GetFileName($resolved) -notmatch '^startup-installer-test-[a-f0-9]{32}$'){throw 'Cleanup boundary check failed.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
