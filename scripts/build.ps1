# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
[CmdletBinding()]
param([ValidateSet('x64','Win32')][string]$Architecture='x64',[switch]$NoTests,[switch]$CoreOnly)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if(-not $vsPath){throw 'MSVC C++ build tools were not found.'}
$cmake=Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ctest=Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe'
$build=Join-Path $projectRoot "build\$Architecture"
$runner=Join-Path $PSScriptRoot 'run_clean_env.py'
$windowsModule=if($CoreOnly){'OFF'}else{'ON'}
& python $runner $cmake -S $projectRoot -B $build -G 'Visual Studio 17 2022' -A $Architecture "-DMANSUR_BUILD_WINDOWS=$windowsModule"
if($LASTEXITCODE -ne 0){throw 'CMake configure failed.'}
& python $runner $cmake --build $build --config Release --parallel 4
if($LASTEXITCODE -ne 0){throw 'Build failed.'}
if(-not $NoTests){
  & python $runner $ctest --test-dir $build -C Release --output-on-failure
  if($LASTEXITCODE -ne 0){throw 'Tests failed.'}
}
