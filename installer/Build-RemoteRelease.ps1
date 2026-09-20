[CmdletBinding()]
param(
    [string]$Version,
    [ValidatePattern('^b\d{2}\.\d+$')]
    [string]$ReleaseLine = 'b01.5',
    [string]$TargetCommit = 'origin/master',
    [switch]$Prerelease,
    [string]$GitHubRepository,
    [string]$ReleaseTitle,
    [string]$ReleaseNotes,
    [string]$ReleaseNotesPath,
    [switch]$PrepareOnly,
    [switch]$KeepSourceWorktree
)

$ErrorActionPreference = 'Stop'
$toolRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$releaseRoot = Join-Path $toolRoot 'artifacts\release'
$resolvedCommit = (git -C $toolRoot rev-parse --verify "$TargetCommit^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Cannot resolve release target '$TargetCommit'. Run git fetch origin first." }
$worktreeRoot = Join-Path $releaseRoot ('.source-' + $resolvedCommit.Substring(0, 12))

if (Test-Path -LiteralPath $worktreeRoot) {
    git -C $toolRoot worktree remove --force $worktreeRoot
    if ($LASTEXITCODE -ne 0) { throw "Cannot remove old source worktree: $worktreeRoot" }
}

try {
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    git -C $toolRoot worktree add --detach $worktreeRoot $resolvedCommit
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the source worktree.' }

    Copy-Item -LiteralPath (Join-Path $toolRoot 'Assets') -Destination (Join-Path $worktreeRoot 'Assets') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $toolRoot 'LICENSE') -Destination (Join-Path $worktreeRoot 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $toolRoot 'Infrastructure\Configuration') `
        -Destination (Join-Path $worktreeRoot 'Infrastructure') -Recurse -Force
    $worktreeInstaller = Join-Path $worktreeRoot 'installer'
    New-Item -ItemType Directory -Path $worktreeInstaller -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReleaseVersion.psm1') `
        -Destination (Join-Path $worktreeInstaller 'ReleaseVersion.psm1') -Force

    $requiredSourceFiles = @(
        'IDVBuff.csproj',
        'Assets\Gate.png',
        'Infrastructure\Configuration\default.toml',
        'Infrastructure\Configuration\survey.toml',
        'LICENSE',
        'installer\ReleaseVersion.psm1',
        'Updater\IDVBuff.Updater.csproj',
        'release\trust\idvb-update-2026-01.pem'
    )
    foreach ($relativePath in $requiredSourceFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $worktreeRoot $relativePath))) {
            throw "Prepared source worktree is missing required file: $relativePath"
        }
    }
    if ($PrepareOnly) {
        Get-Item -LiteralPath $worktreeRoot | Select-Object FullName, LastWriteTime
        return
    }

    $buildParameters = @{
        SourceRoot = $worktreeRoot
        ReleaseOutputRoot = $releaseRoot
        TargetCommit = $resolvedCommit
        ReleaseLine = $ReleaseLine
        PublishGitHubRelease = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($Version)) { $buildParameters.Version = $Version }
    if ($Prerelease) { $buildParameters.Prerelease = $true }
    if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) { $buildParameters.GitHubRepository = $GitHubRepository }
    if (-not [string]::IsNullOrWhiteSpace($ReleaseTitle)) { $buildParameters.ReleaseTitle = $ReleaseTitle }
    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) { $buildParameters.ReleaseNotes = $ReleaseNotes }
    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) { $buildParameters.ReleaseNotesPath = $ReleaseNotesPath }
    & (Join-Path $PSScriptRoot 'Build-Release.ps1') @buildParameters
}
finally {
    if (-not $KeepSourceWorktree -and (Test-Path -LiteralPath $worktreeRoot)) {
        git -C $toolRoot worktree remove --force $worktreeRoot
    }
}
