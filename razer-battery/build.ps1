[CmdletBinding()]
param([switch]$Test, [switch]$Probe)

$ErrorActionPreference = 'Stop'
$framework = if ([Environment]::Is64BitOperatingSystem) { 'Framework64' } else { 'Framework' }
$compiler = Join-Path $env:WINDIR "Microsoft.NET\$framework\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler is required.' }

$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$common = @('/nologo', '/optimize+', '/warn:4', '/warnaserror+', '/platform:anycpu',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', "/win32manifest:$PSScriptRoot\app.manifest")
$sources = @('NativeHid.cs', 'Battery.cs', 'Widget.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$exe = Join-Path $output 'RazerBattery.exe'
& $compiler @common /target:winexe "/out:$exe" @sources (Join-Path $PSScriptRoot 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Widget compilation failed. Exit the running widget before rebuilding.' }
Write-Host ("Built RazerBattery.exe ({0:N0} bytes)" -f (Get-Item -LiteralPath $exe).Length)

if ($Test) {
    $testExe = Join-Path $output 'BatteryTests.exe'
    & $compiler @common /target:exe "/out:$testExe" @sources (Join-Path $PSScriptRoot 'Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testExe (Join-Path $PSScriptRoot 'test-output')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if ($Probe) {
    $probeExe = Join-Path $output 'BatteryProbe.exe'
    & $compiler @common /target:exe "/out:$probeExe" (Join-Path $PSScriptRoot 'NativeHid.cs') `
        (Join-Path $PSScriptRoot 'Battery.cs') (Join-Path $PSScriptRoot 'Probe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed.' }
    & $probeExe
    if ($LASTEXITCODE -ne 0) { throw 'No live battery reading. See the probe output and README.' }
}
