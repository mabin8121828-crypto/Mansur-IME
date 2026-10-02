# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
# Pure gate checks shared by installation acceptance and isolated fixtures.
# Dot-sourcing does not register, activate, launch or change an installed product.
function Get-MansurVerificationExitStatus([object]$ExitCode) {
    if($ExitCode -isnot [int] -or $ExitCode -eq 10){return 'INCOMPLETE'}
    if($ExitCode -eq 0){return 'PASS'}
    return 'FAILED'
}
function Assert-MansurVerificationReceiptShape([object]$Record,[bool]$Probe,[int]$ExpectedFileCount) {
    if($Record.status -cin @('FAILED','INCOMPLETE')){return}
    $checks=@($Record.checks)
    if($Probe) {
        if($Record.status -cne 'PROBE_PASS' -or $Record.stage -cne 'environment-probe-complete' -or
           $Record.manifest_files -ne 0 -or $Record.lifecycle_cycles_per_architecture -ne 0 -or $checks.Count -ne 0) {
            throw 'Environment probe receipt cannot report installed acceptance.'
        }
        return
    }
    if($Record.status -cne 'PASS' -or $Record.stage -cne 'complete' -or $ExpectedFileCount -le 0 -or
       $Record.manifest_files -ne $ExpectedFileCount -or $Record.lifecycle_cycles_per_architecture -ne 60 -or $checks.Count -ne 4) {
        throw 'Installed verification receipt has incomplete acceptance evidence.'
    }
    $expected=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach($name in @('registration-x64','lifecycle-x64','registration-x86','lifecycle-x86')){$null=$expected.Add($name)}
    foreach($check in $checks) {
        if($check.name -isnot [string] -or -not $expected.Remove($check.name) -or $check.status -cne 'PASS' -or
           ($check.exit_code -isnot [int] -and $check.exit_code -isnot [long]) -or $check.exit_code -ne 0) {
            throw 'Installed verification receipt contains a missing, repeated or unsuccessful check.'
        }
    }
    if($expected.Count -ne 0){throw 'Installed verification receipt is missing an architecture check.'}
}
function Get-MansurRequiredInstalledEntries {
    return @('bin\MansurNext.Desktop.exe','bin\MansurNext.Desktop.exe.config',
        'bin\x64\mansur_next_tsf.dll','bin\x86\mansur_next_tsf.dll',
        'bin\x64\mansur_register.exe','bin\x86\mansur_register.exe',
        'bin\x64\data\base.mlex','bin\x86\data\base.mlex','bin\x64\mansur_lexicon_compile.exe',
        'learning\worker.py','learning\openrouter_provider.py','Manage-Trial.ps1',
        'Start-Companion.ps1','Restart-Companion.ps1','Update-Input.ps1','collect_health.ps1',
        'local-models.example.json','notices\NOTICE-AOSP.txt','notices\lexicon-manifest.json','README.md')
}
function Assert-MansurRegularPath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    $item=Get-Item -LiteralPath $full -ErrorAction Stop
    if($item.PSIsContainer){throw 'Expected a regular file.'}
    for($cursor=$full; -not [string]::IsNullOrEmpty($cursor); $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if((Get-Item -LiteralPath $cursor -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Verification path contains a reparse point.'}
    }
    return $item
}
function Test-MansurManifestRelativePath([object]$Value) {
    if($Value -isnot [string] -or $Value.Length -eq 0 -or $Value.Length -gt 1024 -or
       [IO.Path]::IsPathRooted($Value) -or $Value.IndexOfAny([char[]]@(':','/','"','<','>','|','?','*',[char]0)) -ge 0){return $false}
    foreach($part in $Value.Split([char]'\')) {
        if($part.Length -eq 0 -or $part -eq '.' -or $part -eq '..' -or $part.TrimEnd(' ','.') -cne $part -or
           $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' -or $part -match '[\x00-\x1f]'){return $false}
    }
    return $true
}
function Read-MansurFrozenManifest([string]$ReferenceManifest,[string]$Version,[string]$ExpectedHash) {
    if($Version -notmatch '^0\.1\.0-local\.[0-9]+$' -or $ExpectedHash -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Invalid frozen manifest identity.'}
    $info=Assert-MansurRegularPath $ReferenceManifest
    if($info.Length -gt 2097152){throw 'Manifest exceeds the bounded schema size.'}
    if((Get-FileHash -LiteralPath $ReferenceManifest -Algorithm SHA256).Hash -ne $ExpectedHash){throw 'Frozen manifest changed during verification.'}
    $manifest=Get-Content -LiteralPath $ReferenceManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.version -ne $Version -or -not $manifest.files -or @($manifest.files).Count -gt 10000){throw 'Manifest version or files list is invalid.'}
    $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $manifest.files) {
        if(-not (Test-MansurManifestRelativePath $entry.path) -or -not $seen.Add([string]$entry.path)){throw 'Manifest contains an unsafe or duplicate path.'}
        if(($entry.bytes -isnot [int] -and $entry.bytes -isnot [long]) -or $entry.bytes -lt 0 -or
           $entry.sha256 -isnot [string] -or $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Manifest contains invalid size or SHA256.'}
    }
    foreach($required in Get-MansurRequiredInstalledEntries) {
        if(-not $seen.Contains($required)){throw 'Manifest omits a required installed component.'}
    }
    # Bind the parsed data to the same frozen bytes, even if another writer races.
    if((Get-FileHash -LiteralPath $ReferenceManifest -Algorithm SHA256).Hash -ne $ExpectedHash){throw 'Frozen manifest changed while being read.'}
    return $manifest
}
function Assert-MansurInstalledIntegrity([string]$InstalledRoot,[string]$ReferenceManifest,[string]$Version,[string]$ExpectedHash) {
    $root=[IO.Path]::GetFullPath($InstalledRoot).TrimEnd('\')
    $manifest=Read-MansurFrozenManifest $ReferenceManifest $Version $ExpectedHash
    $installedManifest=Join-Path $root 'manifest.json'
    $null=Assert-MansurRegularPath $installedManifest
    if((Get-FileHash -LiteralPath $installedManifest -Algorithm SHA256).Hash -ne $ExpectedHash){throw 'Installed manifest does not match the frozen package.'}
    foreach($entry in $manifest.files) {
        $path=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
        if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Installed entry escaped the version directory.'}
        $info=Assert-MansurRegularPath $path
        if($info.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256){throw 'Installed file differs from the frozen package.'}
    }
    if((Get-FileHash -LiteralPath $installedManifest -Algorithm SHA256).Hash -ne $ExpectedHash){throw 'Installed manifest changed during verification.'}
    return [pscustomobject]@{version=$Version;manifest_sha256=$ExpectedHash.ToUpperInvariant();files=@($manifest.files).Count}
}
