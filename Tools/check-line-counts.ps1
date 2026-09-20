param(
    [string]$ProjectDir,
    [int]$WarningThreshold = 501,
    [int]$ErrorThreshold = 801
)

$exitCode = 0
$filesChecked = 0

if ([string]::IsNullOrWhiteSpace($ProjectDir)) {
    $ProjectDir = Split-Path -Parent $PSScriptRoot
}
$projectRoot = [IO.Path]::GetFullPath($ProjectDir).TrimEnd([char[]]'\/')
if (-not (Test-Path -LiteralPath $projectRoot -PathType Container)) {
    throw "Project directory does not exist: $projectRoot"
}

$sourceExtensions = @('.cs', '.cpp', '.cxx', '.cc', '.h', '.hpp', '.inc')

Get-ChildItem -LiteralPath $projectRoot -Recurse -File | Where-Object {
    $f = $_.FullName
    $sourceExtensions -contains $_.Extension.ToLowerInvariant() -and
    $f -notmatch '\\obj\\' -and
    $f -notmatch '\\bin\\' -and
    $f -notmatch '\\node_modules\\' -and
    $f -notmatch '\\.claude\\' -and
    $f -notmatch '\\.idvb-gpu\\' -and
    $f -notmatch '\\artifacts\\' -and
    $f -notmatch '\\.verify\\' -and
    $f -notmatch '\\Tools\\overlay_game\\build\\' -and
    $f -notmatch '\\third[-_]?party\\' -and
    $f -notmatch '\\\.playwright-mcp\\' -and
    $_.Name -notmatch '\.g\.cs$' -and
    $_.Name -notmatch '\.g\.i\.cs$'
} | ForEach-Object {
    # Array Count is the physical line count, including blank and comment lines.
    $lines = (Get-Content -LiteralPath $_.FullName).Count
    $filesChecked++
    $relativePath = $_.FullName.Substring($projectRoot.Length).TrimStart('\', '/')
    if ($lines -ge $ErrorThreshold) {
        Write-Host "${relativePath}($($lines)): error IDVB0001: [REMASTER-IMMEDIATE] $lines lines (limit: $ErrorThreshold). Split immediately."
        $exitCode = 1
    } elseif ($lines -ge $WarningThreshold) {
        Write-Host "${relativePath}($($lines)): warning IDVB0002: [REMASTER] $lines lines (limit: $WarningThreshold). Should be split."
    }
}

Write-Host "Checked $filesChecked first-party source files."
exit $exitCode
