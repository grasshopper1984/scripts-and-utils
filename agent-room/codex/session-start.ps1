$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    # This metadata is copied into the plugin cache, but points to the live installation.
    $adapterFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'adapter.json'
    $adapter = Get-Content -LiteralPath $adapterFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $executable = [string]$adapter.executable
    if (-not [IO.Path]::IsPathRooted($executable) -or
        $executable.StartsWith('\\') -or
        -not [string]::Equals([IO.Path]::GetFullPath($executable), $executable, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'The configured AgentRoom executable is missing or invalid.'
    }
    $payload = [Console]::In.ReadToEnd()
    $result = $payload | & $executable codex hook
    if ($LASTEXITCODE -ne 0) { throw 'The AgentRoom hook process failed.' }
    if ($null -ne $result) { $result }
}
catch {
    $message = 'AgentRoom context was not restored. Run room.exe codex setup from the current AgentRoom installation to repair its executable path, then review the updated hook in Codex. Do not perform room work until the current briefing is restored.'
    @{
        systemMessage = $message
        hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = $message }
    } | ConvertTo-Json -Depth 4 -Compress
}
