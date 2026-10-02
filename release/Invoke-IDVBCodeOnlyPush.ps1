[CmdletBinding()]
param(
    # External GitHub publication is always explicit. Without this switch the
    # script only prints the exact code-only change set.
    [switch]$Publish,

    [string]$CommitMessage = 'sync: publish IDVB code-only source',
    [string]$TargetBranch = 'master',
    [switch]$PreserveSourceWorktree,
    [switch]$KeepSnapshot,
    [string]$ExpectedTree
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')

function Assert-Success([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Invoke-FetchOrigin([string]$Operation) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & git -C $repositoryRoot fetch origin
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0) {
            return
        }
        if ($attempt -lt 3) {
            Write-Warning "$Operation failed with exit code $exitCode on attempt $attempt; retrying."
            Start-Sleep -Seconds 2
        }
    }
    throw "$Operation failed with exit code $exitCode after 3 attempts."
}

function Test-PublicCodeOnlyPath([string]$Path) {
    $normalized = $Path.Replace('\', '/').TrimStart('/')
    if ($normalized -match '^release/trust/[A-Za-z0-9._-]+\.pem$') {
        return $true
    }
    if ($normalized -match '^Assets/Guide/[A-Za-z0-9._-]+\.png$') {
        return $true
    }
    if ($normalized -match '^(release|installer|docs|Tools|\.verify|web_installer|\.claude|\.temp-settings)(/|$)') {
        return $false
    }
    if ($normalized -match '^(IDVBuff\.Tests|IDVB\.PluginSystem\.Tests|IDVBuff\.PluginContracts\.Tests|IDVB\.PluginTestHost)(/|$)') {
        return $false
    }
    if ($normalized -match '(^|/)\.git(/|$)') {
        return $false
    }
    if ($normalized -in @('Startup_IDVB.cmd', 'Startup_RealCLI.cmd', 'Startup_overlay_game.cmd')) {
        return $true
    }
    if ($normalized -in @(
        'README.md',
        'LICENSE',
        'Assets/Icons/IDVB_icon_square_master.png'
    )) {
        return $true
    }
    if ($normalized -match '(^|/)(Package\.appxmanifest|app\.manifest)$') {
        return $true
    }
    return $normalized -match '\.(cs|xaml|csproj|slnx)$'
}

function Get-WorktreeStatusPaths {
    $status = @(git -C $repositoryRoot status --porcelain --untracked-files=all)
    Assert-Success 'Inspect worktree before source commit'
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $status) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) {
            continue
        }
        $pathText = $line.Substring(3).Trim()
        if ($pathText -match '^(.*) -> (.*)$') {
            $paths.Add($Matches[1])
            $paths.Add($Matches[2])
        }
        else {
            $paths.Add($pathText)
        }
    }
    return @($paths)
}

function Assert-SafeLocalChanges {
    foreach ($path in Get-WorktreeStatusPaths) {
        $normalized = $path.Replace('\', '/').TrimStart('/')
        if (($normalized -match '(^|/)(artifacts|\.secrets|\.wrangler|bin|obj)(/|$)') -or ($normalized -match '\.(pdb|nupkg|zip|exe|log|tmp|bak)$')) {
            throw "Refusing to commit a generated, secret, cache, or temporary path: $path"
        }
    }
}

function Invoke-LocalCommit {
    $paths = @(Get-WorktreeStatusPaths)
    if ($paths.Count -eq 0) {
        return
    }

    Assert-SafeLocalChanges
    Write-Host '=== Local source changes ==='
    $paths | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
    $answer = Read-Host 'Commit these local source files before publishing the code-only snapshot? [Y/N]'
    if ($answer -notmatch '^(?i)y(es)?$') {
        throw 'The user declined the local source commit.'
    }

    $null = git -C $repositoryRoot add --all
    Assert-Success 'Stage local source changes'
    $staged = @(git -C $repositoryRoot diff --cached --name-status)
    Assert-Success 'Inspect local source commit files'
    if ($staged.Count -eq 0) {
        throw 'Worktree reported changes, but no files are staged.'
    }

    Write-Host '=== Local source commit files ==='
    $staged | ForEach-Object { Write-Host $_ }
    $null = git -C $repositoryRoot commit -m $CommitMessage
    Assert-Success 'Commit local source changes'
}

function Get-CodeOnlyWorkingTreePaths {
    $paths = @(git -C $repositoryRoot ls-files --cached --others --exclude-standard)
    Assert-Success 'Enumerate current source files for code-only publication'
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $paths) {
        if (-not (Test-PublicCodeOnlyPath $path)) {
            continue
        }
        $fullPath = Join-Path $repositoryRoot ($path.Replace('/', '\'))
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $result.Add($path.Replace('\', '/'))
        }
    }
    return @($result | Sort-Object -Unique)
}

function Copy-CodeOnlySnapshot([string]$SnapshotRoot, [string]$BaseCommit) {
    $sourcePaths = @(Get-CodeOnlyWorkingTreePaths)
    $basePaths = @(git -C $repositoryRoot ls-tree -r --name-only $BaseCommit)
    Assert-Success "Enumerate all public source files at $BaseCommit"
    $sourceSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $sourcePaths) {
        [void]$sourceSet.Add($path)
        $source = Join-Path $repositoryRoot ($path.Replace('/', '\'))
        $destination = Join-Path $SnapshotRoot ($path.Replace('/', '\'))
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    foreach ($path in $basePaths) {
        if ($sourceSet.Contains($path)) {
            continue
        }
        $destination = Join-Path $SnapshotRoot ($path.Replace('/', '\'))
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            [IO.File]::Delete($destination)
        }
    }
}

& git check-ref-format --branch $TargetBranch | Out-Null
Assert-Success 'Validate publication branch'
if ($TargetBranch.StartsWith('-')) { throw 'Invalid publication branch.' }
$targetRef = if ($TargetBranch -eq 'master') { 'refs/heads/master' } else { "refs/heads/$TargetBranch" }

if ($Publish -and -not $PreserveSourceWorktree) {
    Invoke-LocalCommit
}

Invoke-FetchOrigin 'Fetch origin before code-only source publication'
$remoteRef = "origin/$TargetBranch"
$remoteHeads = @(git -C $repositoryRoot ls-remote --heads origin $targetRef)
Assert-Success 'Inspect publication branch on origin'
$baseRef = if ($remoteHeads.Count -gt 0) { $remoteRef } else { 'origin/master' }
$baseCommit = (git -C $repositoryRoot rev-parse --verify "$baseRef^{commit}").Trim()
Assert-Success "Resolve $baseRef before code-only source publication"
if ($remoteHeads.Count -gt 0 -and $remoteHeads[0].Split()[0] -ne $baseCommit) {
    throw 'Remote target changed after fetch. Review and retry.'
}
Write-Host "Target: $remoteRef; base: $baseCommit"

$snapshotRoot = Join-Path ([IO.Path]::GetTempPath()) ('IDVB-code-only-' + [Guid]::NewGuid().ToString('N'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $snapshotRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create a code-only worktree outside the temporary directory: $snapshotRoot"
}

$worktreeAdded = $false
try {
    $null = git -C $repositoryRoot worktree add --detach $snapshotRoot $baseCommit
    Assert-Success 'Create temporary code-only source worktree'
    $worktreeAdded = $true
    Copy-CodeOnlySnapshot $snapshotRoot $baseCommit

    $null = git -C $snapshotRoot add --all
    Assert-Success 'Stage code-only source snapshot'
    $sourcePaths = @(Get-CodeOnlyWorkingTreePaths)
    for ($offset = 0; $offset -lt $sourcePaths.Count; $offset += 50) {
        $last = [Math]::Min($offset + 49, $sourcePaths.Count - 1)
        $null = git -C $snapshotRoot add --force -- $sourcePaths[$offset..$last]
        Assert-Success 'Stage code-only source paths'
    }

    $snapshotTree = (git -C $snapshotRoot write-tree).Trim()
    Assert-Success 'Resolve code-only snapshot tree'
    Write-Host "Snapshot: $snapshotRoot"
    Write-Host "Snapshot tree: $snapshotTree"
    if ($ExpectedTree -and $snapshotTree -ne $ExpectedTree) {
        throw "Source changed after review: expected $ExpectedTree, found $snapshotTree."
    }

    $changed = @(git -C $snapshotRoot diff --cached --name-status)
    Assert-Success 'Inspect code-only source commit files'
    if ($changed.Count -eq 0) {
        Write-Host "Public code-only source already matches $baseCommit."
        return
    }

    foreach ($entry in $changed) {
        $changeType = if ($entry.Length -gt 0) { $entry.Substring(0, 1) } else { '' }
        $path = if ($entry.Length -gt 1) { $entry.Substring(1).Trim() } else { $entry }
        if ($path -match '^(.*) -> (.*)$') { $path = $Matches[2] }
        if ($changeType -ne 'D' -and -not (Test-PublicCodeOnlyPath $path)) {
            throw "Code-only staging contains a forbidden path: $path"
        }
    }

    Write-Host '=== Code-only public commit files ==='
    $changed | ForEach-Object { Write-Host $_ }
    if (-not $Publish) {
        Write-Host 'Preview complete. Re-run with -Publish to commit and push this exact code-only snapshot.'
        return
    }

    $answer = Read-Host "Commit this code-only source snapshot and push it to ${remoteRef}? [Y/N]"
    if ($answer -notmatch '^(?i)y(es)?$') {
        throw 'The user declined the code-only source commit and push.'
    }

    $null = git -C $snapshotRoot commit -m $CommitMessage
    Assert-Success 'Commit code-only source snapshot'
    $publicCommit = (git -C $snapshotRoot rev-parse --verify HEAD).Trim()
    Assert-Success 'Resolve code-only source commit'

    $null = git -C $snapshotRoot push origin "$($publicCommit):$targetRef"
    Assert-Success "Push code-only source commit to $remoteRef"
    Invoke-FetchOrigin 'Refresh origin after code-only source push'
    $publishedCommit = (git -C $repositoryRoot rev-parse --verify "$remoteRef^{commit}").Trim()
    if ($publishedCommit -ne $publicCommit) {
        throw "$remoteRef resolved to $publishedCommit after push, expected $publicCommit."
    }
    Write-Host "Published code-only source commit: $publicCommit"
}
finally {
    if ($KeepSnapshot) {
        Write-Host "Preserved snapshot for audit: $snapshotRoot"
    }
    elseif ($worktreeAdded -and (Test-Path -LiteralPath $snapshotRoot)) {
        $null = git -C $repositoryRoot worktree remove --force $snapshotRoot
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $snapshotRoot)) {
            [IO.Directory]::Delete($snapshotRoot, $true)
        }
    }
}

