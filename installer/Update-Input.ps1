# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([switch]$Machine,[switch]$Rollback,[switch]$CheckOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$version='0.1.0-local.32'
$previousVersion='0.1.0-local.31'
$sourceRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$productRoot=Join-Path $env:ProgramFiles 'MansurNext'
$destinationRoot=Join-Path $productRoot $version
$previousRoot=Join-Path $productRoot $previousVersion
$registryPath='Software\Classes\CLSID\{E17225F9-B37A-4A39-A6FA-6EC6971CB481}\InprocServer32'
function Is-Administrator {
    return (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Read-Manifest([string]$root,[string]$expectedVersion,[bool]$recoveryOnly=$false,[bool]$startupOnly=$false) {
    if(-not [Environment]::Is64BitProcess){throw 'Use 64-bit Windows PowerShell.'}
    $manifest=Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    if($manifest.version -ne $expectedVersion -or $manifest.files.Count -lt 8){throw 'Unexpected update package.'}
    $entries=@($manifest.files)
    if($recoveryOnly -or $startupOnly) {
        $required=if($startupOnly){@('bin\MansurNext.Desktop.exe','bin\MansurNext.Desktop.exe.config','Start-Companion.ps1','Restart-Companion.ps1')}else{@('bin\x64\mansur_next_tsf.dll','bin\x86\mansur_next_tsf.dll','bin\x64\mansur_register.exe','bin\x86\mansur_register.exe','bin\x64\data\base.mlex','bin\x86\data\base.mlex')}
        $entries=@($manifest.files | Where-Object {$required -contains $_.path})
        if(@($entries.path | Select-Object -Unique).Count -ne $required.Count){throw 'Previous input components are incomplete.'}
    }
    foreach($entry in $entries) {
        $relative=[string]$entry.path
        if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.([\\/]|$)'){throw 'Unsafe update entry.'}
        $file=[IO.Path]::GetFullPath((Join-Path $root $relative))
        if(-not $file.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Update entry escaped package.'}
        $info=Get-Item -LiteralPath $file
        if($info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
           $info.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256){throw "Update integrity failed: $relative"}
    }
    return $manifest
}
function Registered-Path([string]$architecture) {
    $view=if($architecture -eq 'x64'){[Microsoft.Win32.RegistryView]::Registry64}else{[Microsoft.Win32.RegistryView]::Registry32}
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
    try {
        $key=$base.OpenSubKey($registryPath)
        if(-not $key){return ''}
        try{return [string]$key.GetValue('')}finally{$key.Dispose()}
    }finally{$base.Dispose()}
}
function Module-Path([string]$root,[string]$architecture){return (Join-Path $root "bin\$architecture\mansur_next_tsf.dll")}
function Run-Helper([string]$root,[string]$architecture,[string[]]$arguments) {
    & (Join-Path $root "bin\$architecture\mansur_register.exe") @arguments
    if($LASTEXITCODE -ne 0){throw "Registration step failed for $architecture"}
}
function Request-Machine([bool]$restore) {
    $script=Join-Path $sourceRoot 'Update-Input.ps1'
    if($script.Contains('"')){throw 'Invalid update path.'}
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'" -Machine'
    if($restore){$arguments+=' -Rollback'}
    $process=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -Wait -PassThru
    if($process.ExitCode -ne 0){throw 'Windows update step failed. Previous version files were retained.'}
}
try {
    # Both directions use this package's current-user startup maintenance helper.
    $manifest=Read-Manifest $sourceRoot $version $false ([bool]$Rollback)
    $null=Read-Manifest $previousRoot $previousVersion ([bool]$Rollback)
    foreach($directory in @($productRoot,$previousRoot,$destinationRoot)) {
        if((Test-Path -LiteralPath $directory) -and ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Version directories must not be reparse points.'}
    }
    foreach($architecture in @('x64','x86')) {
        $registered=Registered-Path $architecture
        if(-not ($registered.Equals((Module-Path $previousRoot $architecture),[StringComparison]::OrdinalIgnoreCase) -or
                $registered.Equals((Module-Path $destinationRoot $architecture),[StringComparison]::OrdinalIgnoreCase))){throw 'The registered version is not this update source or target.'}
    }
    if($CheckOnly){'UPDATE_CHECK_OK: previous package and update payload verified; no state changed';exit 0}
    if($Machine) {
        if(-not (Is-Administrator)){throw 'Windows administrator confirmation is required.'}
        if($Rollback) {
            foreach($architecture in @('x86','x64')) {Run-Helper $previousRoot $architecture @('--install',(Module-Path $previousRoot $architecture))}
            exit 0
        }
        if(Test-Path -LiteralPath $destinationRoot) {
            $null=Read-Manifest $destinationRoot $version
            if((Get-FileHash -LiteralPath (Join-Path $destinationRoot 'manifest.json')).Hash -ne
               (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'manifest.json')).Hash){throw 'Different files already occupy the target version directory.'}
        } else {
            $stagingRoot=[IO.Path]::GetFullPath((Join-Path $productRoot ('.staging-'+$version+'-'+[Guid]::NewGuid().ToString('N'))))
            $resolvedDestination=[IO.Path]::GetFullPath($destinationRoot)
            $boundary=[IO.Path]::GetFullPath($productRoot).TrimEnd('\')+'\'
            if(-not $stagingRoot.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or
               -not $resolvedDestination.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or
               [IO.Path]::GetFileName($resolvedDestination) -ne $version){throw 'Staging move escaped the product directory.'}
            New-Item -ItemType Directory -Path $stagingRoot | Out-Null
            foreach($entry in $manifest.files) {
                $target=Join-Path $stagingRoot $entry.path
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
                Copy-Item -LiteralPath (Join-Path $sourceRoot $entry.path) -Destination $target
            }
            Copy-Item -LiteralPath (Join-Path $sourceRoot 'manifest.json') -Destination (Join-Path $stagingRoot 'manifest.json')
            $null=Read-Manifest $stagingRoot $version
            # Both absolute paths were checked above; Directory.Move rejects an
            # existing target instead of merging a partial directory into it.
            [IO.Directory]::Move($stagingRoot,$resolvedDestination)
        }
        $null=Read-Manifest $destinationRoot $version
        try {
            foreach($architecture in @('x86','x64')) {Run-Helper $destinationRoot $architecture @('--install',(Module-Path $destinationRoot $architecture))}
            'machine_update_ok; old version retained' | Set-Content -LiteralPath (Join-Path $destinationRoot 'machine-result.txt') -Encoding UTF8
        } catch {
            $updateError=$_
            $failed=@()
            foreach($architecture in @('x86','x64')) {try{Run-Helper $previousRoot $architecture @('--install',(Module-Path $previousRoot $architecture))}catch{$failed+=$architecture}}
            ('update_failed; rollback_failed='+($failed -join ',')) | Set-Content -LiteralPath (Join-Path $destinationRoot 'machine-result.txt') -Encoding UTF8
            throw $updateError
        }
    } else {
        if(Is-Administrator){throw 'Run this update normally; only its machine step needs elevation.'}
        if($Rollback) {
            & (Join-Path $sourceRoot 'Restart-Companion.ps1') -TargetRoot $previousRoot -CheckOnly
            if($LASTEXITCODE -ne 0){throw 'Rollback model configuration is not ready; input registration was not changed.'}
        }
        $inputChanged=$false
        foreach($taskArch in @('x64','x86')) {
            if((Get-FileHash -LiteralPath (Module-Path $previousRoot $taskArch)).Hash -ne (Get-FileHash -LiteralPath (Module-Path $sourceRoot $taskArch)).Hash){$inputChanged=$true}
        }
        Request-Machine ([bool]$Rollback)
        $activeRoot=if($Rollback){$previousRoot}else{$destinationRoot}
        try {
            Run-Helper $activeRoot 'x64' @('--enable')
            foreach($architecture in @('x64','x86')) {Run-Helper $activeRoot $architecture @('--verify',(Module-Path $activeRoot $architecture))}
        } catch {
            $verificationError=$_
            if(-not $Rollback){
                Request-Machine $true
                Run-Helper $previousRoot 'x64' @('--enable')
                foreach($architecture in @('x64','x86')) {Run-Helper $previousRoot $architecture @('--verify',(Module-Path $previousRoot $architecture))}
            }
            throw $verificationError
        }
        # Refresh our own companion so its settings/toolbar match the input DLL.
        # No browser, editor, chat application or user draft is closed here.
        & (Join-Path $sourceRoot 'Restart-Companion.ps1') -TargetRoot $activeRoot
        if($LASTEXITCODE -ne 0){throw 'Input registration was updated, but the learning companion could not restart. Exit Mansur Next from its tray menu, then run the start shortcut.'}
        . (Join-Path $sourceRoot 'Start-Companion.ps1')
        try {
            $null=Invoke-MansurStartupMaintenance (Join-Path $sourceRoot 'bin\MansurNext.Desktop.exe') 'refresh' (Join-Path $activeRoot 'bin\MansurNext.Desktop.exe')
        } catch {throw ('Input and companion updated, but the login startup setting could not be refreshed. Check the login startup option in settings. '+$_.Exception.Message)}
        try {$null=New-MansurSettingsShortcut (Join-Path $activeRoot 'bin\MansurNext.Desktop.exe')} catch {Write-Warning 'Desktop settings shortcut could not be created; use the package settings entry.'}
        'UPDATE_OK: native profile verified and companion restarted.'
        ''
        '更新已完成，英文伴读后台已启动。'
        if($Rollback) {
            '回退后的原生输入组件在软件下次正常启动时加载；请先保存未提交的草稿。'
        } else {
            if($inputChanged){'学习后台和设置入口已更新。保存工作后，可统一重启 Windows 一次，以重新加载新版输入组件；安装不会关闭你的软件。'}
            else {'学习浮窗已更新，可以直接使用。本轮未更换输入组件，无需重启 Windows 或其他软件。'}
        }
    }
    exit 0
} catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
