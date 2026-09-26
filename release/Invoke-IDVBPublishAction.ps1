[CmdletBinding()]
param(
    [string]$PrivateKeyPath = '.\.secrets\idvb-update-2026-01-private.pem',
    [string]$R2Bucket = 'idvb-installer',
    [string]$GitHubRepository = 'xigefuli-dev/IDVB',
    [switch]$MigrationBaseline,
    [string[]]$Targets,
    [string]$ProductVersion,
    [string[]]$ReleaseNotesLines
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$releaseRunner = Join-Path $PSScriptRoot 'Invoke-IDVBRelease.ps1'
$workflowRunner = Join-Path $PSScriptRoot 'Invoke-IDVBUpdateWorkflow.ps1'
$manifestRoot = Join-Path $PSScriptRoot 'manifests'
$notesRoot = Join-Path $PSScriptRoot 'notes'
$projectPath = Join-Path $repositoryRoot 'IDVBuff.csproj'
$updateRoot = 'https://download.xgflee.com/updates/'

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host ('=== {0} ===' -f $Text) -ForegroundColor Cyan
}

function Assert-LastExitCode([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Assert-Command([string]$Name, [string]$Hint) {
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found. $Hint"
    }
}

function Test-CommandSuccess([scriptblock]$Command) {
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Command
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }
}

function Get-InnoCompiler {
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    throw 'Inno Setup 6 compiler ISCC.exe was not found.'
}

function Ensure-WranglerAuthentication {
    Write-Host 'Checking Cloudflare Wrangler authentication...'
    if (Test-CommandSuccess { & npx wrangler whoami }) {
        return
    }

    Write-Warning 'Wrangler authentication is missing or expired. Opening the login flow...'
    & npx wrangler login
    Assert-LastExitCode 'Cloudflare Wrangler login'

    if (-not (Test-CommandSuccess { & npx wrangler whoami })) {
        throw 'Wrangler login completed, but authentication is still unavailable.'
    }
}

function Ensure-GitHubAuthentication {
    Write-Host 'Checking GitHub CLI authentication...'
    if (Test-CommandSuccess { & gh auth status --hostname github.com }) {
        return
    }

    Write-Warning 'GitHub CLI authentication is missing or expired. Opening the browser login flow...'
    & gh auth login --hostname github.com --web
    Assert-LastExitCode 'GitHub CLI login'

    if (-not (Test-CommandSuccess { & gh auth status --hostname github.com })) {
        throw 'GitHub CLI login completed, but authentication is still unavailable.'
    }
}

function Ensure-GitInfoExclude {
    $gitDirText = (& git -C $repositoryRoot rev-parse --git-dir).Trim()
    Assert-LastExitCode 'Resolve .git directory'
    $gitDir = if ([IO.Path]::IsPathRooted($gitDirText)) {
        [IO.Path]::GetFullPath($gitDirText)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot $gitDirText))
    }
    $excludePath = Join-Path $gitDir 'info\exclude'
    $parent = Split-Path -Parent $excludePath
    [void][IO.Directory]::CreateDirectory($parent)
    if (-not (Test-Path -LiteralPath $excludePath)) {
        [IO.File]::WriteAllText($excludePath, '', [Text.UTF8Encoding]::new($false))
    }
    $existing = [IO.File]::ReadAllText($excludePath)
    if ($existing -notmatch '(?m)^\.wrangler/$') {
        Add-Content -LiteralPath $excludePath -Value '.wrangler/' -Encoding UTF8
    }
    return $gitDir
}

function Invoke-EnvironmentPreflight {
    Write-Step '环境检查'

    if ($env:OS -ne 'Windows_NT') {
        throw 'This release action only supports Windows.'
    }
    foreach ($pair in @(
        @('git', 'Install Git for Windows and reopen the terminal.'),
        @('dotnet', 'Install the .NET 10 SDK.'),
        @('npm', 'Install Node.js/npm.'),
        @('npx', 'Install Node.js/npm.'),
        @('gh', 'Install GitHub CLI.')
    )) {
        Assert-Command $pair[0] $pair[1]
    }

    foreach ($path in @($releaseRunner, $workflowRunner, $projectPath)) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required release file is missing: $path"
        }
    }

    $dotnetVersion = (& dotnet --version).Trim()
    Assert-LastExitCode 'Read .NET SDK version'
    $dotnetMajor = 0
    if ($dotnetVersion -notmatch '^(?<major>\d+)\.') {
        throw "Cannot parse .NET SDK version '$dotnetVersion'."
    }
    $dotnetMajor = [int]$Matches.major
    if ($dotnetMajor -lt 10) {
        throw "The release requires .NET 10 SDK or newer; found $dotnetVersion."
    }

    & npx wrangler --version
    Assert-LastExitCode 'Verify Wrangler'
    [void](Get-InnoCompiler)

    $privateKey = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PrivateKeyPath))
    if (-not (Test-Path -LiteralPath $privateKey)) {
        throw "Release private key was not found: $privateKey"
    }

    $null = Ensure-GitInfoExclude

    & git -C $repositoryRoot remote get-url origin
    Assert-LastExitCode 'Read origin remote'
    & git -C $repositoryRoot fetch origin
    Assert-LastExitCode 'Fetch origin'

    & dotnet tool restore --tool-manifest (Join-Path $repositoryRoot '.config\dotnet-tools.json')
    Assert-LastExitCode 'Restore pinned dotnet tools'

    Ensure-GitHubAuthentication
    Ensure-WranglerAuthentication

    & gh repo view $GitHubRepository --json nameWithOwner
    Assert-LastExitCode "Verify GitHub repository access: $GitHubRepository"

    $bucketList = (& npx wrangler r2 bucket list) -join "`n"
    Assert-LastExitCode 'Verify R2 bucket access'
    if ($bucketList -notmatch [regex]::Escape($R2Bucket)) {
        throw "R2 bucket '$R2Bucket' was not found in the authenticated Cloudflare account."
    }

    $trackedPrivate = @(git -C $repositoryRoot ls-files -- $PrivateKeyPath)
    Assert-LastExitCode 'Verify private-key tracking state'
    if ($trackedPrivate.Count -ne 0) {
        throw "The release private key is tracked by Git and publication is blocked: $PrivateKeyPath"
    }

    Write-Host 'Environment is ready.' -ForegroundColor Green
}

function Read-ReleaseTargets {
    Write-Step '发行目标'
    Write-Host '[1] GitHub Release'
    Write-Host '[2] 官网 / Updater 测试通道'
    Write-Host '[3] 官网 / Updater 稳定通道'
    Write-Host ''
    $raw = if ($null -ne $Targets -and $Targets.Count -gt 0) {
        ($Targets -join ',').Trim()
    } else {
        (Read-Host '请输入序号，多个目标使用 "," 分隔').Trim()
    }
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw 'At least one publication target is required.'
    }

    $selected = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($part in $raw.Split(',')) {
        $token = $part.Trim()
        $number = 0
        if (-not [int]::TryParse($token, [ref]$number) -or $number -lt 1 -or $number -gt 3) {
            throw "Invalid publication target '$token'. Use only 1, 2, or 3 separated by commas."
        }
        [void]$selected.Add($number)
    }
    return $selected
}

function Read-ProductVersion {
    $raw = if (-not [string]::IsNullOrWhiteSpace($ProductVersion)) {
        $ProductVersion.Trim()
    } else {
        (Read-Host '请输入产品版本，例如 v1.6.0-unstable.2').Trim()
    }
    if ($raw.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $raw = $raw.Substring(1)
    }
    $match = [regex]::Match(
        $raw,
        '^(?<major>\d{1,2})\.(?<minor>\d{1,3})\.(?<patch>\d{1,3})(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$')
    if (-not $match.Success) {
        throw "Invalid product version '$raw'."
    }
    return [pscustomobject]@{
        ProductVersion = $raw
        Major = [int]$match.Groups['major'].Value
        Minor = [int]$match.Groups['minor'].Value
        Patch = [int]$match.Groups['patch'].Value
        IsPrerelease = $match.Groups['prerelease'].Success -and $match.Groups['prerelease'].Value -match '^unstable(?:\.|$)'
    }
}

function Get-ChinaReleaseDateToken {
    $timeZone = [TimeZoneInfo]::FindSystemTimeZoneById('China Standard Time')
    $chinaNow = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $timeZone)
    return $chinaNow.ToString('yy.MM.dd')
}

function Get-GitDirectory {
    $gitDirText = (& git -C $repositoryRoot rev-parse --git-dir).Trim()
    Assert-LastExitCode 'Resolve .git directory'
    if ([IO.Path]::IsPathRooted($gitDirText)) {
        return [IO.Path]::GetFullPath($gitDirText)
    }
    return [IO.Path]::GetFullPath((Join-Path $repositoryRoot $gitDirText))
}

function Get-MaxReservedCounter([string]$DateToken) {
    $maximum = 0
    $pattern = '-'+[regex]::Escape($DateToken)+'\.(?<counter>\d{4})(?:$|[^0-9])'

    if (Test-Path -LiteralPath $manifestRoot) {
        foreach ($file in Get-ChildItem -LiteralPath $manifestRoot -Filter '*.psd1' -File -ErrorAction SilentlyContinue) {
            $text = [IO.File]::ReadAllText($file.FullName)
            foreach ($m in [regex]::Matches($text, $pattern)) {
                $maximum = [Math]::Max($maximum, [int]$m.Groups['counter'].Value)
            }
        }
    }

    $artifactRoot = Join-Path $repositoryRoot 'artifacts\updates'
    if (Test-Path -LiteralPath $artifactRoot) {
        foreach ($directory in Get-ChildItem -LiteralPath $artifactRoot -Directory -ErrorAction SilentlyContinue) {
            $m = [regex]::Match($directory.Name, $pattern)
            if ($m.Success) {
                $maximum = [Math]::Max($maximum, [int]$m.Groups['counter'].Value)
            }
        }
    }

    try {
        $json = (& gh release list --repo $GitHubRepository --limit 100 --json tagName) -join "`n"
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($json)) {
            foreach ($release in @($json | ConvertFrom-Json)) {
                $m = [regex]::Match([string]$release.tagName, $pattern)
                if ($m.Success) {
                    $maximum = [Math]::Max($maximum, [int]$m.Groups['counter'].Value)
                }
            }
        }
    }
    catch {
        Write-Warning "Could not inspect GitHub releases while seeding the build counter: $($_.Exception.Message)"
    }

    $counterFile = Join-Path (Get-GitDirectory) 'idvb-release-counter.txt'
    if (Test-Path -LiteralPath $counterFile) {
        $counterText = [IO.File]::ReadAllText($counterFile).Trim()
        if ($counterText -match '^(?<date>\d{2}\.\d{2}\.\d{2})\|(?<counter>\d{1,4})$' -and $Matches.date -eq $DateToken) {
            $maximum = [Math]::Max($maximum, [int]$Matches.counter)
        }
    }

    return $maximum
}

function Reserve-PublicVersion([string]$ReleaseLine) {
    $dateToken = Get-ChinaReleaseDateToken
    $counter = (Get-MaxReservedCounter $dateToken) + 1
    if ($counter -gt 9999) {
        throw "The build counter for $dateToken has exceeded 9999. Humanity has released enough builds for one day."
    }

    $counterFile = Join-Path (Get-GitDirectory) 'idvb-release-counter.txt'
    [IO.File]::WriteAllText(
        $counterFile,
        "$dateToken|$counter",
        [Text.UTF8Encoding]::new($false))

    return '{0}-{1}.{2:D4}' -f $ReleaseLine, $dateToken, $counter
}

function Read-LatestManifestTemplate {
    if (-not (Test-Path -LiteralPath $manifestRoot)) {
        throw "Manifest directory does not exist: $manifestRoot"
    }
    $templateFile = Get-ChildItem -LiteralPath $manifestRoot -Filter '*.psd1' -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $templateFile) {
        throw 'No existing release manifest is available to use as a policy template.'
    }
    $template = Import-PowerShellDataFile -LiteralPath $templateFile.FullName
    foreach ($name in @('MinimumVersion', 'KeyId', 'VelopackVersion')) {
        if (-not $template.ContainsKey($name)) {
            throw "Manifest template '$($templateFile.Name)' is missing '$name'."
        }
    }
    return $template
}

function ConvertTo-Psd1String([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function New-AutomaticReleaseNotes([string]$ProductVersion, [string]$PublicVersion) {
    [void][IO.Directory]::CreateDirectory($notesRoot)
    $notesPath = Join-Path $notesRoot "$PublicVersion.md"
    if (Test-Path -LiteralPath $notesPath) {
        throw "Release notes already exist; the reserved version cannot be reused: $notesPath"
    }

    $commitLines = @()
    if ($null -ne $ReleaseNotesLines -and $ReleaseNotesLines.Count -gt 0) {
        $commitLines = @($ReleaseNotesLines | ForEach-Object { if ($_.StartsWith('- ')) { $_ } else { "- $_" } })
    }
    else {
        try {
            $commitLines = @(& git -C $repositoryRoot log -n 30 --no-merges --pretty=format:'- %s' 'origin/master..HEAD')
            if ($LASTEXITCODE -ne 0) { $commitLines = @() }
        }
        catch { $commitLines = @() }
    }

    if ($commitLines.Count -eq 0) {
        $commitLines = @('- Maintenance, fixes, and release preparation for this build.')
    }

    $body = @(
        "# Identity Vision Bridge v$ProductVersion",
        '',
        '## Changes',
        ''
    ) + $commitLines + @(
        '',
        "Build: $PublicVersion",
        ''
    )
    [IO.File]::WriteAllLines($notesPath, $body, [Text.UTF8Encoding]::new($false))
    return $notesPath
}

function Set-RepositoryVersion($VersionInfo, [string]$ReleaseLine) {
    $text = [IO.File]::ReadAllText($projectPath)
    $oldProductMatch = [regex]::Match($text, '<IDVBProductVersion>(?<value>[^<]+)</IDVBProductVersion>')
    $oldLineMatch = [regex]::Match($text, '<IDVBReleaseLine>(?<value>[^<]+)</IDVBReleaseLine>')
    if (-not $oldProductMatch.Success -or -not $oldLineMatch.Success) {
        throw 'IDVBuff.csproj does not contain the canonical IDVB version properties.'
    }

    $oldProduct = $oldProductMatch.Groups['value'].Value
    $oldReleaseLine = $oldLineMatch.Groups['value'].Value
    $oldBaseMatch = [regex]::Match($oldProduct, '^(?<base>\d+\.\d+\.\d+)')
    $newBase = "$($VersionInfo.Major).$($VersionInfo.Minor).$($VersionInfo.Patch)"
    $oldAssembly = if ($oldBaseMatch.Success) { "$($oldBaseMatch.Groups['base'].Value).0" } else { $null }
    $newAssembly = "$newBase.0"

    $text = [regex]::Replace(
        $text,
        '<IDVBProductVersion>[^<]+</IDVBProductVersion>',
        "<IDVBProductVersion>$($VersionInfo.ProductVersion)</IDVBProductVersion>",
        1)
    $text = [regex]::Replace(
        $text,
        '<IDVBReleaseLine>[^<]+</IDVBReleaseLine>',
        "<IDVBReleaseLine>$ReleaseLine</IDVBReleaseLine>",
        1)
    $text = [regex]::Replace($text, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$newAssembly</AssemblyVersion>", 1)
    $text = [regex]::Replace($text, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$newAssembly</FileVersion>", 1)
    [IO.File]::WriteAllText($projectPath, $text, [Text.UTF8Encoding]::new($false))

    # Keep legacy release policy files synchronized by exact-value replacement.
    # Exact replacement avoids spraying a broad version regex through unrelated code.
    foreach ($relative in @(
        'installer\IDVB.iss',
        'installer\Build-Release.ps1',
        'IDVBuff.Tests\ReleaseVersioningPolicyTests.cs'
    )) {
        $path = Join-Path $repositoryRoot $relative
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $value = [IO.File]::ReadAllText($path)
        $value = $value.Replace($oldProduct, $VersionInfo.ProductVersion)
        $value = $value.Replace($oldReleaseLine, $ReleaseLine)
        if ($null -ne $oldAssembly) {
            $value = $value.Replace($oldAssembly, $newAssembly)
        }
        [IO.File]::WriteAllText($path, $value, [Text.UTF8Encoding]::new($false))
    }
}

function New-ReleaseManifest($VersionInfo, [string]$ReleaseLine, [string]$PublicVersion, [string]$NotesPath) {
    [void][IO.Directory]::CreateDirectory($manifestRoot)
    $manifestPath = Join-Path $manifestRoot "$PublicVersion.psd1"
    if (Test-Path -LiteralPath $manifestPath) {
        throw "Release manifest already exists; the reserved version cannot be reused: $manifestPath"
    }

    $template = Read-LatestManifestTemplate
    $notesRelative = '..\notes\' + [IO.Path]::GetFileName($NotesPath)
    $migrationText = if ($MigrationBaseline) { '$true' } else { '$false' }
    $manifestText = @(
        '@{',
        '    SchemaVersion = 1',
        '    Draft = $false',
        ('    PublicVersion = {0}' -f (ConvertTo-Psd1String $PublicVersion)),
        ('    ProductVersion = {0}' -f (ConvertTo-Psd1String $VersionInfo.ProductVersion)),
        ('    ReleaseLine = {0}' -f (ConvertTo-Psd1String $ReleaseLine)),
        ('    MinimumVersion = {0}' -f (ConvertTo-Psd1String ([string]$template.MinimumVersion))),
        ('    MigrationBaseline = {0}' -f $migrationText),
        ('    KeyId = {0}' -f (ConvertTo-Psd1String ([string]$template.KeyId))),
        ('    VelopackVersion = {0}' -f (ConvertTo-Psd1String ([string]$template.VelopackVersion))),
        ('    ReleaseNotesPath = {0}' -f (ConvertTo-Psd1String $notesRelative)),
        '}',
        ''
    ) -join "`r`n"
    # PSD1 remains ASCII-compatible; release notes carry UTF-8 text.
    [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.Encoding]::ASCII)

    $publicKey = Join-Path $PSScriptRoot ("trust\{0}.pem" -f $template.KeyId)
    if (-not (Test-Path -LiteralPath $publicKey)) {
        throw "Public trust key from the manifest template is missing: $publicKey"
    }
    return $manifestPath
}

function Get-WorktreeStatusPaths {
    $status = @(git -C $repositoryRoot status --porcelain --untracked-files=all)
    Assert-LastExitCode 'Inspect release worktree'
    $paths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $status) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) { continue }
        $pathText = $line.Substring(3).Trim()
        if ($pathText -match '^(.*) -> (.*)$') {
            $paths.Add($Matches[1]); $paths.Add($Matches[2])
        }
        else {
            $paths.Add($pathText)
        }
    }
    return @($paths)
}

function Assert-SafeReleaseChanges {
    foreach ($path in Get-WorktreeStatusPaths) {
        $normalized = $path.Replace('\', '/').TrimStart('/')
        if ($normalized -match '(^|/)(artifacts|\.secrets|\.wrangler|bin|obj)(/|$)' -or
            $normalized -match '\.(pdb|nupkg|zip|exe|log|tmp|bak)$') {
            throw "Refusing to include generated, secret, cache, or binary path in the release preparation commit: $path"
        }
    }
}

function Commit-ReleasePreparation([string]$PublicVersion) {
    Assert-SafeReleaseChanges
    $status = @(git -C $repositoryRoot status --porcelain --untracked-files=all)
    Assert-LastExitCode 'Inspect release preparation changes'
    if ($status.Count -eq 0) {
        Write-Host 'No local release preparation changes require a commit.'
        return
    }

    # Stage tracked changes, then only explicitly safe untracked source/release files.
    # Never use `git add --all` here: a release button should not discover and
    # immortalize someone's forgotten password.txt. Humanity has enough problems.
    & git -C $repositoryRoot add -u
    Assert-LastExitCode 'Stage tracked release preparation changes'

    $untracked = @(git -C $repositoryRoot ls-files --others --exclude-standard)
    Assert-LastExitCode 'Enumerate untracked release preparation files'
    foreach ($path in $untracked) {
        $normalized = $path.Replace('\', '/').TrimStart('/')
        $allowedLocalReleasePath = $normalized -match '^(release|installer|Tools|web_installer|docs|\.config)(/|$)'
        if (-not (Test-PublicCodeOnlyPath $normalized) -and -not $allowedLocalReleasePath) {
            throw "Untracked file is not on the automatic release allowlist: $path"
        }
        & git -C $repositoryRoot add -- $path
        Assert-LastExitCode "Stage approved untracked release file $path"
    }

    $staged = @(git -C $repositoryRoot diff --cached --name-status)
    Assert-LastExitCode 'Inspect staged release preparation changes'
    if ($staged.Count -eq 0) {
        throw 'The worktree changed, but no release preparation files were staged.'
    }

    Write-Host 'Release preparation commit:'
    $staged | ForEach-Object { Write-Host "  $_" }
    & git -C $repositoryRoot commit -m "release: prepare $PublicVersion"
    Assert-LastExitCode 'Commit release preparation'
}

function Test-PublicCodeOnlyPath([string]$Path) {
    $normalized = $Path.Replace('\', '/').TrimStart('/')
    if ($normalized -match '^release/trust/[A-Za-z0-9._-]+\.pem$') { return $true }
    if ($normalized -match '^Assets/Guide/[A-Za-z0-9._-]+\.png$') { return $true }
    if ($normalized -match '^(release|installer|docs|Tools|\.verify|web_installer|\.claude|\.temp-settings)(/|$)') { return $false }
    if ($normalized -match '^(IDVBuff\.Tests|IDVB\.PluginSystem\.Tests|IDVBuff\.PluginContracts\.Tests|IDVB\.PluginTestHost)(/|$)') { return $false }
    if ($normalized -match '(^|/)\.git(/|$)') { return $false }
    if ($normalized -in @('Startup_IDVB.cmd', 'Startup_RealCLI.cmd', 'Startup_overlay_game.cmd')) { return $true }
    if ($normalized -in @(
        'README.md', 'LICENSE',
        'Assets/Icons/IDVB_icon_square_master.png'
    )) { return $true }
    if ($normalized -match '(^|/)(Package\.appxmanifest|app\.manifest)$') { return $true }
    return $normalized -match '\.(cs|xaml|csproj|slnx)$'
}

function Get-CodeOnlyWorkingTreePaths {
    $paths = @(git -C $repositoryRoot ls-files --cached --others --exclude-standard)
    Assert-LastExitCode 'Enumerate code-only source paths'
    $result = New-Object 'System.Collections.Generic.List[string]'
    foreach ($path in $paths) {
        if (-not (Test-PublicCodeOnlyPath $path)) { continue }
        $fullPath = Join-Path $repositoryRoot ($path.Replace('/', '\'))
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $result.Add($path.Replace('\', '/'))
        }
    }
    return @($result | Sort-Object -Unique)
}

function New-CodeOnlyCandidate([string]$PublicVersion) {
    Write-Step '生成未发布源码候选'
    & git -C $repositoryRoot fetch origin | Out-Host
    Assert-LastExitCode 'Fetch origin before candidate creation'
    $baseCommit = (& git -C $repositoryRoot rev-parse --verify 'origin/master^{commit}').Trim()
    Assert-LastExitCode 'Resolve origin/master'

    $snapshotRoot = Join-Path ([IO.Path]::GetTempPath()) ('IDVB-release-candidate-' + [Guid]::NewGuid().ToString('N'))
    $worktreeAdded = $false
    try {
        & git -C $repositoryRoot worktree add --detach $snapshotRoot $baseCommit | Out-Host
        Assert-LastExitCode 'Create temporary code-only worktree'
        $worktreeAdded = $true

        $sourcePaths = @(Get-CodeOnlyWorkingTreePaths)
        $sourceSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $sourcePaths) {
            [void]$sourceSet.Add($path)
            $source = Join-Path $repositoryRoot ($path.Replace('/', '\'))
            $destination = Join-Path $snapshotRoot ($path.Replace('/', '\'))
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }

        $basePaths = @(git -C $snapshotRoot ls-tree -r --name-only HEAD)
        Assert-LastExitCode 'Enumerate code-only base tree'
        foreach ($path in $basePaths) {
            if ($sourceSet.Contains($path)) { continue }
            $destination = Join-Path $snapshotRoot ($path.Replace('/', '\'))
            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                Remove-Item -LiteralPath $destination -Force
            }
        }

        & git -C $snapshotRoot add --all | Out-Host
        Assert-LastExitCode 'Stage code-only candidate'
        foreach ($path in $sourcePaths) {
            & git -C $snapshotRoot add --force -- $path | Out-Host
            Assert-LastExitCode "Stage code-only path $path"
        }

        $changed = @(git -C $snapshotRoot diff --cached --name-status)
        Assert-LastExitCode 'Inspect code-only candidate changes'
        if ($changed.Count -eq 0) {
            $candidate = $baseCommit
        }
        else {
            Write-Host 'Code-only candidate files:'
            $changed | ForEach-Object { Write-Host "  $_" }
            & git -C $snapshotRoot commit -m "sync: publish IDVB code-only source for $PublicVersion" | Out-Host
            Assert-LastExitCode 'Commit code-only candidate'
            $candidate = (& git -C $snapshotRoot rev-parse --verify HEAD).Trim()
            Assert-LastExitCode 'Resolve code-only candidate SHA'
        }

        $candidateRef = "refs/idvb-release-candidates/$PublicVersion"
        & git -C $repositoryRoot update-ref $candidateRef $candidate | Out-Host
        Assert-LastExitCode 'Pin local release candidate ref'
        return [pscustomobject]@{
            BaseCommit = $baseCommit
            CandidateCommit = $candidate
            CandidateRef = $candidateRef
        }
    }
    finally {
        if ($worktreeAdded -and (Test-Path -LiteralPath $snapshotRoot)) {
            & git -C $repositoryRoot worktree remove --force $snapshotRoot | Out-Host
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $snapshotRoot)) {
                [IO.Directory]::Delete($snapshotRoot, $true)
            }
        }
    }
}

function Invoke-ReleaseStage(
    [string]$ManifestPath,
    [string]$Stage,
    [string]$TargetCommit,
    [switch]$WithPrivateKey,
    [switch]$DryRun
) {
    $parameters = @{
        ManifestPath = $ManifestPath
        Stage = $Stage
        TargetCommit = $TargetCommit
        R2Bucket = $R2Bucket
    }
    if ($WithPrivateKey) { $parameters.PrivateKeyPath = $PrivateKeyPath }
    if ($DryRun) { $parameters.DryRun = $true }

    Write-Step $Stage
    & $releaseRunner @parameters
}

function Confirm-OnlineEnvelope([string]$ManifestPath, [string]$Channel) {
    $manifest = Import-PowerShellDataFile -LiteralPath $ManifestPath
    $localPath = Join-Path $repositoryRoot "artifacts\updates\$($manifest.PublicVersion)\channels\$Channel\feed-envelope.json"
    if (-not (Test-Path -LiteralPath $localPath)) {
        throw "Local verified envelope not found: $localPath"
    }
    $uri = "$updateRoot$Channel/feed-envelope.json"
    $response = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 30 -UseBasicParsing
    if ([int]$response.StatusCode -ne 200) {
        throw "Published envelope returned HTTP $($response.StatusCode): $uri"
    }
    $local = [IO.File]::ReadAllText($localPath, [Text.Encoding]::UTF8).Trim()
    if (-not [string]::Equals($local, $response.Content.Trim(), [StringComparison]::Ordinal)) {
        throw "Online envelope does not match the locally verified envelope: $uri"
    }
    Write-Host "Verified online envelope: $uri" -ForegroundColor Green
}

function Publish-CodeOnlyCandidate($CandidateInfo) {
    Write-Step '发布源码候选'
    & git -C $repositoryRoot fetch origin
    Assert-LastExitCode 'Refresh origin before source publication'
    $currentOrigin = (& git -C $repositoryRoot rev-parse --verify 'origin/master^{commit}').Trim()
    Assert-LastExitCode 'Resolve current origin/master'
    if ($currentOrigin -ne $CandidateInfo.BaseCommit) {
        throw "origin/master changed from $($CandidateInfo.BaseCommit) to $currentOrigin while the release was being verified. Refusing to publish a stale candidate."
    }

    & git -C $repositoryRoot push origin "$($CandidateInfo.CandidateCommit):refs/heads/master"
    Assert-LastExitCode 'Push verified code-only candidate'
    & git -C $repositoryRoot fetch origin
    Assert-LastExitCode 'Refresh origin after source publication'
    $published = (& git -C $repositoryRoot rev-parse --verify 'origin/master^{commit}').Trim()
    if ($published -ne $CandidateInfo.CandidateCommit) {
        throw "origin/master resolved to $published after push; expected $($CandidateInfo.CandidateCommit)."
    }
    Write-Host "Published source commit: $published" -ForegroundColor Green
}

function Assert-WorkingTreeCleanAfterPreparation {
    $status = @(git -C $repositoryRoot status --porcelain --untracked-files=all)
    Assert-LastExitCode 'Inspect worktree after release preparation'
    if ($status.Count -ne 0) {
        throw "Release preparation did not leave a clean worktree.`n$($status -join "`n")"
    }
}

try {
    Invoke-EnvironmentPreflight

    [System.Collections.Generic.HashSet[int]]$targets = Read-ReleaseTargets
    Write-Step '版本信息'
    $versionInfo = Read-ProductVersion
    $releaseLine = 'b{0:D2}.{1}' -f $versionInfo.Major, $versionInfo.Minor

    # GitHub Release consumes the already-published matching updater channel.
    if ($targets.Contains(1)) {
        if ($versionInfo.IsPrerelease) {
            [void]$targets.Add(2)
        }
        else {
            [void]$targets.Add(3)
        }
    }

    $publicVersion = Reserve-PublicVersion $releaseLine
    Write-Host "ProductVersion : $($versionInfo.ProductVersion)"
    Write-Host "ReleaseLine    : $releaseLine"
    Write-Host "PublicVersion  : $publicVersion"

    $notesPath = New-AutomaticReleaseNotes $versionInfo.ProductVersion $publicVersion
    Set-RepositoryVersion $versionInfo $releaseLine
    $manifestPath = New-ReleaseManifest $versionInfo $releaseLine $publicVersion $notesPath

    Commit-ReleasePreparation $publicVersion
    Assert-WorkingTreeCleanAfterPreparation

    $candidate = New-CodeOnlyCandidate $publicVersion
    Write-Host "Candidate SHA  : $($candidate.CandidateCommit)"

    # Stop build servers before entering the immutable payload build. No external
    # publication has happened yet, so any failure below is still cheap and safe.
    Write-Step '.NET 构建服务清理'
    & dotnet build-server shutdown
    Assert-LastExitCode 'Shut down .NET build servers'

    Invoke-ReleaseStage $manifestPath 'Validate' $candidate.CandidateCommit
    Invoke-ReleaseStage $manifestPath 'BuildPayload' $candidate.CandidateCommit

    if ($targets.Contains(2)) {
        Invoke-ReleaseStage $manifestPath 'PackTest' $candidate.CandidateCommit -WithPrivateKey
        Invoke-ReleaseStage $manifestPath 'VerifyTest' $candidate.CandidateCommit
    }
    if ($targets.Contains(3)) {
        Invoke-ReleaseStage $manifestPath 'PackStable' $candidate.CandidateCommit -WithPrivateKey
        Invoke-ReleaseStage $manifestPath 'VerifyStable' $candidate.CandidateCommit
    }

    # Tests run after every requested package has already been physically built,
    # signed, hashed, and structurally verified. A green test result therefore
    # leaves only external writes and online readback.
    Invoke-ReleaseStage $manifestPath 'ReleaseTests' $candidate.CandidateCommit

    if ($targets.Contains(2)) {
        Invoke-ReleaseStage $manifestPath 'PublishTest' $candidate.CandidateCommit -DryRun
    }
    if ($targets.Contains(3)) {
        Invoke-ReleaseStage $manifestPath 'PublishStable' $candidate.CandidateCommit -DryRun
    }

    Write-Step '发布闸门通过'
    Write-Host 'Build, package, signature, structure verification, release tests, and upload dry-run all passed.' -ForegroundColor Green
    Write-Host 'External publication begins now. No compile/package/signing step remains.' -ForegroundColor Green

    Publish-CodeOnlyCandidate $candidate

    if ($targets.Contains(2)) {
        Invoke-ReleaseStage $manifestPath 'PublishTest' $candidate.CandidateCommit
        Confirm-OnlineEnvelope $manifestPath 'win-x64-test'
    }
    if ($targets.Contains(3)) {
        Invoke-ReleaseStage $manifestPath 'PublishStable' $candidate.CandidateCommit
        Confirm-OnlineEnvelope $manifestPath 'win-x64-stable'
    }

    if ($targets.Contains(1)) {
        Write-Step 'GitHub Release'
        & $workflowRunner `
            -ManifestPath $manifestPath `
            -Phase GitHub `
            -Publish `
            -PrivateKeyPath $PrivateKeyPath `
            -R2Bucket $R2Bucket `
            -GitHubRepository $GitHubRepository `
            -TargetCommit $candidate.CandidateCommit
    }

    Invoke-ReleaseStage $manifestPath 'AuditSource' $candidate.CandidateCommit

    Write-Step '发布完成'
    Write-Host "Version : v$($versionInfo.ProductVersion)"
    Write-Host "Build   : $publicVersion"
    Write-Host "Commit  : $($candidate.CandidateCommit)"
    if ($targets.Contains(2)) { Write-Host 'Test    : published + online envelope verified' }
    if ($targets.Contains(3)) { Write-Host 'Stable  : published + online envelope verified' }
    if ($targets.Contains(1)) { Write-Host 'GitHub  : release created and verified' }
}
catch {
    Write-Host ''
    Write-Host 'RELEASE STOPPED' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'The reserved PublicVersion, receipts, packages, and diagnostic artifacts were intentionally left in place.' -ForegroundColor Yellow
    Write-Host 'Do not reuse the same public version for a fresh candidate.' -ForegroundColor Yellow
    exit 1
}
