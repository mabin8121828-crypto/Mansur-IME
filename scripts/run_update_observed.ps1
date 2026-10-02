# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
# Prepared for an explicitly dispatched versioned update. Do not use this script
# to retry an older updater whose completion is unknown; audit that install.
[CmdletBinding()]
param(
    [ValidateSet('0.1.0-local.32')][string]$Version='0.1.0-local.32',
    [Parameter(Mandatory=$true)][string]$RunId,
    [Parameter(Mandatory=$true)][string]$ExpectedSid,
    [Parameter(Mandatory=$true)][int]$ExpectedSession,
    [Parameter(Mandatory=$true)][string]$ExpectedManifestHash
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
if($RunId -cnotmatch '^[a-f0-9]{32}$' -or $ExpectedSid -notmatch '^S-1-[0-9-]+$' -or
   $ExpectedSession -lt 0 -or $ExpectedManifestHash -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Invalid observed update identity.'}
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-MansurUpdateIdentity {
    if(-not ('MansurObservedUpdate.Package' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MansurObservedUpdate {
 public static class Package {
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint count,IntPtr name);
  public static int Query() {uint count=0;return GetCurrentPackageFullName(ref count,IntPtr.Zero);}
 }
}
'@
    }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent();$self=[Diagnostics.Process]::GetCurrentProcess()
    try {return [pscustomobject]@{pid=$self.Id;sid=$identity.User.Value;session=$self.SessionId;path=$self.MainModule.FileName
        started_utc=$self.StartTime.ToUniversalTime().ToString('o');package_result=[MansurObservedUpdate.Package]::Query()
        elevated=(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)}}
    finally {$self.Dispose();$identity.Dispose()}
}
function Publish-MansurUpdateReceipt([object]$Record,[string]$Path) {
    $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($temporary,($Record | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
        # Each receipt has one publication. No File.Replace(null), no overwriting
        # a running receipt and no silent replacement of another run's result.
        [IO.File]::Move($temporary,$Path)
    } finally {if(Test-Path -LiteralPath $temporary){[IO.File]::Delete($temporary)}}
}
function Invoke-MansurUpdateChild([string]$Executable,[string]$Arguments,[string]$OutputPath,[string]$ErrorPath,[int]$TimeoutMilliseconds=600000) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$Executable;$start.Arguments=$Arguments;$start.UseShellExecute=$false
    $start.CreateNoWindow=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process;$process.StartInfo=$start
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        if(-not $process.Start()){throw 'Update child did not start.'}
        $null=$process.Handle
        $result=[ordered]@{pid=$process.Id;session=$process.SessionId;path=$Executable
            started_utc=$process.StartTime.ToUniversalTime().ToString('o');exit_code=$null;reason=$null}
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $remaining=[Math]::Max(0,$TimeoutMilliseconds-[int]$clock.ElapsedMilliseconds)
        if(-not $process.WaitForExit($remaining)) {$result.reason='OBSERVATION_TIMEOUT'}
        else {
            $result.exit_code=$process.ExitCode
            $remaining=[Math]::Max(0,$TimeoutMilliseconds-[int]$clock.ElapsedMilliseconds)
            if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),$remaining)){$result.reason='OUTPUT_OBSERVATION_TIMEOUT'}
        }
        if($stdout.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion){[IO.File]::WriteAllText($OutputPath,$stdout.Result,[Text.UTF8Encoding]::new($false))}
        if($stderr.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion){[IO.File]::WriteAllText($ErrorPath,$stderr.Result,[Text.UTF8Encoding]::new($false))}
        return [pscustomobject]$result
    } finally {$clock.Stop();$process.Dispose()}
}

# The parent dispatches this wrapper once via ordinary current-user WMI. This
# script never relaunches itself, changes credentials or replays an update.
$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$identity=Get-MansurUpdateIdentity
if(-not [Environment]::Is64BitProcess -or $identity.sid -ne $ExpectedSid -or $identity.session -ne $ExpectedSession -or
   $identity.elevated -or $identity.package_result -ne 15700 -or -not $identity.path.Equals($shell,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Dispatch the updater in ordinary 64-bit Windows PowerShell, as the expected normal signed-in user/session.'
}
. (Join-Path $PSScriptRoot 'Installed-Integrity.ps1')
$source=Join-Path $workspace ('dist\MansurNext-'+$Version)
$null=Read-MansurFrozenManifest (Join-Path $source 'manifest.json') $Version $ExpectedManifestHash
$folder=Join-Path $workspace ('build\updates\local32-'+$RunId)
if(Test-Path -LiteralPath $folder){throw 'This update run was already claimed; inspect its receipt, do not replay.'}
[IO.Directory]::CreateDirectory($folder) | Out-Null
# CreateNew arbitrates even if two callers raced the directory existence check.
$claim=[IO.File]::Open((Join-Path $folder 'claimed'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
$claim.Dispose()
$receipt=[ordered]@{schema=1;run_id=$RunId;version=$Version;stage='running';manifest_sha256=$ExpectedManifestHash
    wrapper=$identity;child=$null;requested_utc=[DateTime]::UtcNow.ToString('o');finished_utc=$null;error_type=$null}
Publish-MansurUpdateReceipt $receipt (Join-Path $folder 'running.json')
$exitCode=10
try {
    # A separate -File process makes its internal exit terminate only the child.
    # Its real handle supplies the code; log text never establishes success.
    $child=Invoke-MansurUpdateChild $shell ('-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $source 'Update-Input.ps1')+'"') (Join-Path $folder 'stdout.log') (Join-Path $folder 'stderr.log')
    $receipt.child=$child
    if($child.reason -or $child.exit_code -isnot [int]) {$receipt.stage='incomplete'}
    elseif($child.exit_code -eq 0) {$receipt.stage='completed';$exitCode=0}
    else {$receipt.stage='failed';$exitCode=$child.exit_code}
} catch {$receipt.stage='incomplete';$receipt.error_type=$_.Exception.GetType().Name}
$receipt.finished_utc=[DateTime]::UtcNow.ToString('o')
Publish-MansurUpdateReceipt $receipt (Join-Path $folder 'result.json')
$receipt | ConvertTo-Json -Depth 8
exit $exitCode
