[CmdletBinding()]
param(
    [string]$CommitMessage = ("sync: update developer-a " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$targetBranch = 'developer-a'
$expectedOrigin = 'https://github.com/xigefuli-dev/IDVB-Private.git'

function Assert-Success([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Get-StagedPaths {
    $paths = @(git -C $repositoryRoot diff --cached --name-only)
    Assert-Success 'Inspect staged files'
    return $paths
}

function Test-ForbiddenPath([string]$Path) {
    $normalized = $Path.Replace('\', '/').TrimStart('/')
    return $normalized -match '(?i)(^|/)(\.secrets|\.wrangler|\.claude|\.codex|\.vs|\.temp-settings[^/]*|_artifact_work|scratch|artifacts|bin|obj|buildverify|TestResults|node_modules|Logs)(/|$)' -or
        $normalized -match '(?i)(^|/)(Assets\.zip|_shot_conv\.jpg)$' -or
        $normalized -match '(?i)\.(dll|exe|pdb|trx|log|tmp)$'
}

if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git') -PathType Container)) {
    throw "The script must run inside the IDVB Git working tree: $repositoryRoot"
}

$currentBranch = (git -C $repositoryRoot branch --show-current).Trim()
Assert-Success 'Read current branch'
if ($currentBranch -ne $targetBranch) {
    throw "Refusing to publish from '$currentBranch'. Switch to '$targetBranch' first, so main and integration cannot be pushed accidentally."
}

$originUrl = (git -C $repositoryRoot remote get-url origin).Trim()
Assert-Success 'Read origin remote'
if ($originUrl -ne $expectedOrigin) {
    throw "Refusing to push to unexpected origin '$originUrl'. Expected '$expectedOrigin'."
}

git -C $repositoryRoot add --all
Assert-Success 'Stage project changes'

$stagedPaths = @(Get-StagedPaths)
foreach ($path in $stagedPaths) {
    if (Test-ForbiddenPath $path) {
        throw "Refusing to commit a private, generated, or local-only path: $path"
    }
}

if ($stagedPaths.Count -gt 0) {
    git -C $repositoryRoot diff --cached --check
    Assert-Success 'Validate staged whitespace'

    Write-Host '=== Committing to developer-a ==='
    $stagedPaths | Sort-Object | ForEach-Object { Write-Host $_ }
    git -C $repositoryRoot commit -m $CommitMessage
    Assert-Success 'Create developer-a commit'
}
else {
    Write-Host 'No local changes to commit; pushing the existing developer-a branch.'
}

git -C $repositoryRoot push origin "HEAD:refs/heads/$targetBranch"
Assert-Success "Push $targetBranch to origin"

$localCommit = (git -C $repositoryRoot rev-parse HEAD).Trim()
Assert-Success 'Resolve local developer-a commit'
git -C $repositoryRoot fetch origin $targetBranch
Assert-Success 'Fetch remote developer-a branch'
$remoteCommit = (git -C $repositoryRoot rev-parse "origin/$targetBranch").Trim()
Assert-Success 'Resolve remote developer-a commit'
if ($localCommit -ne $remoteCommit) {
    throw "Push verification failed: local $localCommit, remote $remoteCommit."
}

Write-Host "developer-a is now published at $localCommit"
