# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([switch]$SkipSelfTest)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $projectRoot 'desktop'
$outputRoot = Join-Path $projectRoot 'build\desktop'
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $locator)) { throw 'Visual Studio C# compiler is unavailable.' }
    $installation = & $locator -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
    $compiler = Join-Path $installation 'MSBuild\Current\Bin\Roslyn\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Roslyn csc.exe is unavailable.' }
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$assemblies = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'Accessibility.dll')
foreach ($assembly in $assemblies) {
    if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot $assembly))) { throw "Missing .NET Framework assembly: $assembly" }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$executable = Join-Path $outputRoot 'MansurNext.Desktop.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/warnaserror+', '/langversion:7.3', '/utf8output', '/noconfig', '/nostdlib+', "/out:$executable", "/win32manifest:$(Join-Path $sourceRoot 'app.manifest')")
$productIcon = Join-Path $sourceRoot 'iconassets\product\Mansur.ico'
if (-not (Test-Path -LiteralPath $productIcon -PathType Leaf)) { throw 'Missing product icon.' }
$arguments += ('/win32icon:' + $productIcon)
$arguments += ('/resource:' + $productIcon + ',MansurNext.Product.Icon')
$arguments += ('/resource:' + (Join-Path $sourceRoot 'iconassets\product\brand.png') + ',MansurNext.Product.Brand')
foreach ($legal in @('LICENSE', 'COPYRIGHT.md', 'NOTICE', 'THIRD_PARTY_NOTICES.md')) {
    $arguments += ('/resource:' + (Join-Path $projectRoot $legal) + ',MansurNext.Legal.' + $legal)
}
$arguments += $assemblies | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$arguments += ('/reference:' + (Join-Path $frameworkRoot 'WPF\System.Speech.dll'))
$iconRoot = Join-Path $sourceRoot 'iconassets\lucide'
foreach ($icon in @('rotate-ccw', 'settings', 'x', 'grip-vertical', 'keyboard', 'palette', 'volume-2', 'book-open', 'cpu', 'copy')) {
    foreach ($pixels in @(20, 25, 30, 40, 60, 80)) {
        $name = "$icon-$pixels.png"
        $asset = Join-Path $iconRoot ('png\' + $name)
        if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Missing official toolbar icon: $name" }
        $arguments += "/resource:$asset,MansurNext.Icons.$name"
    }
}
$arguments += ('/resource:' + (Join-Path $iconRoot 'LICENSE') + ',MansurNext.Icons.LICENSE')
$serviceIcons = Join-Path $sourceRoot 'iconassets\services'
$arguments += ('/resource:' + (Join-Path $serviceIcons 'LICENSE') + ',MansurNext.Services.LICENSE')
foreach ($brand in @('openai', 'openrouter', 'deepseek', 'siliconcloud', 'qwen', 'zhipu', 'kimi', 'doubao', 'minimax', 'tencentcloud', 'hunyuan', 'baidu', 'stepfun', 'spark')) {
    foreach ($theme in @('light', 'dark')) {
        $asset = Join-Path $serviceIcons ($brand + '-' + $theme + '.png')
        if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw 'Missing service brand icon.' }
        $arguments += ('/resource:' + $asset + ',MansurNext.Services.' + $brand + '-' + $theme + '.png')
    }
}
$arguments += Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'app.config') -Destination ($executable + '.config') -Force
if (-not $SkipSelfTest) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $executable
    $start.Arguments = '--self-test'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $test = New-Object Diagnostics.Process
    $test.StartInfo = $start
    [void]$test.Start()
    $readOutput = $test.StandardOutput.ReadToEndAsync()
    $readError = $test.StandardError.ReadToEndAsync()
    if (-not $test.WaitForExit(30000)) {
        $test.Kill()
        $test.Dispose()
        throw 'Desktop self-test timed out.'
    }
    $stdout = $readOutput.GetAwaiter().GetResult()
    $stderr = $readError.GetAwaiter().GetResult()
    $exitCode = $test.ExitCode
    $test.Dispose()
    if ($exitCode -ne 0) { throw "Desktop self-test failed: $stderr" }
    [IO.File]::WriteAllText((Join-Path $outputRoot 'self-test.json'), $stdout, (New-Object Text.UTF8Encoding($false)))
    Write-Output $stdout.Trim()
}
Get-Item -LiteralPath $executable | Select-Object FullName, Length
