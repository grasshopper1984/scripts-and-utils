$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    # This metadata sits at the plugin root and points to the live AgentRoom installation.
    $adapterFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'adapter.json'
    $adapter = Get-Content -LiteralPath $adapterFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $executable = [string]$adapter.executable
    $agyHome = [string]$adapter.home
    if (-not [IO.Path]::IsPathRooted($executable) -or
        $executable.StartsWith('\\') -or
        -not [string]::Equals([IO.Path]::GetFullPath($executable), $executable, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'The configured AgentRoom executable is missing or invalid.'
    }
    if (-not [IO.Path]::IsPathRooted($agyHome) -or
        $agyHome.StartsWith('\\') -or
        -not [string]::Equals([IO.Path]::GetFullPath($agyHome), $agyHome, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $agyHome -PathType Container)) {
        throw 'The configured Antigravity home is missing or invalid.'
    }
    $payload = [Console]::In.ReadToEnd()
    $result = $payload | & $executable agy --agy-home $agyHome hook
    if ($LASTEXITCODE -ne 0) { throw 'The AgentRoom hook process failed.' }
    if ($null -ne $result) { $result }
}
catch {
    $message = 'AgentRoom context was not restored. Run room.exe agy setup from the current AgentRoom installation to repair its configured paths. Do not perform room work until the current briefing is restored.'
    @{
        injectSteps = @(
            @{
                ephemeralMessage = $message
            }
        )
    } | ConvertTo-Json -Depth 5 -Compress
}
