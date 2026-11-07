[CmdletBinding()]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$framework = if ([Environment]::Is64BitOperatingSystem) { 'Framework64' } else { 'Framework' }
$compiler = Join-Path $env:WINDIR "Microsoft.NET\$framework\v4.0.30319\csc.exe"
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$options = @('/nologo', '/optimize+', '/warn:4', '/warnaserror+', '/platform:anycpu',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
    "/win32manifest:$PSScriptRoot\app.manifest", "/win32icon:$PSScriptRoot\app.ico",
    "/resource:$PSScriptRoot\app.ico,AgentRoom.AppIcon")
# Role playbooks and the protocol are embedded so join can brief an agent with no files beside the executables.
$options += @('moderator', 'coordinator', 'worker', 'observer') | ForEach-Object { "/resource:$PSScriptRoot\roles\$_.md,AgentRoom.Roles.$_.md" }
$options += "/resource:$PSScriptRoot\PROTOCOL.md,AgentRoom.Roles.protocol.md"
$options += "/resource:$PSScriptRoot\codex\skills\agent-room\SKILL.md,AgentRoom.CodexSkill"
$options += "/resource:$PSScriptRoot\codex\session-start.ps1,AgentRoom.CodexHook"
$options += "/resource:$PSScriptRoot\agy\skills\agent-room\SKILL.md,AgentRoom.AgySkill"
$options += "/resource:$PSScriptRoot\agy\pre-invocation.ps1,AgentRoom.AgyHook"
$options += "/resource:$PSScriptRoot\README.md,AgentRoom.Readme"
$sources = @('Json.cs','Database.cs','LocalFiles.cs','RoomService.cs','OperatorMigration.cs','ConnectionSessions.cs','Coordination.cs','Threads.cs','ResponseLimits.cs','ProjectData.cs','Waiting.cs','Transport.cs','CodexAdapter.cs','AgyAdapter.cs','GraphView.cs','DetailView.cs','Viewer.cs','ViewerActions.cs','Program.cs') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
# Builds to a temporary file, then swaps it in. Windows allows renaming a running executable but not overwriting it,
# so a copy still in use by the hub or an agent's connector is moved aside and keeps running until that process exits.
function Publish-Binary([string]$name, [string[]]$arguments, [string]$failure) {
    $target = Join-Path $output $name
    $staging = Join-Path $output 'staging'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $fresh = Join-Path $staging $name
    & $compiler "/out:$fresh" @arguments
    if ($LASTEXITCODE -ne 0) { Remove-Item -LiteralPath $fresh -ErrorAction SilentlyContinue; throw $failure }
    if (Test-Path -LiteralPath $target) {
        try { Remove-Item -LiteralPath $target -ErrorAction Stop }
        catch { Move-Item -LiteralPath $target -Destination ("$target.old-" + [DateTime]::Now.ToString('yyyyMMddHHmmssfff')) }
    }
    Move-Item -LiteralPath $fresh -Destination $target
}
# Copies moved aside by earlier builds are removed once nothing runs them.
Get-ChildItem -LiteralPath $output -Filter '*.old-*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
Publish-Binary 'AgentRoom.exe' ($options + '/target:winexe' + $sources) 'GUI build failed.'
Publish-Binary 'room.exe' ($options + '/target:exe' + $sources) 'Connector build failed.'
Get-Item -LiteralPath "$output\AgentRoom.exe", "$output\room.exe" | Select-Object Name,Length
if ($Test) {
    Publish-Binary 'RoomTests.exe' ($options + '/target:exe' + '/main:AgentRoom.Tests' + $sources + (Join-Path $PSScriptRoot 'Tests.cs') + (Join-Path $PSScriptRoot 'ProjectTests.cs') + (Join-Path $PSScriptRoot 'NamingTests.cs') + (Join-Path $PSScriptRoot 'ReliabilityTests.cs') + (Join-Path $PSScriptRoot 'ConnectionSessionTests.cs') + (Join-Path $PSScriptRoot 'CodexTests.cs') + (Join-Path $PSScriptRoot 'AgyTests.cs') + (Join-Path $PSScriptRoot 'ViewTests.cs')) 'Test build failed.'
    & "$output\RoomTests.exe" (Join-Path $PSScriptRoot 'test-output')
    if ($LASTEXITCODE -ne 0) { throw 'Room tests failed.' }
}
