# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
# Shared, current-user startup helpers. Dot-sourcing this file changes no state.
# WMI's local process provider starts the desktop outside the caller's Job;
# no elevation, remote connection, scheduled task or shell injection is used.
function New-MansurSettingsShortcut([string]$Executable) {
    $exe=[IO.Path]::GetFullPath($Executable)
    $productRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'MansurNext'))
    if (-not $exe.StartsWith($productRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($exe) -ne 'MansurNext.Desktop.exe' -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'Invalid settings shortcut target.'
    }
    $desktop=[Environment]::GetFolderPath('DesktopDirectory')
    if (-not $desktop -or -not (Test-Path -LiteralPath $desktop -PathType Container)) {throw 'Desktop folder is unavailable.'}
    $shell=New-Object -ComObject WScript.Shell
    try {
        $path=$null
        foreach($name in @('Mansur 输入法设置.lnk','Mansur 输入法设置（新版）.lnk')) {
            $candidate=Join-Path $desktop $name
            if (-not (Test-Path -LiteralPath $candidate)) {$path=$candidate;break}
            $old=$shell.CreateShortcut($candidate)
            if ($old.TargetPath.StartsWith($productRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($old.TargetPath) -eq 'MansurNext.Desktop.exe' -and $old.Arguments -eq '--show-settings') {
                $path=$candidate;break
            }
        }
        if (-not $path) {throw 'Settings shortcut name is occupied by another target.'}
        $link=$shell.CreateShortcut($path)
        $link.TargetPath=$exe;$link.Arguments='--show-settings';$link.WorkingDirectory=[IO.Path]::GetDirectoryName($exe)
        $icon=Join-Path $link.WorkingDirectory 'Mansur.ico'
        $link.IconLocation=if(Test-Path -LiteralPath $icon -PathType Leaf){$icon+',0'}else{$exe+',0'}
        $link.Description='打开 Mansur 输入法设置';$link.WindowStyle=1;$link.Save()
        $verified=$shell.CreateShortcut($path)
        if ($verified.TargetPath -ne $exe -or $verified.Arguments -ne '--show-settings' -or $verified.IconLocation -ne $link.IconLocation) {
            throw 'Settings shortcut verification failed.'
        }
        return $path
    } finally {[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null}
}

function Convert-MansurTimestampUtc([object]$Value) {
    # PowerShell 7 JSON may already deserialize ISO dates, while Windows
    # PowerShell 5.1 keeps strings. Do not round-trip through locale formatting.
    if($Value -is [DateTimeOffset]){return $Value.UtcDateTime}
    if($Value -is [DateTime]){return $Value.ToUniversalTime()}
    return [DateTimeOffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).UtcDateTime
}

function Invoke-MansurStartupMaintenance([string]$Executable,[ValidateSet('inspect','refresh','remove')][string]$Operation,[string]$TargetExecutable) {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if((New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Manage login startup as the normal signed-in user.'
    }
    $exe=[IO.Path]::GetFullPath($Executable)
    if([IO.Path]::GetFileName($exe) -ne 'MansurNext.Desktop.exe' -or $exe.Contains('"') -or -not (Test-Path -LiteralPath $exe -PathType Leaf)){throw 'Invalid startup maintenance executable.'}
    if($Operation -eq 'refresh') {
        $TargetExecutable=[IO.Path]::GetFullPath($TargetExecutable)
        if($TargetExecutable.Contains('"') -or -not (Test-Path -LiteralPath $TargetExecutable -PathType Leaf)){throw 'Invalid login startup target.'}
    }
    $shared=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.mansur-next'
    [IO.Directory]::CreateDirectory($shared) | Out-Null
    $resultPath=Join-Path $shared ('startup-operation-'+[Guid]::NewGuid().ToString('N')+'.json')
    $arguments='--maintain-startup '+$Operation
    if($Operation -eq 'refresh'){$arguments+=' "'+$TargetExecutable+'"'}
    $arguments+=' "'+$resultPath+'"'
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $started=[DateTime]::UtcNow
    $startup=New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ShowWindow=[uint16]0}
    $created=Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine=('"'+$exe+'" '+$arguments)
        CurrentDirectory=[IO.Path]::GetDirectoryName($exe)
        ProcessStartupInformation=$startup
    }
    if($created.ReturnValue -ne 0 -or -not $created.ProcessId){throw 'Could not start login startup maintenance.'}
    $childProcessId=[int]$created.ProcessId
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    $complete=$false
    try {
        do {
            $record=$null
            try {
                if(Test-Path -LiteralPath $resultPath -PathType Leaf) {
                    if((Get-Item -LiteralPath $resultPath).Length -gt 8192){throw 'Oversized startup maintenance result.'}
                    $record=Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
                }
            } catch { $record=$null } # A partial write is not completion.
            if($record) {
                if($record.schema -ne 1 -or $record.pid -ne $childProcessId -or $record.session -ne $session -or
                   $record.sid -ne $identity.User.Value -or $record.action -ne $Operation -or
                   -not ([string]$record.executable).Equals($exe,[StringComparison]::OrdinalIgnoreCase) -or
                   (Convert-MansurTimestampUtc $record.updated_utc) -lt $started) {
                    throw 'Startup maintenance result did not match its request.'
                }
                $complete=$true
                if($record.success -ne $true){throw ('Login startup was not changed: '+[string]$record.message)}
                return $record
            }
            Start-Sleep -Milliseconds 100
        } while([DateTime]::UtcNow -lt $deadline)
        # An observation timeout is not proof the helper stopped. Do not replay
        # a mutation or terminate an unpinned PID; leave the unique result path.
        throw ('Login startup maintenance did not finish in time; inspect helper PID '+$childProcessId+'. No second operation was started.')
    } finally {
        if($complete -and (Test-Path -LiteralPath $resultPath)){
            try {[IO.File]::Delete($resultPath)} catch { } # Receipt cleanup cannot turn an acknowledged operation into failure.
        }
    }
}

function Assert-MansurCompanionVersion([string]$Executable) {
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach($existing in @(Get-Process -Name 'MansurNext.Desktop' -ErrorAction SilentlyContinue)) {
        if($existing.SessionId -ne $session){continue}
        $metadata=Get-CimInstance Win32_Process -Filter ('ProcessId='+$existing.Id)
        if(-not $metadata){continue}
        $owner=Invoke-CimMethod -InputObject $metadata -MethodName GetOwnerSid
        if($owner.ReturnValue -eq 0 -and $owner.Sid -eq $sid -and
           -not $existing.Path.Equals($Executable,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Another companion version is running. Use the update helper; no configuration was changed.'
        }
    }
}

function Initialize-MansurUserState([string]$Executable,[string]$ConfigurationPath,[switch]$AllowIncomplete) {
    $shared=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.mansur-next'
    [IO.Directory]::CreateDirectory($shared) | Out-Null
    $snapshot=Join-Path $shared ('migration-preferences-'+[Guid]::NewGuid().ToString('N')+'.json')
    $preferences=@{}
    $legacy=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\MansurNext\Settings')
    try {
        if($legacy) {
            foreach($name in @('Theme','CandidateLayout','FontSize','Abbreviation','FuzzyMask','ToolbarVisible','Voice','SpeedPercent','ToolbarX','ToolbarY')) {
                $value=$legacy.GetValue($name,$null)
                if($null -ne $value){$preferences[$name]=$value}
            }
        }
    } finally {if($legacy){$legacy.Dispose()}}
    [IO.File]::WriteAllText($snapshot,($preferences | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $process=$null
    try {
        if($Executable.Contains('"') -or $ConfigurationPath.Contains('"')){throw 'Invalid migration paths.'}
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=$Executable
        $prepareMode=if($AllowIncomplete){'--prepare-settings'}else{'--prepare-user'}
        $start.Arguments=$prepareMode+' "'+$ConfigurationPath+'" "'+$snapshot+'"'
        $start.UseShellExecute=$false
        $start.CreateNoWindow=$true
        $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput=$true
        $start.RedirectStandardError=$true
        $process=[Diagnostics.Process]::Start($start)
        if(-not $process.WaitForExit(10000)){$process.Kill();throw 'User state migration timed out; previous files were retained.'}
        if($process.ExitCode -ne 0){throw 'User state migration failed; previous files were retained.'}
        # The migration emits only a fixed success marker and our shared path.
        $result=$process.StandardOutput.ReadToEnd() | ConvertFrom-Json
        if($result.status -ne 'USER_STATE_READY' -or
           -not ([string]$result.configuration_path).Equals((Join-Path $shared 'local-models.json'),[StringComparison]::OrdinalIgnoreCase)) {
            throw 'User state migration did not confirm completion.'
        }
    } finally {
        if($process){$process.Dispose()}
        if(Test-Path -LiteralPath $snapshot){[IO.File]::Delete($snapshot)}
    }
}

function Save-MansurConfiguration([object]$Configuration,[string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    $folder=[IO.Path]::GetDirectoryName($full)
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    $temporary=Join-Path $folder ('local-models.'+[Guid]::NewGuid().ToString('N')+'.tmp')
    $backup=$null
    try {
        [IO.File]::WriteAllText($temporary,($Configuration | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
        if(Test-Path -LiteralPath $full) {
            $backup=Join-Path $folder ('local-models.before-update.'+[Guid]::NewGuid().ToString('N')+'.json')
            [IO.File]::Replace($temporary,$full,$backup)
        } else { [IO.File]::Move($temporary,$full) }
    } finally {
        if(Test-Path -LiteralPath $temporary){[IO.File]::Delete($temporary)}
    }
    return $backup
}

function Start-MansurCompanion([string]$Executable,[string]$ConfigurationPath,[int]$ReadyTimeoutSeconds=30) {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if((New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Start the companion as the normal signed-in user, not as administrator.'
    }
    $exe=[IO.Path]::GetFullPath($Executable)
    $config=[IO.Path]::GetFullPath($ConfigurationPath)
    if([IO.Path]::GetFileName($exe) -ne 'MansurNext.Desktop.exe' -or $exe.Contains('"') -or $config.Contains('"') -or
       -not (Test-Path -LiteralPath $exe -PathType Leaf) -or -not (Test-Path -LiteralPath $config -PathType Leaf)) {
        throw 'Invalid companion startup paths.'
    }
    $sid=$identity.User.Value
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $process=$null
    foreach($existing in @(Get-Process -Name 'MansurNext.Desktop' -ErrorAction SilentlyContinue)) {
        if($existing.SessionId -ne $session){continue}
        $metadata=Get-CimInstance Win32_Process -Filter ('ProcessId='+$existing.Id)
        if(-not $metadata){continue}
        $owner=Invoke-CimMethod -InputObject $metadata -MethodName GetOwnerSid
        if($owner.ReturnValue -ne 0 -or $owner.Sid -ne $sid){continue}
        if(-not $existing.Path.Equals($exe,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Another companion version is running. Use the update or restart helper.'
        }
        $process=$existing
        break
    }
    if(-not $process) {
        $startup=New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ShowWindow=[uint16]0}
        $created=Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
            CommandLine=('"'+$exe+'" --config "'+$config+'"')
            CurrentDirectory=[IO.Path]::GetDirectoryName($exe)
            ProcessStartupInformation=$startup
        }
        if($created.ReturnValue -ne 0 -or -not $created.ProcessId){throw ('Independent companion startup failed: '+$created.ReturnValue)}
        $process=[Diagnostics.Process]::GetProcessById([int]$created.ProcessId)
    }
    try {
        $null=$process.Handle # Pin identity while validating; do not rely on a reusable PID alone.
        $started=$process.StartTime.ToUniversalTime()
        if($process.SessionId -ne $session -or -not $process.Path.Equals($exe,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Companion started in an unexpected session or path.'
        }
        $metadata=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.Id)
        if(-not $metadata){throw 'Companion exited before ownership could be checked.'}
        $owner=Invoke-CimMethod -InputObject $metadata -MethodName GetOwnerSid
        if($owner.ReturnValue -ne 0 -or $owner.Sid -ne $sid){throw 'Companion owner did not match the signed-in user.'}
        $deadline=[DateTime]::UtcNow.AddSeconds([Math]::Max(2,[Math]::Min(45,$ReadyTimeoutSeconds)))
        $state='preparing'
        $statusPath=Join-Path ([IO.Path]::GetDirectoryName($config)) 'runtime-status.json'
        do {
            if($process.WaitForExit(250)){throw 'The companion exited during startup. The previous configuration backup is retained.'}
            try {
                if(Test-Path -LiteralPath $statusPath) {
                    $status=Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
                    if($status.pid -eq $process.Id -and (Convert-MansurTimestampUtc $status.updated_utc) -ge $started) {
                        if($status.model_ready -eq $true){$state='ready';break}
                        if($status.stage -eq 'error'){$state='unavailable';break}
                    }
                }
            } catch { } # Stale/partially replaced diagnostics cannot invalidate the process itself.
        } while([DateTime]::UtcNow -lt $deadline)
        if($process.HasExited){throw 'The companion exited before startup verification completed.'}
        return [pscustomobject]@{Id=$process.Id;State=$state;StartedUtc=$started.ToString('o');Path=$exe}
    } finally {$process.Dispose()}
}
