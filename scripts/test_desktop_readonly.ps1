# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$CompanionPath)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskVerification=[IO.Path]::GetFullPath((Join-Path $taskRoot 'build\verification'))
$taskOutput=[IO.Path]::GetFullPath((Join-Path $taskVerification ('desktop-readonly-'+[Guid]::NewGuid().ToString('N'))))
if(-not $taskOutput.StartsWith($taskVerification+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Fixture path escaped verification workspace.'}
$taskInstall=Join-Path $taskOutput 'readonly-bin'
[IO.Directory]::CreateDirectory($taskInstall)|Out-Null
$taskExe=Join-Path $taskInstall 'MansurNext.Desktop.exe'
Copy-Item -LiteralPath $CompanionPath -Destination $taskExe
Copy-Item -LiteralPath ($CompanionPath+'.config') -Destination ($taskExe+'.config')
$taskOriginalAcl=Get-Acl -LiteralPath $taskInstall
$taskAcl=Get-Acl -LiteralPath $taskInstall
$taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$taskRule=[Security.AccessControl.FileSystemAccessRule]::new($taskSid,[Security.AccessControl.FileSystemRights]::Write,
    [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit,
    [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Deny)
$taskAcl.AddAccessRule($taskRule)
$taskProcess=$null
try {
    Set-Acl -LiteralPath $taskInstall -AclObject $taskAcl
    $taskWriteBlocked=$false
    try {[IO.File]::WriteAllText((Join-Path $taskInstall 'write-probe'),'fixed')}catch [UnauthorizedAccessException] {$taskWriteBlocked=$true}
    if(-not $taskWriteBlocked){throw 'The fixture directory is still writable; no readonly claim made.'}
    $taskStart=[Diagnostics.ProcessStartInfo]::new()
    $taskStart.FileName=$taskExe;$taskStart.Arguments='--self-test';$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true
    $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
    $taskStart.StandardOutputEncoding=[Text.Encoding]::UTF8;$taskStart.StandardErrorEncoding=[Text.Encoding]::UTF8
    $taskProcess=[Diagnostics.Process]::Start($taskStart)
    $taskOut=$taskProcess.StandardOutput.ReadToEndAsync();$taskErr=$taskProcess.StandardError.ReadToEndAsync()
    if(-not $taskProcess.WaitForExit(30000)){$taskProcess.Kill();throw 'Own readonly fixture timed out.'}
    $taskCode=$taskProcess.ExitCode
    $taskStdout=$taskOut.GetAwaiter().GetResult();$taskStderr=$taskErr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $taskOutput 'stdout.json'),$taskStdout,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $taskOutput 'stderr.json'),$taskStderr,[Text.UTF8Encoding]::new($false))
    $taskSummary=[ordered]@{status=$(if($taskCode -eq 0){'PASS'}else{'FAIL'});actual_exit=$taskCode;install_directory_write_denied=$taskWriteBlocked;
        companion_sha256=(Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash;scope='Own temporary readonly executable copy, fixed headless fixtures, no installed files or user data changed.'}
    [IO.File]::WriteAllText((Join-Path $taskOutput 'result.json'),($taskSummary|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $taskSummary|ConvertTo-Json -Compress
    if($taskCode -ne 0){throw ('Readonly desktop self-test failed; evidence='+$taskOutput)}
    Write-Output ('DESKTOP_READONLY_PASS evidence='+$taskOutput)
} finally {
    # Restore only the checked, unique fixture directory's original ACL.
    Set-Acl -LiteralPath $taskInstall -AclObject $taskOriginalAcl
    if($taskProcess){$taskProcess.Dispose()}
}
