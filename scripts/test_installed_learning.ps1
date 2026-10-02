# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [ValidatePattern('^0\.1\.0-local\.[0-9]{1,4}$')][string]$Version='0.1.0-local.20',
    [switch]$RequireVisiblePopup)
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathRooted($EvidenceDirectory)) {throw 'Absolute evidence directory required.'}
[IO.Directory]::CreateDirectory($EvidenceDirectory)|Out-Null
$taskStatePath=Join-Path $env:USERPROFILE '.mansur-next\runtime-status.json'
function Read-FixedRunRuntime {
    # The broker replaces this file atomically; allow replacement during a read.
    for($taskReadAttempt=0;$taskReadAttempt -lt 10;$taskReadAttempt++) {
    try {
    $taskStream=[IO.FileStream]::new($taskStatePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $taskReader=[IO.StreamReader]::new($taskStream,[Text.Encoding]::UTF8)
        try {return ($taskReader.ReadToEnd()|ConvertFrom-Json)} finally {$taskReader.Dispose()}
    } finally {$taskStream.Dispose()}
    } catch {
        $taskReadError=$_.Exception
        while($taskReadError.InnerException){$taskReadError=$taskReadError.InnerException}
        if($taskReadError -isnot [IO.IOException] -or $taskReadAttempt -eq 9){throw}
        Start-Sleep -Milliseconds 50
    }
    }
}
$taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskInitial=Read-FixedRunRuntime
$taskProcess=Get-CimInstance Win32_Process -Filter ('ProcessId='+$taskInitial.pid)
$taskExpected=Join-Path $env:ProgramFiles ('MansurNext\'+$Version+'\bin\MansurNext.Desktop.exe')
if(-not $taskProcess -or $taskProcess.ExecutablePath -ine $taskExpected) {throw 'Expected installed backend identity is not confirmed.'}
$taskToken='fixed-run-'+[Guid]::NewGuid().ToString('N')
$taskMessage=@{op='learn';context=$taskToken;sender=$taskToken;sent_ticks=[Diagnostics.Stopwatch]::GetTimestamp();sent_sequence=1;revision=1;text='你好';voice='af_heart';speed=1.0}|ConvertTo-Json -Compress
$taskTimer=[Diagnostics.Stopwatch]::StartNew()
$taskPipe=[IO.Pipes.NamedPipeClientStream]::new('.','MansurNext.Learning.'+$taskSid,[IO.Pipes.PipeDirection]::InOut)
try {
    $taskPipe.Connect(2500)
    $taskBytes=[Text.Encoding]::UTF8.GetBytes($taskMessage+"`n")
    $taskPipe.Write($taskBytes,0,$taskBytes.Length);$taskPipe.Flush()
    $taskAckBytes=[byte[]]::new(1)
    $taskAck=$taskPipe.ReadAsync($taskAckBytes,0,1)
    if(-not $taskAck.Wait(2000) -or $taskAck.Result -ne 1 -or $taskAckBytes[0] -ne 6) {throw 'Installed IPC acknowledgement unavailable.'}
} finally {$taskPipe.Dispose()}
$taskAcceptedId=$taskInitial.request_id+1;$taskEnglishMs=$null;$taskFinalEnglishMs=$null;$taskAudioMs=$null;$taskDone=$false;$taskInterrupted=$false;$taskLast=$null;$taskShown=$false
while($taskTimer.Elapsed.TotalSeconds -lt 40) {
    $taskState=Read-FixedRunRuntime
    if($taskState.pid -ne $taskInitial.pid){$taskInterrupted=$true;break}
    if($taskState.request_id -gt $taskInitial.request_id) {
        if($taskState.request_id -ne $taskAcceptedId){$taskInterrupted=$true;break}
        $taskLast=$taskState
        if($taskState.english_characters -gt 0 -and $null -eq $taskEnglishMs){$taskEnglishMs=$taskTimer.ElapsedMilliseconds}
        # Audio is accepted only after the complete translation. Polling can miss
        # the short translation stage; later audio/done also establish completion.
        if($taskState.english_characters -gt 0 -and $null -eq $taskFinalEnglishMs -and
           $taskState.stage -in @('translation','audio_queued','system_voice','done')){$taskFinalEnglishMs=$taskTimer.ElapsedMilliseconds}
        if($taskState.audio_queued_bytes -gt 0 -and $null -eq $taskAudioMs){$taskAudioMs=$taskTimer.ElapsedMilliseconds}
        if($RequireVisiblePopup){$taskShown=$taskState.window_visible -and $taskState.window_english_visible -and $taskState.window_topmost -and $taskState.window_on_screen -and -not $taskState.window_cloaked}
        if($taskState.stage -eq 'done'){$taskDone=$true;if(-not $RequireVisiblePopup -or $taskShown){break}}
        if($taskState.stage -eq 'cancel' -or $taskState.stage -eq 'error'){break}
    }
    Start-Sleep -Milliseconds 75
}
$taskResult=[ordered]@{status=$(if($taskInterrupted){'INCONCLUSIVE_INTERLEAVED_REQUEST'}elseif($taskDone -and $taskLast.english_characters -gt 0 -and $taskLast.audio_queued_bytes -gt 0 -and (-not $RequireVisiblePopup -or $taskShown)){'PASS'}else{'FAIL'});
    installed_backend_pid=$taskInitial.pid;request_id=$taskAcceptedId;english_observed_ms=$taskEnglishMs;complete_english_observed_ms=$taskFinalEnglishMs;audio_queued_observed_ms=$taskAudioMs;
    stage=$(if($taskLast){$taskLast.stage}else{'not_observed'});english_characters=$(if($taskLast){$taskLast.english_characters}else{0});
    audio_bytes=$(if($taskLast){$taskLast.audio_queued_bytes}else{0});error_code=$(if($taskLast){$taskLast.error_code}else{$null});
    popup_check_required=[bool]$RequireVisiblePopup;popup_shown_on_screen=$taskShown;
    scope='Fixed 你好 through installed IPC/Broker/worker/audio queue; optional actual popup shown/topmost/on-screen/uncloaked state. English observed includes a first streaming fragment; complete English is separately observed or bounded by a later audio/done stage. No editor input or user app UI. Done is synthesis, not audible playback confirmation.'}
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'installed-live-chain.json'),($taskResult|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$taskResult|ConvertTo-Json -Compress
if($taskResult.status -eq 'FAIL'){throw 'Installed learning chain failed; see fixed test evidence.'}
