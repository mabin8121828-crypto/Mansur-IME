# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([string]$OutputPath)
$ErrorActionPreference='Stop'
# Read-only fixed-field diagnostics. No titles, input text, clipboard, keystrokes,
# focus changes, process termination or settings writes.
if(-not ('MansurHealth.Endpoints' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
namespace MansurHealth {
 public sealed class BackendIdentity {
  public uint Pid,Session; public string Path,Sid,StartedUtc; public bool Alive;
 }
 public static class Processes {
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint process);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
  [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
  [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint process,out uint session);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
  public static BackendIdentity Read(uint pid) {
   IntPtr process=OpenProcess(0x1000,false,pid);if(process==IntPtr.Zero)return null;
   IntPtr token=IntPtr.Zero;
   try {
    long created,exited,kernel,user;uint session,size=32768;var path=new StringBuilder((int)size);
    if(!GetProcessTimes(process,out created,out exited,out kernel,out user)||
       !QueryFullProcessImageName(process,0,path,ref size)||!ProcessIdToSessionId(pid,out session)||
       !OpenProcessToken(process,0x0008,out token))return null;
    using(var identity=new WindowsIdentity(token)) {
     return new BackendIdentity {Pid=pid,Session=session,Path=path.ToString(),Sid=identity.User.Value,
      StartedUtc=DateTime.FromFileTimeUtc(created).ToString("o"),Alive=exited==0};
    }
   }catch {return null;}
   finally {if(token!=IntPtr.Zero)CloseHandle(token);CloseHandle(process);}
  }
 }
 public sealed class Entry {
  public int Pid; public int Thread; public bool Reachable;
  public Dictionary<string,uint> Fields=new Dictionary<string,uint>();
 }
 public static class Endpoints {
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string title);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string value);
  [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,UIntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
  public static Entry[] Read() {
   var result=new List<Entry>();IntPtr previous=IntPtr.Zero;
   uint message=RegisterWindowMessage("Mansur.Next.Mode.Diagnostic.v1");
    string[] names={"protocol","state_bits","activations","contexts","reclaimed_empty_contexts","context_pressure","metadata_failures","metadata_stage","request_hr","session_hr","property_hr","fallback","mode_get_hr","mode_set_hr","component_revision","focus_epoch","mode_changes","personal_words_state","personal_words_pending","personal_words_error","personal_words_accepted","personal_words_saved","personal_words_rejected","personal_words_skipped_new","writeback_lease_bits","writeback_invalidation_reason","writeback_guard","writeback_notification_stage","writeback_notification_hr","writeback_own_notifications"};
   for(int count=0;count<64;++count) {
    IntPtr window=FindWindowEx(new IntPtr(-3),previous,"Mansur.Next.Mode.v1",null);
    if(window==IntPtr.Zero)break;previous=window;
    uint process;uint thread=GetWindowThreadProcessId(window,out process);
    var entry=new Entry {Pid=(int)process,Thread=(int)thread,Reachable=true};
    for(uint field=0;field<names.Length;++field) {
      if(field>=17 && entry.Fields["component_revision"]<11)break;
      if(field>=24 && entry.Fields["component_revision"]<17)break;
      if(field>=27 && entry.Fields["component_revision"]<18)break;
     UIntPtr value;
     if(SendMessageTimeout(window,message,new UIntPtr(field),IntPtr.Zero,0x0002|0x0020,50,out value)==IntPtr.Zero){entry.Reachable=false;break;}
     entry.Fields[names[field]]=unchecked((uint)value.ToUInt64());
     if(field==0&&value.ToUInt64()!=1){entry.Reachable=false;break;}
    }
    result.Add(entry);
   }
   return result.ToArray();
  }
 }
}
'@
}
function Convert-MansurHealthTimestampUtc([object]$Value) {
    # PS7 may deserialize JSON dates before classification; PS5.1 keeps ISO
    # strings. Never convert a date object back through locale formatting.
    if($Value -is [DateTimeOffset]){return $Value.UtcDateTime}
    if($Value -is [DateTime]){return $Value.ToUniversalTime()}
    return [DateTimeOffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).UtcDateTime
}
function Resolve-MansurRuntimeState([object[]]$Backends,[hashtable]$Runtime,[string]$Source,[datetime]$CollectedUtc,[bool]$EnumerationSucceeded) {
    # Pure classification: a state file is evidence only after it is bound to a
    # verified current-user process, not proof that a missing process exited.
    $live=@($Backends | Where-Object {$_.identity_verified -and $_.alive})
    $result=@{readiness='unknown';readiness_reason='state_missing';backend_alive=$null;
        process_alive=$false;effective_ready=$false;status_matches_live_process=$false;
        runtime_view_reliable=($Source -eq 'shared-user-directory');process_enumeration_succeeded=$EnumerationSucceeded}
    if($live.Count){$result.backend_alive=$true}
    if(-not $EnumerationSucceeded){$result.readiness_reason='process_enumeration_failed';return $result}
    if(-not $live.Count){$result.readiness_reason='verified_backend_not_observed';return $result}
    if($Runtime.ContainsKey('read_error')){$result.readiness_reason='state_unreadable';return $result}
    if(-not $Runtime.ContainsKey('pid') -or -not $Runtime.ContainsKey('updated_utc')){return $result}
    $matching=@($live | Where-Object {$_.pid -eq $Runtime.pid})
    if($matching.Count -ne 1){$result.readiness_reason='state_pid_mismatch';return $result}
    try {
        $written=Convert-MansurHealthTimestampUtc $Runtime.updated_utc
        $started=Convert-MansurHealthTimestampUtc $matching[0].started_utc
        if($written -lt $started -or $written -gt $CollectedUtc.ToUniversalTime().AddSeconds(5)) {
            $result.readiness_reason='state_time_mismatch';return $result
        }
    }catch {$result.readiness_reason='state_time_invalid';return $result}
    $result.process_alive=$true
    $result.status_matches_live_process=$true
    if(($Runtime.ContainsKey('stage') -and $Runtime.stage -eq 'exit') -or
       ($Runtime.ContainsKey('worker_state') -and $Runtime.worker_state -eq 'stopped')) {
        $result.readiness_reason='state_exit_conflicts_with_live_process';return $result
    }
    if(-not $Runtime.ContainsKey('model_ready') -or $Runtime.model_ready -isnot [bool]) {
        $result.readiness_reason='ready_flag_missing_or_invalid';return $result
    }
    $result.effective_ready=$Runtime.model_ready
    $result.readiness=if($Runtime.model_ready){'ready'}else{'not_ready'}
    $result.readiness_reason='matched_process_last_report'
    return $result
}
$profiles=@()
foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
    try {
        $key=$base.OpenSubKey('Software\Classes\CLSID\{E17225F9-B37A-4A39-A6FA-6EC6971CB481}\InprocServer32')
        try { $profiles+=@{architecture=[string]$view;module=if($key){[string]$key.GetValue('')}else{''}} }
        finally {if($key){$key.Dispose()}}
    }finally{$base.Dispose()}
}
$settings=@{}
$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\MansurNext\Settings')
try {
    if($key){foreach($name in @('ChineseMode','Theme','CandidateLayout','FontSize','Abbreviation','FuzzyMask','ToolbarVisible','SpeedPercent','LexiconRevision')){$settings[$name]=$key.GetValue($name,$null)}}
}finally{if($key){$key.Dispose()}}
$sharedRoot=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.mansur-next'
$sharedSettings=@{}
$preferencesPath=Join-Path $sharedRoot 'preferences.ini'
if(Test-Path -LiteralPath $preferencesPath) {
    foreach($line in [IO.File]::ReadAllLines($preferencesPath,[Text.Encoding]::UTF8)) {
        $pair=$line.Split([char[]]@('='),2)
        if($pair.Count -eq 2 -and $pair[0] -in @('Theme','CandidateLayout','FontSize','Abbreviation','FuzzyMask','ToolbarVisible','SpeedPercent','LexiconRevision')) {
            $number=0
            if([int]::TryParse($pair[1],[ref]$number)){$sharedSettings[$pair[0]]=$number}
        }
    }
}
$session=[Diagnostics.Process]::GetCurrentProcess().SessionId
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$productRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'MansurNext')).TrimEnd('\')
$hosts=@()
$backends=@()
$enumerationSucceeded=$true
try {$processes=@(Get-Process -ErrorAction Stop)}catch {$processes=@();$enumerationSucceeded=$false}
foreach($process in $processes) {
    if($process.ProcessName -eq 'MansurNext.Desktop') {
        $identity=[MansurHealth.Processes]::Read([uint32]$process.Id)
        if($identity -and $identity.Session -eq $session) {
            $expectedRoot=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($identity.Path))
            $ownPath=([IO.Path]::GetDirectoryName($expectedRoot) -eq $productRoot -and
                [IO.Path]::GetFileName($expectedRoot) -match '^0\.1\.0-local\.[0-9]+$' -and
                $identity.Path.Equals((Join-Path $expectedRoot 'bin\MansurNext.Desktop.exe'),[StringComparison]::OrdinalIgnoreCase))
            $backends+=@{pid=$identity.Pid;session=$identity.Session;path=$identity.Path;started_utc=$identity.StartedUtc;
                same_user=($identity.Sid -eq $sid);installed_path=$ownPath;identity_verified=($identity.Sid -eq $sid -and $ownPath);alive=$identity.Alive}
        }elseif(-not $identity) {
            $backends+=@{pid=$process.Id;identity_verified=$false;alive=$null;identity_read_error=$true}
        }
    }
    try {
        if($process.SessionId -ne $session){continue}
        $paths=@($process.Modules | Where-Object {$_.ModuleName -eq 'mansur_next_tsf.dll'} | ForEach-Object {$_.FileName})
        if($paths.Count -or $process.ProcessName -eq 'MansurNext.Desktop') {
            $hosts+=@{name=$process.ProcessName;pid=$process.Id;started_utc=$process.StartTime.ToUniversalTime().ToString('o');input_modules=$paths}
        }
    }catch { }
}
$runtime=@{}
$status=Join-Path $sharedRoot 'runtime-status.json'
$runtimeSource='shared-user-directory'
if(-not (Test-Path -LiteralPath $status)) {
    $status=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MansurNext\runtime-status.json'
    $runtimeSource='legacy-view-may-be-virtualized'
}
if(Test-Path -LiteralPath $status) {
    try {
        $raw=Get-Content -LiteralPath $status -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach($name in @('format_version','pid','updated_utc','stage','model_ready','voice_ready','translation_state','device','error_code','request_id','english_characters','audio_queued_packets','audio_queued_bytes','worker_epoch','worker_state','automatic_restarts')) {
            if($raw.PSObject.Properties[$name]){$runtime[$name]=$raw.$name}
        }
    }catch {$runtime=@{read_error=$true}}
}
$collected=[DateTime]::UtcNow
$classification=Resolve-MansurRuntimeState $backends $runtime $runtimeSource $collected $enumerationSucceeded
foreach($entry in $classification.GetEnumerator()){$runtime[$entry.Key]=$entry.Value}
$result=[ordered]@{schema=2;collected_utc=$collected.ToString('o');session=$session;registered=$profiles;legacy_settings_view=$settings;shared_settings=$sharedSettings;loaded=$hosts;backend_processes=$backends;endpoints=@([MansurHealth.Endpoints]::Read());runtime_source=$runtimeSource;runtime=$runtime;scope='read-only fixed metadata; no input text, window titles or clipboard'}
$json=$result | ConvertTo-Json -Depth 8
if($OutputPath) {
    $target=[IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::WriteAllText($target,$json,[Text.UTF8Encoding]::new($false))
}
$json
