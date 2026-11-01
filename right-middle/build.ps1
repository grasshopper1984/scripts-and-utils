[CmdletBinding()]
param([switch]$Test)
$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with Desktop development with C++.' }
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'MSVC C++ tools were not found.' }
$toolVersion = (Get-Content -LiteralPath (Join-Path $vsRoot 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$vc = Join-Path $vsRoot "VC\Tools\MSVC\$toolVersion"
$kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$kitVersion = Get-ChildItem -LiteralPath (Join-Path $kitRoot 'Include') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
if (-not $kitVersion) { throw 'A Windows 10/11 SDK was not found.' }
$compiler = Join-Path $vc 'bin\Hostx64\x64\cl.exe'
$previousInclude = $env:INCLUDE
$previousLib = $env:LIB
try {
    $env:INCLUDE = @((Join-Path $vc 'include'), (Join-Path $kitRoot "Include\$kitVersion\ucrt"),
        (Join-Path $kitRoot "Include\$kitVersion\shared"), (Join-Path $kitRoot "Include\$kitVersion\um")) -join ';'
    $env:LIB = @((Join-Path $vc 'lib\x64'), (Join-Path $kitRoot "Lib\$kitVersion\ucrt\x64"),
        (Join-Path $kitRoot "Lib\$kitVersion\um\x64")) -join ';'
    $output = Join-Path $PSScriptRoot 'bin'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $options = @('/nologo', '/std:c++17', '/O2', '/MT', '/EHsc', '/W4', '/WX', '/utf-8',
        '/DUNICODE', '/D_UNICODE', '/guard:cf')
    & $compiler @options (Join-Path $PSScriptRoot 'main.cpp') "/Fo$output\main.obj" "/Fe$output\RightMiddle.exe" `
        /link /SUBSYSTEM:WINDOWS '/MANIFESTUAC:level=asInvoker uiAccess=false' /DYNAMICBASE /NXCOMPAT `
        user32.lib shell32.lib wtsapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Exit Right Middle before rebuilding.' }
    Write-Host ("Built RightMiddle.exe ({0:N0} bytes)" -f (Get-Item -LiteralPath (Join-Path $output 'RightMiddle.exe')).Length)
    if ($Test) {
        & $compiler @options (Join-Path $PSScriptRoot 'tests.cpp') "/Fo$output\tests.obj" "/Fe$output\Tests.exe" `
            /link /SUBSYSTEM:CONSOLE /DYNAMICBASE /NXCOMPAT user32.lib shell32.lib wtsapi32.lib
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
        & (Join-Path $output 'Tests.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
} finally {
    $env:INCLUDE = $previousInclude
    $env:LIB = $previousLib
}
