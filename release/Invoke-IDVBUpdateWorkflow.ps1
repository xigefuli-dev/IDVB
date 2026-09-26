<#
================================================================================
【重要！发布前 Agent 必读：发布准备度核查清单与实战避坑规范】
================================================================================
本脚本是 IDVB 正式发布的唯一顶层入口。任何 Agent 在执行发布前，禁止盲目直接运行，
必须按以下五个维度先做前置检查与环境准备，确保具备动手发布的客观条件：

一、代码与测试一致性自检（最常见发布阻断点）
   1. 识别全量回归差异：日常开发多为局部测试，但发布流水线在构建后会以单线程严苛模式
      跑完所有测试套件（1400+项）。
   2. 变更级联断言核查：若本次代码改动涉及 Schema 版本、默认配置、常量或状态机模型，
      必须主动排查依赖这些定义的旧测试用例，检查是否存在硬编码的历史旧数值断言；
      如有，必须先将测试用例同步修正为最新契约，避免漫长打包后在全量测试门禁处翻车。
   3. 坚守质量底线：全量测试一旦遇到失败，必须定位源头修复真实根因，绝对禁止通过
      跳过测试、降低验证标准或临时注释用例来糊弄发布。

二、执行环境与非交互预检（避免后台任务无响应假死）
   1. 工具链路径检查：检查当前进程的 $env:PATH，确保 Git、dotnet、node/npm 等工具链
      均可在非交互命令行中正常解析。
   2. 凭据静默状态核验：本脚本通常在后台任务中异步执行。必须提前确认 Git 远端、
      GitHub CLI 以及 R2/Wrangler 均已完成免密静默授权；严禁让后台进程触发 Windows
      凭据管理器的 GUI 对话框，否则会导致任务无限期挂起。
   3. 辅助编译器依赖确认：核验打包所需的底层编译工具（如 Inno Setup 编译器 ISCC 等）
      在当前宿主环境中是否真实就位且能被正确定位。

三、基线资产与网络弹性检查
   1. 增量历史包就位确认：生成增量差分包（Delta Package）需要以历史正式基线包为底座。
      检查本地历史缓存是否已包含目标基线包，能复用本地已校验资产时优先复用。
   2. 严防网络阻塞：如果确实需要在线下载基线包或上传发布产物，必须确认环境网络顺畅，
      且下载逻辑受超时、重试与健壮性保护，杜绝无超时的原生阻塞流导致整个流程停滞。

四、发布策略与非破坏性约束
   1. 遵守发布策略守护：发布工作流由项目的 UpdateReleasePolicyTests 严格守护。
      改动发布相关脚本时，必须保持非破坏性设计原则，严禁使用非受控的破坏性文件清理命令。
   2. 确保公开分支纯净：必须核验最终推送到公开主干的内容为纯净代码（Code-Only），
      严格把关暂存区，任何发布工具、密钥、日志、中间缓存均不得泄露至公开提交。

五、构建号原子递增与候选不可变性准则
   1. 禁止拍号：严禁任何主观手写或猜测构建号。必须严格遵循项目指定的版本计数器规范
      进行原子递增，确保构建号全局唯一。
   2. 候选不可变：一旦某个候选版本在构建、验证或测试过程中中断或失败，该版本号即已
      失效，必须保留现场作为诊断证据；严禁就地覆盖或重用失败版本，必须分配全新的
      候选版本号重新启动流程。
================================================================================
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManifestPath,

    [ValidateSet('Source', 'Test', 'Stable', 'GitHub', 'Audit', 'Status')]
    [string]$Phase = 'Status',

    # External publication is always explicit. Without this switch, Test and
    # Stable stop after the exact R2 upload list has been printed.
    [switch]$Publish,

    [string]$PrivateKeyPath = '.\.secrets\idvb-update-2026-01-private.pem',
    [string]$R2Bucket = 'idvb-installer',
    [string]$GitHubRepository = 'xigefuli-dev/IDVB',
    [string]$TargetCommit = 'origin/master'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
$stageRunner = Join-Path $PSScriptRoot 'Invoke-IDVBRelease.ps1'
$updateRoot = 'https://download.xgflee.com/updates/'
$downloadPage = 'https://idvb.xgflee.com/download'

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

function Read-WorkflowManifest {
    if (-not (Test-Path -LiteralPath $manifestFile)) {
        throw "Release manifest not found: $manifestFile"
    }
    $value = Import-PowerShellDataFile -LiteralPath $manifestFile
    foreach ($name in @(
        'SchemaVersion', 'Draft', 'PublicVersion', 'ProductVersion',
        'ReleaseLine', 'KeyId', 'VelopackVersion', 'ReleaseNotesPath'
    )) {
        if (-not $value.ContainsKey($name)) {
            throw "Release manifest is missing '$name'."
        }
    }
    $notesPath = [IO.Path]::GetFullPath((Join-Path (Split-Path $manifestFile) $value.ReleaseNotesPath))
    if (-not (Test-Path -LiteralPath $notesPath)) {
        throw "Release notes not found: $notesPath"
    }
    $value['ResolvedNotesPath'] = $notesPath
    return $value
}

function Assert-CleanSource {
    # Wrangler creates .wrangler cache/state even for read-only-looking CLI
    # probes. Keep it in .git/info/exclude locally; do not delete release-time
    # state merely to satisfy this gate, and never commit it.
    $status = @(git -C $repositoryRoot status --porcelain --untracked-files=all)
    Assert-Success 'Inspect working tree'
    if ($status.Count -ne 0) {
        throw "The update workflow requires a clean worktree.`n$($status -join "`n")"
    }
}

function Assert-PublishManifest {
    if ($manifest.Draft) {
        throw 'A draft manifest cannot be published. Commit a finalized manifest with Draft = $false.'
    }
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

function Assert-SafeLocalReleaseChanges {
    foreach ($path in Get-WorktreeStatusPaths) {
        $normalized = $path.Replace('\', '/').TrimStart('/')
        if (($normalized -match '(^|/)(artifacts|\.secrets|\.wrangler|bin|obj)(/|$)') -or ($normalized -match '\.(pdb|nupkg|zip|exe|log|tmp|bak)$')) {
            throw "Refusing to commit a generated, secret, cache, or temporary path: $path"
        }
    }
}

function Invoke-LocalReleaseCommit {
    $paths = @(Get-WorktreeStatusPaths)
    if ($paths.Count -eq 0) {
        return
    }

    Assert-SafeLocalReleaseChanges
    Write-Host '=== Local release candidate changes ==='
    $paths | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
    $answer = Read-Host 'Commit these local release candidate files before publishing code-only source? [Y/N]'
    if ($answer -notmatch '^(?i)y(es)?$') {
        throw 'The user declined the local release candidate commit.'
    }

    $null = git -C $repositoryRoot add --all
    Assert-Success 'Stage local release candidate changes'
    $staged = @(git -C $repositoryRoot diff --cached --name-status)
    Assert-Success 'Inspect local release candidate files'
    if ($staged.Count -eq 0) {
        throw 'Worktree reported changes, but no files are staged for the local release candidate.'
    }

    Write-Host '=== Local release candidate commit files ==='
    $staged | ForEach-Object { Write-Host $_ }

    $message = "Prepare IDVB release candidate $($manifest.PublicVersion)"
    $null = git -C $repositoryRoot commit -m $message
    Assert-Success 'Commit local release candidate'
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

function Get-CodeOnlyTreePaths([string]$Commit) {
    $paths = @(git -C $repositoryRoot ls-tree -r --name-only $Commit)
    Assert-Success "Enumerate public source files at $Commit"
    return @($paths | Where-Object { Test-PublicCodeOnlyPath $_ } | Sort-Object -Unique)
}

function Copy-CodeOnlySnapshot([string]$SnapshotRoot, [string]$BaseCommit) {
    $sourcePaths = @(Get-CodeOnlyWorkingTreePaths)
    # Remove stale non-code files that an older public snapshot may have left
    # behind while retaining the public update trust key.
    $basePaths = @(git -C $repositoryRoot ls-tree -r --name-only $BaseCommit)
    Assert-Success "Enumerate all public source files at $BaseCommit"
    $sourceSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $sourcePaths) {
        [void]$sourceSet.Add($path)
        $source = Join-Path $repositoryRoot ($path.Replace('/', '\'))
        $destination = Join-Path $SnapshotRoot ($path.Replace('/', '\'))
        $parent = Split-Path -Parent $destination
        [void][IO.Directory]::CreateDirectory($parent)
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

function Invoke-PublicCodeOnlyCommit([switch]$PlanOnly) {
    if (-not [string]::Equals($TargetCommit, 'origin/master', [StringComparison]::OrdinalIgnoreCase)) {
        if ($PlanOnly) {
            $resolvedTarget = (git -C $repositoryRoot rev-parse --verify "$TargetCommit^{commit}").Trim()
            Assert-Success "Resolve local release target $TargetCommit"
            $headCommit = (git -C $repositoryRoot rev-parse --verify 'HEAD^{commit}').Trim()
            Assert-Success 'Resolve current HEAD for local release target'
            if ($resolvedTarget -ne $headCommit) {
                throw "A non-origin release target must resolve to the current local HEAD. Target: $resolvedTarget; HEAD: $headCommit."
            }
            Write-Host "Using committed local release target without code-only publication: $resolvedTarget"
            return $resolvedTarget
        }
        throw 'Code-only source publication requires the default TargetCommit origin/master.'
    }

    Invoke-FetchOrigin 'Fetch origin before code-only source publication'
    $baseCommit = (git -C $repositoryRoot rev-parse --verify 'origin/master^{commit}').Trim()
    Assert-Success 'Resolve origin/master before code-only source publication'

    $receiptRoot = Join-Path (Get-ArtifactRoot $manifest) 'receipts'
    $existingReceipts = @()
    if (Test-Path -LiteralPath $receiptRoot) {
        $existingReceipts = @(Get-ChildItem -LiteralPath $receiptRoot -Filter '*.json' -File)
    }

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
        foreach ($path in Get-CodeOnlyWorkingTreePaths) {
            $null = git -C $snapshotRoot add --force -- $path
            Assert-Success "Stage code-only source path $path"
        }

        $changed = @(git -C $snapshotRoot diff --cached --name-status)
        Assert-Success 'Inspect code-only source commit files'
        if ($changed.Count -eq 0) {
            Write-Host "Public code-only source already matches $baseCommit."
            return $baseCommit
        }
        foreach ($entry in $changed) {
            $changeType = if ($entry.Length -gt 0) { $entry.Substring(0, 1) } else { '' }
            $path = if ($entry.Length -gt 1) { $entry.Substring(1).Trim() } else { $entry }
            if ($path -match '^(.*) -> (.*)$') { $path = $Matches[2] }
            if ($changeType -eq 'D') {
                continue
            }
            if (-not (Test-PublicCodeOnlyPath $path)) {
                throw "Code-only staging contains a forbidden path: $path"
            }
        }

        if ($PlanOnly) {
            Write-Host '=== Code-only source changes not yet published ==='
            $changed | ForEach-Object { Write-Host $_ }
            throw 'The public origin/master code-only snapshot is stale. Run Phase Source with -Publish before building this release.'
        }

        if ($existingReceipts.Count -gt 0) {
            throw "The candidate already has release receipts. Create a new PublicVersion before changing its public source commit: $receiptRoot"
        }

        Write-Host '=== Code-only public commit files ==='
        $changed | ForEach-Object { Write-Host $_ }
        $answer = Read-Host 'Commit this code-only source snapshot and push it to origin/master? [Y/N]'
        if ($answer -notmatch '^(?i)y(es)?$') {
            throw 'The user declined the code-only source commit and push.'
        }

        $message = "sync: publish IDVB code-only source for $($manifest.ProductVersion)"
        $null = git -C $snapshotRoot commit -m $message
        Assert-Success 'Commit code-only source snapshot'
        $publicCommit = (git -C $snapshotRoot rev-parse --verify HEAD).Trim()
        Assert-Success 'Resolve code-only source commit'

        $null = git -C $snapshotRoot push origin "$($publicCommit):refs/heads/master"
        Assert-Success 'Push code-only source commit to origin/master'
        Invoke-FetchOrigin 'Refresh origin/master after code-only source push'
        $publishedCommit = (git -C $repositoryRoot rev-parse --verify 'origin/master^{commit}').Trim()
        if ($publishedCommit -ne $publicCommit) {
            throw "origin/master resolved to $publishedCommit after push, expected $publicCommit."
        }
        Write-Host "Published code-only source commit: $publicCommit"
        return $publicCommit
    }
    finally {
        if ($worktreeAdded -and (Test-Path -LiteralPath $snapshotRoot)) {
            $null = git -C $repositoryRoot worktree remove --force $snapshotRoot
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $snapshotRoot)) {
                [IO.Directory]::Delete($snapshotRoot, $true)
            }
        }
    }
}

function Invoke-CodeOnlyReleasePreparation {
    Assert-PublishManifest
    $receiptRoot = Join-Path (Get-ArtifactRoot $manifest) 'receipts'
    if (Test-Path -LiteralPath $receiptRoot) {
        $existingReceipts = @(Get-ChildItem -LiteralPath $receiptRoot -Filter '*.json' -File)
        if ($existingReceipts.Count -gt 0 -and @(Get-WorktreeStatusPaths).Count -gt 0) {
            throw "The candidate already has release receipts and the worktree has new changes. Create a new PublicVersion before committing or publishing source: $receiptRoot"
        }
    }
    Invoke-LocalReleaseCommit
    return (Invoke-PublicCodeOnlyCommit)
}

function Get-RemainingBuildProcesses {
    @(Get-Process -Name dotnet,MSBuild,VBCSCompiler -ErrorAction SilentlyContinue)
}

function Show-RemainingBuildProcesses($Processes) {
    foreach ($process in $Processes) {
        $path = ''
        try { $path = $process.Path } catch { }
        $executable = if ([string]::IsNullOrWhiteSpace($path)) { '' } else { "; executable '$path'" }
        Write-Host "Build process still running: $($process.ProcessName) (PID $($process.Id))$executable"
    }
}

function Invoke-GracefulBuildProcessCleanup {
    Write-Host 'Attempting the approved graceful cleanup: dotnet build-server shutdown.'
    & dotnet build-server shutdown
    Assert-Success 'Gracefully clean up .NET build servers'
    Start-Sleep -Milliseconds 500
    @(Get-RemainingBuildProcesses)
}

function Assert-BuildProcessesClosed {
    Write-Host 'Shutting down .NET build servers before starting the immutable build...'
    & dotnet build-server shutdown
    Assert-Success 'Shut down .NET build servers'

    $remaining = @(Get-RemainingBuildProcesses)
    if ($remaining.Count -ne 0) {
        Show-RemainingBuildProcesses $remaining
        $answer = Read-Host 'Build hosts are still running. Attempt graceful cleanup and continue this release? [Y/N]'
        if ($answer -notmatch '^(?i)y(es)?$') {
            throw 'Cannot start the release build while .NET/MSBuild processes are still running; the user declined graceful cleanup.'
        }
        $remaining = @(Invoke-GracefulBuildProcessCleanup)
        if ($remaining.Count -ne 0) {
            Show-RemainingBuildProcesses $remaining
            throw 'Cannot start the release build because .NET/MSBuild processes remain after graceful cleanup. No process was force-killed.'
        }
    }
    Write-Host 'Verified that no dotnet, MSBuild, or VBCSCompiler processes remain.'
}

function Assert-Publishable($Manifest) {
    if (-not $Publish) { return }
    Assert-PublishManifest
    Assert-CleanSource
}

function Update-OriginTarget {
    Invoke-FetchOrigin 'Fetch origin before resolving the release target'
}

function Invoke-Stage([string]$Stage, [switch]$WithPrivateKey, [switch]$DryRun) {
    $parameters = @{
        ManifestPath = $manifestFile
        Stage = $Stage
        R2Bucket = $R2Bucket
        TargetCommit = $TargetCommit
    }
    if ($WithPrivateKey) { $parameters.PrivateKeyPath = $PrivateKeyPath }
    if ($DryRun) { $parameters.DryRun = $true }
    & $stageRunner @parameters
    if ($LASTEXITCODE -ne 0) {
        throw "Update stage '$Stage' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-StageIfPending(
    [string]$Stage,
    [string]$Receipt,
    [switch]$WithPrivateKey
) {
    $receiptPath = Join-Path (Get-ArtifactRoot $manifest) "receipts\$Receipt.json"
    if (Test-Path -LiteralPath $receiptPath) {
        # A publish receipt is written after all R2 uploads, before public-domain
        # readback. If readback fails because the Worker is stale, rerunning must
        # skip uploads and retry only verification; otherwise fixed feed keys
        # would be needlessly rewritten after an already complete publication.
        Write-Host "Skipping completed stage: $Stage"
        return
    }
    if ($Stage -in @('Build', 'BuildPayload')) {
        Assert-BuildProcessesClosed
    }
    Invoke-Stage $Stage -WithPrivateKey:$WithPrivateKey
}

function Get-ArtifactRoot($Manifest) {
    Join-Path $repositoryRoot "artifacts\updates\$($Manifest.PublicVersion)"
}

function Confirm-OnlineEnvelope($Manifest, [string]$Channel) {
    $localPath = Join-Path (Get-ArtifactRoot $Manifest) "channels\$Channel\feed-envelope.json"
    if (-not (Test-Path -LiteralPath $localPath)) {
        throw "Local verified envelope not found: $localPath"
    }
    $uri = "$updateRoot$Channel/feed-envelope.json"
    # Windows PowerShell's legacy IE-backed parser can throw NullReferenceException
    # on a perfectly valid JSON response. Basic parsing makes the byte-for-byte
    # envelope comparison portable and avoids mistaking a parser failure for 404.
    $response = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 30 -UseBasicParsing
    if ([int]$response.StatusCode -ne 200) {
        throw "Published envelope returned HTTP $($response.StatusCode): $uri"
    }
    $local = [IO.File]::ReadAllText($localPath, [Text.Encoding]::UTF8).Trim()
    if (-not [string]::Equals($local, $response.Content.Trim(), [StringComparison]::Ordinal)) {
        throw "Online envelope does not match the locally verified envelope: $uri"
    }
    Write-Host "Verified online envelope: $uri"
}

function Show-Status($Manifest) {
    $root = Get-ArtifactRoot $Manifest
    Write-Host "Version: $($Manifest.PublicVersion)"
    Write-Host "Product: $($Manifest.ProductVersion)"
    Write-Host "Draft: $($Manifest.Draft)"
    Write-Host "Target: $TargetCommit"
    Write-Host "Download page: $downloadPage"
    $resolvedStatusCommit = (git -C $repositoryRoot rev-parse --verify "$TargetCommit^{commit}").Trim()
    Write-Host "Commit: $resolvedStatusCommit"
    foreach ($receipt in @(
        'validate', 'build', 'release-tests', 'pack-win-x64-test', 'verify-win-x64-test',
        'publish-win-x64-test', 'pack-win-x64-stable',
        'verify-win-x64-stable', 'publish-win-x64-stable'
    )) {
        $path = Join-Path $root "receipts\$receipt.json"
        $state = if (Test-Path -LiteralPath $path) { 'complete' } else { 'pending' }
        Write-Host ("{0,-28} {1}" -f $receipt, $state)
    }
}

$manifest = Read-WorkflowManifest

switch ($Phase) {
    'Status' {
        Show-Status $manifest
    }
    'Source' {
        if (-not $Publish) {
            throw 'Code-only source publication is an external GitHub change. Re-run Source with -Publish.'
        }
        $TargetCommit = Invoke-CodeOnlyReleasePreparation
        Write-Host "Source commit ready for subsequent release phases: $TargetCommit"
    }
    'Test' {
        Assert-PublishManifest
        Update-OriginTarget
        # Test/Stable publication no longer pushes source as a side effect.
        # Manual callers must publish Source explicitly first. The Codex action
        # uses an unpushed code-only candidate SHA and pushes it only after all
        # local build/package/test gates have passed.
        [void](Invoke-PublicCodeOnlyCommit -PlanOnly)
        Assert-CleanSource
        Invoke-StageIfPending 'Validate' 'validate'
        Invoke-StageIfPending 'BuildPayload' 'build'
        Invoke-StageIfPending 'PackTest' 'pack-win-x64-test' -WithPrivateKey
        Invoke-StageIfPending 'VerifyTest' 'verify-win-x64-test'
        Invoke-StageIfPending 'ReleaseTests' 'release-tests'
        Invoke-Stage 'PublishTest' -DryRun
        if ($Publish) {
            Invoke-StageIfPending 'PublishTest' 'publish-win-x64-test'
            # R2 upload success is not enough: the independently deployed Worker
            # may still be an older version without /updates/* routing. Always
            # verify the public custom domain before declaring the channel live.
            Confirm-OnlineEnvelope $manifest 'win-x64-test'
        }
    }
    'Stable' {
        Assert-PublishManifest
        Update-OriginTarget
        [void](Invoke-PublicCodeOnlyCommit -PlanOnly)
        Assert-CleanSource
        Invoke-StageIfPending 'Validate' 'validate'
        Invoke-StageIfPending 'BuildPayload' 'build'
        Invoke-StageIfPending 'PackStable' 'pack-win-x64-stable' -WithPrivateKey
        Invoke-StageIfPending 'VerifyStable' 'verify-win-x64-stable'
        Invoke-StageIfPending 'ReleaseTests' 'release-tests'
        Invoke-Stage 'PublishStable' -DryRun
        if ($Publish) {
            Invoke-StageIfPending 'PublishStable' 'publish-win-x64-stable'
            # Worker deployment remains a separately authorized external change;
            # this readback catches an undeployed route without redeploying it.
            Confirm-OnlineEnvelope $manifest 'win-x64-stable'
        }
    }
    'GitHub' {
        if (-not $Publish) {
            throw 'GitHub Release creation is an external publication. Re-run with -Publish.'
        }
        Assert-Publishable $manifest
        Invoke-FetchOrigin 'Fetch origin'
        $target = (git -C $repositoryRoot rev-parse --verify "$TargetCommit^{commit}").Trim()
        Assert-Success 'Resolve GitHub release target'
        $isPrerelease = $manifest.ProductVersion -match '-unstable(?:\.|$)'
        $channel = if ($isPrerelease) { 'win-x64-test' } else { 'win-x64-stable' }
        $publicationReceipt = Join-Path (Get-ArtifactRoot $manifest) "receipts\publish-$channel.json"
        if (-not (Test-Path -LiteralPath $publicationReceipt)) {
            throw "GitHub publication requires a completed $channel publication receipt."
        }
        $channelPublication = [IO.File]::ReadAllText($publicationReceipt, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($channelPublication.commit -ne $target) {
            throw "$channel channel commit $($channelPublication.commit) does not match GitHub target $target."
        }
        $channelOutput = Join-Path (Get-ArtifactRoot $manifest) "channels\$channel"
        $setupName = "IDVB-Setup-$($manifest.PublicVersion)-x64.exe"
        $assets = @(
            (Join-Path $channelOutput $setupName),
            (Join-Path $channelOutput "$setupName.sha256"),
            (Join-Path $channelOutput "IDVB-$($manifest.PublicVersion)-x64-manifest.json"),
            (Join-Path $channelOutput 'feed-envelope.json')
        )
        foreach ($asset in $assets) {
            if (-not (Test-Path -LiteralPath $asset)) {
                throw "GitHub release asset is missing: $asset"
            }
        }
        gh auth status
        Assert-Success 'Verify GitHub authentication'
        # A missing release is the normal precondition for creation. Windows
        # PowerShell promotes gh's expected non-zero exit into a native-command
        # error under the script's Stop preference, so invoke through cmd and
        # inspect its exit code explicitly.
        & cmd.exe /d /c "gh release view $($manifest.PublicVersion) --repo $GitHubRepository 1>nul 2>nul"
        $releaseViewExitCode = $LASTEXITCODE
        if ($releaseViewExitCode -eq 0) {
            throw "GitHub Release $($manifest.PublicVersion) already exists; existing releases are never overwritten."
        }
        $githubNotesPath = Join-Path (Get-ArtifactRoot $manifest) 'github-release-notes.md'
        $releaseNotes = [IO.File]::ReadAllText($manifest.ResolvedNotesPath, [Text.Encoding]::UTF8).TrimEnd()
        if ($isPrerelease) {
            $releaseNotes += "`r`n`r`n## 测试更新`r`n`r`n这是测试通道预发布版本，由 IDVB 测试版 Updater 接收；官网默认下载不会指向此版本。`r`n"
        }
        else {
            $releaseNotes += "`r`n`r`n## 下载`r`n`r`n请前往 [Identity Vision Bridge 官方下载页面]($downloadPage) 获取 Windows 客户端和地图包。`r`n"
        }
        [IO.File]::WriteAllText($githubNotesPath, $releaseNotes, [Text.UTF8Encoding]::new($false))
        if ($isPrerelease) {
            gh release create $manifest.PublicVersion `
                --repo $GitHubRepository `
                --target $target `
                --title "Identity Vision Bridge $($manifest.PublicVersion)" `
                --prerelease `
                --notes-file $githubNotesPath `
                @assets
        }
        else {
            gh release create $manifest.PublicVersion `
                --repo $GitHubRepository `
                --target $target `
                --title "Identity Vision Bridge $($manifest.PublicVersion)" `
                --notes-file $githubNotesPath `
                @assets
        }
        Assert-Success "Create GitHub Release from $channel assets"
        gh release view $manifest.PublicVersion --repo $GitHubRepository --json url,tagName,targetCommitish,assets
        Assert-Success 'Verify GitHub Release'
    }
    'Audit' {
        Invoke-Stage 'AuditSource'
        Show-Status $manifest
    }
}
