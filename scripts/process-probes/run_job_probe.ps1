# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output=Join-Path $root ('build\process-probes\'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($output) | Out-Null
$compiler=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if(-not (Test-Path -LiteralPath $compiler)){throw 'C# compiler not found.'}
$exe=Join-Path $output 'JobProbe.exe'
& $compiler /nologo /target:exe /platform:x64 /warn:4 /warnaserror+ ("/out:"+$exe) ("/reference:"+(Join-Path $framework 'System.Management.dll')) ("/reference:"+(Join-Path $framework 'System.Web.Extensions.dll')) (Join-Path $PSScriptRoot 'JobProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Probe compilation failed.'}
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=$exe
$start.Arguments='"'+(Join-Path $output 'result')+'"'
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$process=[Diagnostics.Process]::Start($start)
try {
    if(-not $process.WaitForExit(45000)){throw 'Probe coordinator timed out; test leaves self-expire within 20 seconds.'}
    if($process.ExitCode -ne 0){throw 'Probe reported failure.'}
} finally {$process.Dispose()}
Get-Content -LiteralPath (Join-Path $output 'result\result.json') -Raw -Encoding UTF8
