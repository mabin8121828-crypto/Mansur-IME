# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$TargetRoot,[switch]$CheckOnly)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Start-Companion.ps1')
Set-StrictMode -Version 2
$productRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'MansurNext')).TrimEnd('\')
$target=[IO.Path]::GetFullPath($TargetRoot).TrimEnd('\')
$userRoot=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.mansur-next'
$configPath=Join-Path $userRoot 'local-models.json'
function Verified-Companion([string]$root) {
    $resolved=[IO.Path]::GetFullPath($root).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($resolved) -ne $productRoot -or
       [IO.Path]::GetFileName($resolved) -notmatch '^0\.1\.0-local\.[0-9]+$') {throw 'Unexpected companion version directory.'}
    if((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Companion version must not be a reparse point.'}
    $manifest=Get-Content -LiteralPath (Join-Path $resolved 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.version -ne [IO.Path]::GetFileName($resolved)){throw 'Companion manifest version mismatch.'}
    $required=@('bin\MansurNext.Desktop.exe','bin\MansurNext.Desktop.exe.config','learning\worker.py')
    $runtimeEntries=@($manifest.files | Where-Object {([string]$_.path).StartsWith('learning\',[StringComparison]::OrdinalIgnoreCase)} | ForEach-Object {[string]$_.path})
    foreach($relative in @($required+$runtimeEntries | Select-Object -Unique)) {
        if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.([\\/]|$)'){throw 'Unsafe companion runtime entry.'}
        $entries=@($manifest.files | Where-Object {$_.path -eq $relative})
        if($entries.Count -ne 1){throw 'Companion manifest entry is missing.'}
        $file=Join-Path $resolved $relative
        $info=Get-Item -LiteralPath $file
        if($info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
           $info.Length -ne $entries[0].bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entries[0].sha256){throw 'Companion integrity check failed.'}
    }
    return (Join-Path $resolved 'bin\MansurNext.Desktop.exe')
}
function Test-LegacyLocalConfiguration([object]$Configuration) {
    if($null -eq $Configuration -or $Configuration -isnot [pscustomobject]){return $false}
    $provider=$Configuration.PSObject.Properties['translation_provider']
    if($provider -and [string]$provider.Value -ne 'local'){return $false}
    foreach($name in @('python','llama_server','translation_model')) {
        $field=$Configuration.PSObject.Properties[$name]
        if(-not $field -or $field.Value -isnot [string] -or -not [IO.Path]::IsPathRooted($field.Value) -or
           -not (Test-Path -LiteralPath $field.Value -PathType Leaf)){return $false}
    }
    foreach($limit in @(@('threads',1,32),@('gpu_layers',0,999))) {
        $field=$Configuration.PSObject.Properties[$limit[0]]
        if($field -and (($field.Value -isnot [int] -and $field.Value -isnot [long]) -or
            $field.Value -lt $limit[1] -or $field.Value -gt $limit[2])){return $false}
    }
    $device=$Configuration.PSObject.Properties['device']
    if($device -and ($device.Value -isnot [string] -or [string]::IsNullOrEmpty($device.Value) -or $device.Value.Length -gt 80 -or
        $device.Value.IndexOfAny([char[]]@([char]13,[char]10,[char]0)) -ge 0)){return $false}
    $voice=$Configuration.PSObject.Properties['voice_model_dir']
    return ($voice -and $voice.Value -is [string] -and [IO.Path]::IsPathRooted($voice.Value) -and (Test-Path -LiteralPath $voice.Value -PathType Container))
}
function Find-LegacyLocalConfiguration([object]$Current,[string]$TargetWorker) {
    if(Test-LegacyLocalConfiguration $Current){return $Current}
    # Only our bounded, version-associated configuration backups are candidates.
    foreach($file in @(Get-ChildItem -LiteralPath $userRoot -Filter 'local-models*.json' -File -ErrorAction SilentlyContinue |
        Where-Object {$_.Name -match '^local-models(?:\.before-update\.|\.backup\.|\.json\.before-settings\.)' -and $_.Length -le 65536} |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 64)) {
        try {
            $candidate=Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            if($candidate -isnot [pscustomobject]){continue}
            $priorWorker=$candidate.PSObject.Properties['worker']
            if($priorWorker -and ([string]$priorWorker.Value).Equals($TargetWorker,[StringComparison]::OrdinalIgnoreCase) -and
               (Test-LegacyLocalConfiguration $candidate)){return $candidate}
        } catch { }
    }
    throw 'The older version needs a valid local-model configuration. No compatible pre-update backup was found; the current companion was not stopped.'
}
try {
    if(-not [Environment]::Is64BitProcess){throw 'Use 64-bit Windows PowerShell.'}
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run companion restart as the normal signed-in user, not as administrator.'}
    $executable=Verified-Companion $target
    $supportsSettingsBootstrap=([IO.Path]::GetFileName($target) -match '^0\.1\.0-local\.(\d+)$' -and [int]$Matches[1] -ge 6)
    $sourceConfig=$configPath
    if(-not (Test-Path -LiteralPath $sourceConfig)){$sourceConfig=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MansurNext\local-models.json'}
    if(-not (Test-Path -LiteralPath $sourceConfig) -and $supportsSettingsBootstrap){$sourceConfig=Join-Path $target 'local-models.example.json'}
    $originalConfig=[IO.File]::ReadAllText($sourceConfig,[Text.Encoding]::UTF8)
    $config=$null
    try {$config=$originalConfig | ConvertFrom-Json} catch {if(-not $supportsSettingsBootstrap){throw}}
    if($null -ne $config -and $config -isnot [pscustomobject]) {
        if(-not $supportsSettingsBootstrap){throw 'Previous companion configuration is not an object.'}
        $config=$null
    }
    $recoveryExecutable=$null
    if($config){try {$recoveryExecutable=Verified-Companion (Split-Path -Parent (Split-Path -Parent ([string]$config.worker)))}catch { }}
    $worker=Join-Path $target 'learning\worker.py'
    if(-not (Test-Path -LiteralPath $worker -PathType Leaf)){throw 'Companion worker is missing.'}
    if(-not $supportsSettingsBootstrap){$config=Find-LegacyLocalConfiguration $config $worker}
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $companions=@(Get-Process -Name 'MansurNext.Desktop' -ErrorAction SilentlyContinue | Where-Object {$_.SessionId -eq $session})
    $verified=@()
    foreach($process in $companions) {
        $path=[IO.Path]::GetFullPath($process.Path)
        $root=Split-Path -Parent (Split-Path -Parent $path)
        if(-not $root.StartsWith($productRoot+'\',[StringComparison]::OrdinalIgnoreCase)){continue}
        $expected=Verified-Companion $root
        if(-not $path.Equals($expected,[StringComparison]::OrdinalIgnoreCase)){continue}
        $metadata=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.Id)
        $owner=Invoke-CimMethod -InputObject $metadata -MethodName GetOwnerSid
        if($owner.ReturnValue -ne 0 -or $owner.Sid -ne $identity){continue}
        $verified+=@{Process=$process;Start=$process.StartTime.ToUniversalTime();Path=$path}
    }
    if($CheckOnly){'COMPANION_CHECK_OK: package, configuration and existing companion identities verified';exit 0}
    # Shut down only the exact installed processes validated above. A session-wide
    # control command could otherwise close a source/portable companion that was
    # deliberately excluded from this update. WM_QUIT unwinds Application.Run and
    # disposes the selected process's own model/audio resources normally.
    if(-not ('MansurUpdate.CompanionExit' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace MansurUpdate {
 public static class CompanionExit {
  private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
  [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, IntPtr parameter);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
  [DllImport("user32.dll", SetLastError=true)] private static extern bool PostThreadMessage(uint thread,uint message,IntPtr w,IntPtr l);
  [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenThread(uint access,bool inherit,uint thread);
  [DllImport("kernel32.dll")] private static extern uint GetProcessIdOfThread(IntPtr thread);
  [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
  public static int Request(Process process,DateTime expectedStart,string expectedPath,int expectedSession) {
   int sent=0;
   // Pin the validated process identity; a bare PID may be recycled on exit.
   IntPtr processHandle=process.Handle;
   if(process.HasExited)return 0;
   if(process.StartTime.ToUniversalTime()!=expectedStart || process.SessionId!=expectedSession ||
      !String.Equals(process.MainModule.FileName,expectedPath,StringComparison.OrdinalIgnoreCase))
       throw new InvalidOperationException("Companion identity changed before shutdown.");
   int processId=process.Id;
    foreach(ProcessThread thread in process.Threads) {
     if(process.HasExited)break;
     IntPtr pinnedThread=OpenThread(0x0800,false,(uint)thread.Id);
     if(pinnedThread==IntPtr.Zero)continue;
     try {
     if(GetProcessIdOfThread(pinnedThread)!=(uint)processId)continue;
     bool ownsWindow=false;
     WindowCallback callback=delegate(IntPtr window,IntPtr unused) {
      uint owner; GetWindowThreadProcessId(window,out owner);
      if(owner==(uint)processId)ownsWindow=true;
      return true;
     };
     EnumThreadWindows((uint)thread.Id,callback,IntPtr.Zero);
     if(ownsWindow&&PostThreadMessage((uint)thread.Id,0x0012,IntPtr.Zero,IntPtr.Zero))sent++;
     } finally {CloseHandle(pinnedThread);}
    }
   GC.KeepAlive(processHandle);
   GC.KeepAlive(process);
   return sent;
  }
 }
}
'@
    }
    foreach($entry in $verified) {
        $process=$entry.Process
        if($process.HasExited){continue}
        # Revalidate identity immediately before the bounded shutdown request.
        $process.Refresh()
        if($process.StartTime.ToUniversalTime() -ne $entry.Start -or $process.Path -ne $entry.Path){throw 'Companion process identity changed.'}
        [void][MansurUpdate.CompanionExit]::Request($process,$entry.Start,$entry.Path,$session)
        if(-not $process.WaitForExit(8000)){throw 'Exit Mansur Next using its tray menu, then start the updated companion. No application was force-terminated.'}
    }
    if($config){$config | Add-Member -NotePropertyName worker -NotePropertyValue $worker -Force}
    $backup=$null
    $configurationWritten=$false
    try {
        if($config) {
            $backup=Save-MansurConfiguration $config $configPath
            $configurationWritten=$true
        }
        # Older rollback targets do not implement the shared-state migration CLI.
        if(Test-Path -LiteralPath (Join-Path $target 'Start-Companion.ps1')) {
            Initialize-MansurUserState $executable $sourceConfig -AllowIncomplete:$supportsSettingsBootstrap
        }
        $launched=Start-MansurCompanion $executable $configPath
    } catch {
        $launchError=$_
        $restoreOk=$false
        if($configurationWritten) {
            try {
                $saved=$originalConfig | ConvertFrom-Json
                $null=Save-MansurConfiguration $saved $configPath
                $restoreOk=$true
            } catch { Write-Warning 'Configuration recovery could not complete. The backup is retained.' }
        } else {
            try {$restoreOk=([IO.File]::ReadAllText($configPath,[Text.Encoding]::UTF8) -ceq $originalConfig)}catch { }
        }
        if($restoreOk -and $recoveryExecutable) {
            try {
                $restored=Start-MansurCompanion $recoveryExecutable $configPath
                'PREVIOUS_COMPANION_RESTORED pid='+$restored.Id+' model='+$restored.State
            } catch { Write-Warning 'Previous configuration restored; companion restart could not be confirmed.' }
        }
        throw $launchError
    }
    'COMPANION_RUNNING pid='+$launched.Id+' model='+$launched.State
    exit 0
} catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
