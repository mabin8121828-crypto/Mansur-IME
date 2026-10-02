# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([ValidateSet('Check','Install','InstallMachine','Start','Inspect','Uninstall','RemoveMachine')][string]$Action='Check')
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$version='0.1.0-local.32'
$packageRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$productRoot=Join-Path $env:ProgramFiles 'MansurNext'
$installRoot=Join-Path $productRoot $version
$classPath='Software\Classes\CLSID\{E17225F9-B37A-4A39-A6FA-6EC6971CB481}\InprocServer32'
$userRoot=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.mansur-next'
$configPath=Join-Path $userRoot 'local-models.json'
. (Join-Path $PSScriptRoot 'Start-Companion.ps1')
function Is-Admin {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Registered-Module([Microsoft.Win32.RegistryView]$view) {
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
    try { $key=$base.OpenSubKey($classPath); if($null -eq $key){return ''}; try{return [string]$key.GetValue('')}finally{$key.Dispose()} }
    finally{$base.Dispose()}
}
function Verify-Package([string]$root) {
    if(-not [Environment]::Is64BitOperatingSystem){throw 'This trial requires x64 Windows 10 or 11.'}
    if(-not [Environment]::Is64BitProcess){throw 'Run this installer with 64-bit Windows PowerShell.'}
    $manifest=Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.version -ne $version){throw 'Package version mismatch.'}
    if($manifest.files.Count -lt 8){throw 'Incomplete package manifest.'}
    foreach($entry in $manifest.files) {
        $relative=[string]$entry.path
        if([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or $relative.Contains(':')){throw 'Unsafe package entry.'}
        $file=[IO.Path]::GetFullPath((Join-Path $root $relative))
        if(-not $file.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Package path escaped root.'}
        $info=Get-Item -LiteralPath $file
        if($info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Package must contain regular files.'}
        if($info.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256){throw "Package integrity failed: $relative"}
    }
    return $manifest
}
function Invoke-Helper([string]$root,[string]$architecture,[string[]]$arguments) {
    $helper=Join-Path $root "bin\$architecture\mansur_register.exe"
    & $helper @arguments
    if($LASTEXITCODE -ne 0){throw "Registration helper failed: $architecture"}
}
function Verify-RecoveryHelpers([switch]$IncludeStartup) {
    # Recovery must not depend on an intact installed dictionary or README.
    $manifest=Get-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.version -ne $version){throw 'Recovery package version mismatch.'}
    $required=@('bin\x64\mansur_register.exe','bin\x86\mansur_register.exe')
    if($IncludeStartup){$required+=@('bin\MansurNext.Desktop.exe','bin\MansurNext.Desktop.exe.config')}
    foreach($relative in $required) {
        $entries=@($manifest.files | Where-Object {$_.path -eq $relative})
        if($entries.Count -ne 1){throw 'Recovery helper is absent from manifest.'}
        $file=Join-Path $packageRoot $relative
        $info=Get-Item -LiteralPath $file
        if($info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
           $info.Length -ne $entries[0].bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entries[0].sha256){throw 'Recovery helper integrity check failed.'}
    }
}
function Model-ConfigurationSource {
    if(Test-Path -LiteralPath $configPath) {
        return $configPath
    } elseif(Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MansurNext\local-models.json')) {
        return (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MansurNext\local-models.json')
    } else {
        return (Join-Path $packageRoot 'local-models.example.json')
    }
}
function Run-Machine([string]$operation) {
    $script=Join-Path $packageRoot 'Manage-Trial.ps1'
    if($script.Contains('"')){throw 'Unsupported package path.'}
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'" -Action '+$operation
    $process=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -PassThru -Wait
    if($process.ExitCode -ne 0){throw "Machine step did not complete ($operation). See machine-result.txt in the install directory if present."}
}
function Machine-Install {
    if(-not (Is-Admin)){throw 'Windows administrator confirmation is required.'}
    $manifest=Verify-Package $packageRoot
    foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
        if(Registered-Module $view){throw 'Mansur Next is already registered. This first trial installer will not overwrite it.'}
    }
    $reuse=Test-Path -LiteralPath $installRoot
    if($reuse) {
        $null=Verify-Package $installRoot
        if((Get-FileHash -LiteralPath (Join-Path $packageRoot 'manifest.json')).Hash -ne
           (Get-FileHash -LiteralPath (Join-Path $installRoot 'manifest.json')).Hash){throw 'A different package occupies this version directory; no files were overwritten.'}
    }
    foreach($directory in @($productRoot,$installRoot)) {
        if((Test-Path -LiteralPath $directory) -and ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Install directory must not be a reparse point.'}
    }
    $registrationStarted=$false
    try {
        if(-not $reuse) {
            $stagingRoot=[IO.Path]::GetFullPath((Join-Path $productRoot ('.staging-'+$version+'-'+[Guid]::NewGuid().ToString('N'))))
            $resolvedDestination=[IO.Path]::GetFullPath($installRoot)
            $boundary=[IO.Path]::GetFullPath($productRoot).TrimEnd('\')+'\'
            if(-not $stagingRoot.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or
               -not $resolvedDestination.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or
               [IO.Path]::GetFileName($resolvedDestination) -ne $version){throw 'Install staging escaped the product directory.'}
            New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
            foreach($entry in $manifest.files) {
                $target=Join-Path $stagingRoot $entry.path
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
                Copy-Item -LiteralPath (Join-Path $packageRoot $entry.path) -Destination $target
            }
            Copy-Item -LiteralPath (Join-Path $packageRoot 'manifest.json') -Destination (Join-Path $stagingRoot 'manifest.json')
            $null=Verify-Package $stagingRoot
            [IO.Directory]::Move($stagingRoot,$resolvedDestination)
        }
        $null=Verify-Package $installRoot
        $registrationStarted=$true
        foreach($architecture in @('x86','x64')) {
            Invoke-Helper $installRoot $architecture @('--install',(Join-Path $installRoot "bin\$architecture\mansur_next_tsf.dll"))
        }
        'registered; default input method unchanged' | Set-Content -LiteralPath (Join-Path $installRoot 'machine-result.txt') -Encoding UTF8
    } catch {
        $installError=$_
        if($registrationStarted) {
            $rollbackFailures=@()
            foreach($architecture in @('x86','x64')) {
                try { Invoke-Helper $packageRoot $architecture @('--uninstall') } catch { $rollbackFailures+=$architecture }
            }
            ($installError.Exception.Message+"; rollback_failed="+($rollbackFailures -join ',')) | Set-Content -LiteralPath (Join-Path $installRoot 'machine-result.txt') -Encoding UTF8
        }
        throw $installError
    }
}
function Machine-Remove {
    if(-not (Is-Admin)){throw 'Windows administrator confirmation is required.'}
    Verify-RecoveryHelpers
    foreach($pair in @(@('x64',[Microsoft.Win32.RegistryView]::Registry64),@('x86',[Microsoft.Win32.RegistryView]::Registry32))) {
        $registered=Registered-Module $pair[1]
        $expected=Join-Path $installRoot ('bin\'+$pair[0]+'\mansur_next_tsf.dll')
        if($registered -and -not $registered.Equals($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'A different Mansur Next version is registered; removal stopped.'}
    }
    foreach($architecture in @('x86','x64')) { Invoke-Helper $packageRoot $architecture @('--uninstall') }
    # Keep versioned files: an application may still have this DLL loaded.
    # Removing only our registration rolls back availability without killing apps.
    if(Test-Path -LiteralPath $installRoot) {'unregistered; user data and version files retained' | Set-Content -LiteralPath (Join-Path $installRoot 'machine-result.txt') -Encoding UTF8}
}
try {
    switch($Action) {
        'Check' {
            $manifest=Verify-Package $packageRoot
            "CHECK_OK version=$version files=$($manifest.files.Count) model_configuration=optional default_changed=false"
        }
        'InstallMachine' { Machine-Install }
        'Install' {
            if(Is-Admin){throw 'Run Install without administrator privileges; it will request Windows confirmation for its machine step.'}
            $null=Verify-Package $packageRoot
            $sourceConfig=Model-ConfigurationSource
            Run-Machine 'InstallMachine'
            try {
                Invoke-Helper $installRoot 'x64' @('--enable')
                foreach($architecture in @('x64','x86')) {
                    Invoke-Helper $installRoot $architecture @('--verify',(Join-Path $installRoot "bin\$architecture\mansur_next_tsf.dll"))
                }
            } catch {
                Run-Machine 'RemoveMachine'
                throw
            }
            New-Item -ItemType Directory -Path $userRoot -Force | Out-Null
            Initialize-MansurUserState (Join-Path $installRoot 'bin\MansurNext.Desktop.exe') $sourceConfig -AllowIncomplete
            $running=Start-MansurCompanion (Join-Path $installRoot 'bin\MansurNext.Desktop.exe') $configPath
            try {$null=New-MansurSettingsShortcut (Join-Path $installRoot 'bin\MansurNext.Desktop.exe')} catch {Write-Warning 'Desktop settings shortcut could not be created; use the package settings entry.'}
            'COMPANION_RUNNING pid='+$running.Id+' model='+$running.State
            "INSTALL_OK version=$version profile=Mansur-Next default_changed=false config=$configPath"
        }
        'Start' {
            if(Is-Admin){throw 'Start the learning desktop normally, not as administrator.'}
            $null=Verify-Package $installRoot
            Assert-MansurCompanionVersion (Join-Path $installRoot 'bin\MansurNext.Desktop.exe')
            $sourceConfig=Model-ConfigurationSource
            New-Item -ItemType Directory -Path $userRoot -Force | Out-Null
            Initialize-MansurUserState (Join-Path $installRoot 'bin\MansurNext.Desktop.exe') $sourceConfig -AllowIncomplete
            $running=Start-MansurCompanion (Join-Path $installRoot 'bin\MansurNext.Desktop.exe') $configPath
            'COMPANION_RUNNING pid='+$running.Id+' model='+$running.State
        }
        'Inspect' {
            foreach($architecture in @('x64','x86')) { Invoke-Helper $installRoot $architecture @('--inspect') }
        }
        'Uninstall' {
            if(Is-Admin){throw 'Run Uninstall as the normal signed-in user; its machine step will request Windows confirmation.'}
            Verify-RecoveryHelpers -IncludeStartup
            Run-Machine 'RemoveMachine'
            $null=Invoke-MansurStartupMaintenance (Join-Path $packageRoot 'bin\MansurNext.Desktop.exe') 'remove'
            'UNREGISTERED: switch to another input method. Exit Mansur Next from its tray menu. User data and version files retained.'
        }
        'RemoveMachine' { Machine-Remove }
    }
    exit 0
} catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
