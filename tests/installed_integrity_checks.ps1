# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $workspace 'scripts\Installed-Integrity.ps1')
$fixture=Join-Path $workspace ('build\verification\integrity-fixture-'+[Guid]::NewGuid().ToString('N'))
$installed=Join-Path $fixture 'installed'
$reference=Join-Path $fixture 'reference.json'
$version='0.1.0-local.7'
[IO.Directory]::CreateDirectory($installed) | Out-Null
$entries=@(foreach($relative in Get-MansurRequiredInstalledEntries) {
    $path=Join-Path $installed $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path,'fixture-'+$relative,[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{path=$relative;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}
})
$baseline=[pscustomobject]@{version=$version;files=$entries} | ConvertTo-Json -Depth 5
function Reset-Fixture {
    [IO.File]::WriteAllText($reference,$baseline,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $installed 'manifest.json'),$baseline,[Text.UTF8Encoding]::new($false))
    foreach($entry in $entries) {[IO.File]::WriteAllText((Join-Path $installed $entry.path),'fixture-'+$entry.path,[Text.UTF8Encoding]::new($false))}
}
function Save-Reference([object]$Manifest) {
    [IO.File]::WriteAllText($reference,($Manifest | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
}
function Invoke-Integrity {Assert-MansurInstalledIntegrity $installed $reference $version (Get-FileHash -LiteralPath $reference).Hash}
$passed=0
function Check([string]$Name,[scriptblock]$Action,[switch]$Reject) {
    Reset-Fixture
    $rejected=$false
    try {$null=& $Action} catch {$rejected=$true;if(-not $Reject){throw}}
    if($Reject -and -not $rejected){throw ('Expected rejection: '+$Name)}
    $script:passed++
    'PASS '+$Name
}
Check 'valid frozen and installed bytes' {if((Invoke-Integrity).files -ne 20){throw 'Required entries not all checked.'}}
Check 'installed file changed despite unchanged installed manifest' {[IO.File]::WriteAllText((Join-Path $installed $entries[0].path),'changed');Invoke-Integrity} -Reject
Check 'installed required file missing' {[IO.File]::Delete((Join-Path $installed $entries[0].path));Invoke-Integrity} -Reject
Check 'installed manifest missing' {[IO.File]::Delete((Join-Path $installed 'manifest.json'));Invoke-Integrity} -Reject
Check 'installed manifest cannot omit an entry' {
    $edited=$baseline | ConvertFrom-Json;$edited.files=@($edited.files | Select-Object -Skip 1)
    [IO.File]::WriteAllText((Join-Path $installed 'manifest.json'),($edited | ConvertTo-Json -Depth 5));Invoke-Integrity
} -Reject
Check 'installed manifest cannot rebase a changed file' {
    $edited=$baseline | ConvertFrom-Json;$target=Join-Path $installed $entries[0].path
    [IO.File]::WriteAllText($target,'changed');$edited.files[0].bytes=(Get-Item -LiteralPath $target).Length;$edited.files[0].sha256=(Get-FileHash -LiteralPath $target).Hash
    [IO.File]::WriteAllText((Join-Path $installed 'manifest.json'),($edited | ConvertTo-Json -Depth 5));Invoke-Integrity
} -Reject
Check 'reference cannot omit required component even with matching hash' {
    $edited=$baseline | ConvertFrom-Json;$edited.files=@($edited.files | Select-Object -Skip 1);Save-Reference $edited
    Read-MansurFrozenManifest $reference $version (Get-FileHash -LiteralPath $reference).Hash
} -Reject
Check 'reference duplicate path is case insensitive' {
    $edited=$baseline | ConvertFrom-Json;$extra=$baseline | ConvertFrom-Json;$extra.files[0].path=$extra.files[0].path.ToUpperInvariant()
    $edited.files=@($edited.files)+$extra.files[0];Save-Reference $edited
    Read-MansurFrozenManifest $reference $version (Get-FileHash -LiteralPath $reference).Hash
} -Reject
Check 'reference version must match request' {$edited=$baseline | ConvertFrom-Json;$edited.version='0.1.0-local.4';Save-Reference $edited;Read-MansurFrozenManifest $reference $version (Get-FileHash -LiteralPath $reference).Hash} -Reject
Check 'frozen hash captured before verification cannot change' {$hash=(Get-FileHash -LiteralPath $reference).Hash;[IO.File]::AppendAllText($reference,' ');Read-MansurFrozenManifest $reference $version $hash} -Reject
foreach($badSize in @(-1,'1',1.25)) {
    Check ('invalid byte size '+$badSize) {$edited=$baseline | ConvertFrom-Json;$edited.files[0].bytes=$badSize;Save-Reference $edited;Read-MansurFrozenManifest $reference $version (Get-FileHash -LiteralPath $reference).Hash} -Reject
}
Check 'invalid SHA256 rejected' {$edited=$baseline | ConvertFrom-Json;$edited.files[0].sha256='ABC';Save-Reference $edited;Read-MansurFrozenManifest $reference $version (Get-FileHash -LiteralPath $reference).Hash} -Reject
foreach($badPath in @('..\escape.dll','C:\escape.dll','bin/file.dll','bin\\file.dll','bin\.\file.dll','bin\file.','bin\file ','bin\NUL.txt','bin\file:stream')) {
    Check ('noncanonical path '+$badPath) {$edited=$baseline | ConvertFrom-Json;$edited.files[0].path=$badPath;Save-Reference $edited;Read-MansurFrozenManifest $reference $version (Get-FileHash -LiteralPath $reference).Hash} -Reject
}
Check 'directory cannot masquerade as regular file' {
    $target=Join-Path $installed $entries[0].path;[IO.File]::Delete($target);[IO.Directory]::CreateDirectory($target) | Out-Null;Invoke-Integrity
} -Reject
foreach($case in @(@(0,'PASS'),@(10,'INCOMPLETE'),@(1,'FAILED'),@($null,'INCOMPLETE'),@('0','INCOMPLETE'))) {
    if((Get-MansurVerificationExitStatus $case[0]) -ne $case[1]){throw 'Verification exit classification mismatch.'}
    $passed++
    'PASS fixed exit classification: '+$case[1]
}
function New-Receipt {
    return [pscustomobject]@{status='PASS';stage='complete';manifest_files=20;lifecycle_cycles_per_architecture=60
        checks=@(foreach($name in @('registration-x64','lifecycle-x64','registration-x86','lifecycle-x86')) {
            [pscustomobject]@{name=$name;status='PASS';exit_code=0}
        })}
}
function Check-Receipt([string]$Name,[scriptblock]$Action,[switch]$Reject) {
    $rejected=$false
    try {& $Action} catch {$rejected=$true;if(-not $Reject){throw}}
    if($Reject -and -not $rejected){throw ('Expected receipt rejection: '+$Name)}
    $script:passed++;'PASS receipt: '+$Name
}
Check-Receipt 'four unique successful checks' {Assert-MansurVerificationReceiptShape (New-Receipt) $false 20}
Check-Receipt 'missing check' {$r=New-Receipt;$r.checks=@($r.checks | Select-Object -Skip 1);Assert-MansurVerificationReceiptShape $r $false 20} -Reject
Check-Receipt 'duplicate check' {$r=New-Receipt;$r.checks[3].name='registration-x64';Assert-MansurVerificationReceiptShape $r $false 20} -Reject
Check-Receipt 'precondition exit cannot pass' {$r=New-Receipt;$r.checks[3].exit_code=10;Assert-MansurVerificationReceiptShape $r $false 20} -Reject
Check-Receipt 'missing exit cannot pass' {$r=New-Receipt;$r.checks[3].exit_code=$null;Assert-MansurVerificationReceiptShape $r $false 20} -Reject
Check-Receipt 'no file evidence cannot pass' {$r=New-Receipt;$r.manifest_files=0;Assert-MansurVerificationReceiptShape $r $false 20} -Reject
Check-Receipt 'wrong file count cannot pass' {$r=New-Receipt;$r.manifest_files=19;Assert-MansurVerificationReceiptShape $r $false 20} -Reject
Check-Receipt 'no lifecycle evidence cannot pass' {$r=New-Receipt;$r.lifecycle_cycles_per_architecture=0;Assert-MansurVerificationReceiptShape $r $false 20} -Reject
Check-Receipt 'wrong completed stage cannot pass' {$r=New-Receipt;$r.stage='environment';Assert-MansurVerificationReceiptShape $r $false 20} -Reject
Check-Receipt 'probe only accepts zero evidence' {
    $r=[pscustomobject]@{status='PROBE_PASS';stage='environment-probe-complete';manifest_files=0;lifecycle_cycles_per_architecture=0;checks=@()}
    Assert-MansurVerificationReceiptShape $r $true 20
}
Check-Receipt 'probe cannot claim installed pass' {Assert-MansurVerificationReceiptShape (New-Receipt) $true 20} -Reject
Check-Receipt 'incomplete remains incomplete' {$r=New-Receipt;$r.status='INCOMPLETE';$r.stage='environment';Assert-MansurVerificationReceiptShape $r $false 20}
# Leave this isolated fixture as evidence; no real installed path is read/written.
[pscustomobject]@{status='PASS';checks=$passed;fixture=$fixture} | ConvertTo-Json
