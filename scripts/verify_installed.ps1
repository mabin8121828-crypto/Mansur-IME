# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [string]$Version='0.1.0-local.7',
    [switch]$Probe,
    [switch]$Internal,
    [string]$RunId,
    [string]$ExpectedManifestHash,
    [string]$ExpectedSid,
    [int]$ExpectedSession=-1,
    [string]$NotBeforeUtc
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
if($Version -notmatch '^0\.1\.0-local\.[0-9]+$'){throw 'Invalid version.'}
if(-not [Environment]::Is64BitProcess){Write-Error 'INCOMPLETE: run the verifier using 64-bit PowerShell.' -ErrorAction Continue;exit 10}
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$referenceManifest=Join-Path $workspace ('dist\MansurNext-'+$Version+'\manifest.json')
$installed=Join-Path $env:ProgramFiles ('MansurNext\'+$Version)
$powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
. (Join-Path $PSScriptRoot 'Installed-Integrity.ps1')

function Convert-VerificationUtc([object]$Value) {
    if($Value -is [DateTimeOffset]){return $Value.UtcDateTime}
    if($Value -is [DateTime]){return $Value.ToUniversalTime()}
    return [DateTimeOffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).UtcDateTime
}
function Get-VerificationIdentity {
    if(-not ('MansurVerification.Package' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MansurVerification {
    public static class Package {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
        public static int Query() { uint length=0; return GetCurrentPackageFullName(ref length, IntPtr.Zero); }
    }
}
'@
    }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $process=[Diagnostics.Process]::GetCurrentProcess()
    try {
        return [pscustomobject]@{
            pid=$process.Id;session=$process.SessionId;sid=$identity.User.Value
            path=$process.MainModule.FileName;started_utc=$process.StartTime.ToUniversalTime().ToString('o')
            package_result=[MansurVerification.Package]::Query()
            elevated=(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        }
    } finally {$process.Dispose();$identity.Dispose()}
}
function Publish-VerificationResult([object]$Record,[string]$Destination) {
    $temporary=$Destination+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($temporary,($Record | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
        # Publishing only after the handle closes avoids partial JSON and cleanup races.
        [IO.File]::Move($temporary,$Destination)
    } finally {if(Test-Path -LiteralPath $temporary){[IO.File]::Delete($temporary)}}
}
function Invoke-MansurVerifierProcess([string]$Executable,[string]$Arguments,[string]$OutputPath,[string]$ErrorPath,[int]$TimeoutMilliseconds=30000) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$Executable;$start.Arguments=$Arguments
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process
    $process.StartInfo=$start
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        if(-not $process.Start()){throw 'Verifier process did not start.'}
        # Keep the handle created by Process.Start, even if a fast helper exits
        # immediately. Do not reacquire a process by a PID that may be reused.
        $null=$process.Handle
        $result=[ordered]@{pid=$process.Id;exit_code=$null;reason=$null}
        # Drain both streams concurrently before waiting: either pipe may fill.
        $stdout=$process.StandardOutput.ReadToEndAsync()
        $stderr=$process.StandardError.ReadToEndAsync()
        $remaining=[Math]::Max(0,$TimeoutMilliseconds-[int]$clock.ElapsedMilliseconds)
        if(-not $process.WaitForExit($remaining)) {
            $result.reason='OBSERVATION_TIMEOUT'
        } else {
            $result.exit_code=$process.ExitCode
            $remaining=[Math]::Max(0,$TimeoutMilliseconds-[int]$clock.ElapsedMilliseconds)
            if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),$remaining)) {
                $result.reason='OUTPUT_OBSERVATION_TIMEOUT'
            }
        }
        # A timeout is unknown completion. It never kills or replays a process.
        if($stdout.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) {
            [IO.File]::WriteAllText($OutputPath,$stdout.Result,[Text.UTF8Encoding]::new($false))
        }
        if($stderr.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) {
            [IO.File]::WriteAllText($ErrorPath,$stderr.Result,[Text.UTF8Encoding]::new($false))
        }
        return [pscustomobject]$result
    } finally {$clock.Stop();$process.Dispose()}
}

if($Internal) {
    if($RunId -cnotmatch '^[a-f0-9]{32}$' -or $ExpectedManifestHash -notmatch '^[a-fA-F0-9]{64}$' -or
       $ExpectedSid -notmatch '^S-1-[0-9-]+$' -or $ExpectedSession -lt 0){throw 'Invalid internal verification request.'}
    $evidence=Join-Path $workspace ('build\verification\'+$Version+'\'+$RunId)
    $resultPath=Join-Path $evidence 'result.json'
    if(-not (Test-Path -LiteralPath $evidence -PathType Container) -or (Test-Path -LiteralPath $resultPath)){throw 'Missing or already completed verification request.'}
    $identity=$null
    $checks=New-Object 'Collections.Generic.List[object]'
    $stage='environment';$status='INCOMPLETE';$errorType=$null;$files=0
    function Run-Check([string]$Name,[string]$Executable,[string]$Arguments) {
        $script:stage=$Name
        $script:status='INCOMPLETE'
        # These are our disposable console probes. Never close a user application,
        # and do not replay an activation probe whose completion is unknown.
        $process=Invoke-MansurVerifierProcess $Executable $Arguments (Join-Path $evidence ($Name+'.stdout.log')) (Join-Path $evidence ($Name+'.stderr.log'))
        $record=[ordered]@{name=$Name;pid=$process.pid;status='INCOMPLETE';exit_code=$process.exit_code;reason=$process.reason}
        if($process.reason) {
            $checks.Add([pscustomobject]$record)
            throw 'Own verification process did not finish within the observation period; no replay or termination was attempted.'
        }
        $classification=Get-MansurVerificationExitStatus $process.exit_code
        if($classification -eq 'INCOMPLETE') {
            $record.reason=$(if($process.exit_code -eq 10){'PRECONDITION'}else{'EXIT_STATUS_UNAVAILABLE'})
            $checks.Add([pscustomobject]$record)
            throw 'Verification prerequisite was not available; this does not establish installation damage.'
        }
        if($classification -eq 'FAILED') {
            $record.status='FAILED';$record.reason='CHECK_FAILED';$script:status='FAILED'
            $checks.Add([pscustomobject]$record)
            throw 'Verification check failed; see its fixed diagnostic log.'
        }
        $record.status='PASS'
        $checks.Add([pscustomobject]$record)
    }
    try {
        $identity=Get-VerificationIdentity
        # APPMODEL_ERROR_NO_PACKAGE = 15700. A SID match alone cannot prove the
        # unvirtualized view; check the actual WMI child's package identity too.
        if($identity.package_result -ne 15700 -or $identity.elevated -or $identity.sid -ne $ExpectedSid -or
           $identity.session -ne $ExpectedSession -or -not $identity.path.Equals($powershell,[StringComparison]::OrdinalIgnoreCase) -or
           (Convert-VerificationUtc $identity.started_utc) -lt (Convert-VerificationUtc $NotBeforeUtc).AddSeconds(-2)) {
            throw 'Could not establish an ordinary, non-elevated verifier in the requested user/session.'
        }
        $stage='frozen-manifest';$status='FAILED'
        $manifest=Read-MansurFrozenManifest $referenceManifest $Version $ExpectedManifestHash
        if($Probe) {
            # This branch cannot inspect or activate an installed input method.
            $status='PROBE_PASS';$stage='environment-probe-complete'
        } else {
            $stage='installed-integrity'
            $integrity=Assert-MansurInstalledIntegrity $installed $referenceManifest $Version $ExpectedManifestHash
            $files=$integrity.files
            foreach($pair in @(@('x64','x64'),@('x86','Win32'))) {
                $dll=Join-Path $installed ('bin\'+$pair[0]+'\mansur_next_tsf.dll')
                Run-Check ('registration-'+$pair[0]) (Join-Path $installed ('bin\'+$pair[0]+'\mansur_register.exe')) ('--verify "'+$dll+'"')
                # flags=0 confines activation/mode recovery to this fresh process's
                # own TSF thread. The helper restores that thread's language/profile.
                Run-Check ('lifecycle-'+$pair[0]) (Join-Path $workspace ('build\'+$pair[1]+'\Release\mansur_tsf_activation_host.exe')) ('"'+$dll+'" --mode-reset')
            }
            $stage='complete';$status='PASS'
        }
    } catch {$errorType=$_.Exception.GetType().Name}
    $result=[ordered]@{schema=2;status=$status;stage=$stage;error_type=$errorType;run_id=$RunId;version=$Version
        probe=[bool]$Probe;manifest_sha256=$ExpectedManifestHash.ToUpperInvariant();manifest_files=$files
        identity=$identity;checked_utc=[DateTime]::UtcNow.ToString('o');checks=@($checks.ToArray())
        lifecycle_cycles_per_architecture=$(if($status -eq 'PASS'){60}else{0})
        scope='ordinary Windows process; frozen-package integrity and own-thread TSF checks only; no typing or audio acceptance'}
    Publish-VerificationResult $result $resultPath
    if($status -in @('PASS','PROBE_PASS')){exit 0}
    if($status -eq 'INCOMPLETE'){exit 10}
    exit 1
}

# The caller may itself be packaged. It launches exactly one ordinary WMI child,
# then accepts only that PID's fresh result for this nonce and frozen hash.
try {$caller=Get-VerificationIdentity} catch {
    Write-Error 'INCOMPLETE: the calling user/session identity could not be established.' -ErrorAction Continue
    exit 10
}
if($caller.elevated){Write-Error 'INCOMPLETE: run verification as the normal signed-in user.' -ErrorAction Continue;exit 10}
$ExpectedSid=$caller.sid;$ExpectedSession=$caller.session
$ExpectedManifestHash=(Get-FileHash -LiteralPath $referenceManifest -Algorithm SHA256).Hash
$frozenManifest=Read-MansurFrozenManifest $referenceManifest $Version $ExpectedManifestHash
$RunId=[Guid]::NewGuid().ToString('N')
$evidence=Join-Path $workspace ('build\verification\'+$Version+'\'+$RunId)
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$resultPath=Join-Path $evidence 'result.json'
$scriptPath=[IO.Path]::GetFullPath($PSCommandPath)
if($scriptPath.Contains('"')){throw 'Invalid verification script path.'}
$started=[DateTime]::UtcNow
$NotBeforeUtc=$started.ToString('o')
$arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$scriptPath+'" -Internal -Version '+$Version+
    ' -RunId '+$RunId+' -ExpectedManifestHash '+$ExpectedManifestHash+' -ExpectedSid '+$ExpectedSid+
    ' -ExpectedSession '+$ExpectedSession+' -NotBeforeUtc "'+$NotBeforeUtc+'"'
if($Probe){$arguments+=' -Probe'}
try {
    $startup=New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ShowWindow=[uint16]0}
    $created=Invoke-CimMethod -ClassName Win32_Process -MethodName Create -OperationTimeoutSec 15 -Arguments @{
        CommandLine=('"'+$powershell+'" '+$arguments);CurrentDirectory=$workspace;ProcessStartupInformation=$startup}
    if($created.ReturnValue -ne 0 -or -not $created.ProcessId){throw 'WMI did not confirm verifier creation.'}
} catch {
    Publish-VerificationResult ([ordered]@{schema=2;status='INCOMPLETE';stage='launch';run_id=$RunId;version=$Version;probe=[bool]$Probe;error_type=$_.Exception.GetType().Name;checked_utc=[DateTime]::UtcNow.ToString('o')}) (Join-Path $evidence 'observation.json')
    Write-Error ('INCOMPLETE: ordinary verifier launch could not be confirmed; no operation was repeated. Evidence: '+$evidence) -ErrorAction Continue
    exit 10
}
$childProcessId=[int]$created.ProcessId
Publish-VerificationResult ([ordered]@{schema=2;run_id=$RunId;version=$Version;pid=$childProcessId;sid=$ExpectedSid
    session=$ExpectedSession;path=$powershell;manifest_sha256=$ExpectedManifestHash;requested_utc=$NotBeforeUtc;probe=[bool]$Probe}) (Join-Path $evidence 'launch.json')
$deadline=$started.AddSeconds(150)
do {
    if(Test-Path -LiteralPath $resultPath -PathType Leaf) {
        try {
            if((Get-Item -LiteralPath $resultPath).Length -gt 65536){throw 'Oversized verification result.'}
            $result=Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if($result.schema -ne 2 -or $result.run_id -cne $RunId -or $result.version -cne $Version -or
               $result.probe -ne [bool]$Probe -or $result.manifest_sha256 -ne $ExpectedManifestHash -or
               $result.identity.pid -ne $childProcessId -or $result.identity.sid -ne $ExpectedSid -or
               $result.identity.session -ne $ExpectedSession -or $result.identity.package_result -ne 15700 -or $result.identity.elevated -ne $false -or
               -not ([string]$result.identity.path).Equals($powershell,[StringComparison]::OrdinalIgnoreCase) -or
               (Convert-VerificationUtc $result.identity.started_utc) -lt $started.AddSeconds(-2) -or
               (Convert-VerificationUtc $result.checked_utc) -lt $started -or
               (Convert-VerificationUtc $result.checked_utc) -gt [DateTime]::UtcNow.AddSeconds(5)) {
                throw 'Verifier receipt identity or freshness did not match its request.'
            }
            Assert-MansurVerificationReceiptShape $result ([bool]$Probe) @($frozenManifest.files).Count
        } catch {
            Write-Error ('INCOMPLETE: verifier environment or receipt could not be verified. No replay; evidence: '+$evidence) -ErrorAction Continue
            exit 10
        }
        $result | ConvertTo-Json -Depth 10
        if(($Probe -and $result.status -eq 'PROBE_PASS') -or (-not $Probe -and $result.status -eq 'PASS')){exit 0}
        if($result.status -eq 'INCOMPLETE'){exit 10}
        Write-Error ('Verification did not pass; evidence: '+$evidence) -ErrorAction Continue
        exit 1
    }
    Start-Sleep -Milliseconds 200
} while([DateTime]::UtcNow -lt $deadline)
Publish-VerificationResult ([ordered]@{schema=2;status='INCOMPLETE';stage='observation-timeout';run_id=$RunId;version=$Version;probe=[bool]$Probe
    pid=$childProcessId;checked_utc=[DateTime]::UtcNow.ToString('o');completion='unknown'}) (Join-Path $evidence 'observation.json')
Write-Error ('INCOMPLETE: verifier completion is unknown. No replay or termination; PID '+$childProcessId+'; evidence: '+$evidence) -ErrorAction Continue
exit 10
