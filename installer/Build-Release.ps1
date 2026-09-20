[CmdletBinding()]
param(
    [string]$Version,
    [ValidatePattern('^b\d{2}\.\d+$')]
    [string]$ReleaseLine = 'b01.5',
    [switch]$KeepWorkDirectory,
    [switch]$RequireSignedRelease,
    [switch]$PublishGitHubRelease,
    [switch]$Prerelease,
    [string]$GitHubRepository,
    [string]$ReleaseTitle,
    [string]$ReleaseNotes,
    [string]$ReleaseNotesPath,
    [string]$SourceRoot,
    [string]$ReleaseOutputRoot,
    [string]$TargetCommit,
    [string]$SignToolPath,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleaseVersion.psm1') -Force -DisableNameChecking
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$buildCounterPath = Join-Path $repoRoot '.idvb-build-count'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = New-IDVBBuildVersion -ReleaseLine $ReleaseLine -CounterPath $buildCounterPath
}
if (-not $Version.StartsWith("$ReleaseLine-", [System.StringComparison]::Ordinal)) {
    throw "IDVB build version '$Version' does not match release line '$ReleaseLine'."
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = $repoRoot }
$SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($ReleaseOutputRoot)) { $ReleaseOutputRoot = Join-Path $repoRoot 'artifacts\release' }
$releaseRoot = [System.IO.Path]::GetFullPath($ReleaseOutputRoot).TrimEnd('\')
$projectPath = Join-Path $SourceRoot 'IDVBuff.csproj'
$updaterProjectPath = Join-Path $SourceRoot 'Updater\IDVBuff.Updater.csproj'
[xml]$projectXml = Get-Content -LiteralPath $projectPath
$productVersionNode = $projectXml.SelectSingleNode("//*[local-name()='IDVBProductVersion']")
if ($null -eq $productVersionNode -or [string]::IsNullOrWhiteSpace($productVersionNode.InnerText)) {
    throw "IDVBProductVersion is not defined in $projectPath."
}
$productVersion = $productVersionNode.InnerText.Trim()
$expectedProductParts = ConvertFrom-IDVBProductVersion -ProductVersion (Get-IDVBProductVersion -ReleaseLine $ReleaseLine)
$productParts = ConvertFrom-IDVBProductVersion -ProductVersion $productVersion
if ($productParts.Major -ne $expectedProductParts.Major -or $productParts.Minor -ne $expectedProductParts.Minor) {
    throw "IDVB product version '$productVersion' does not match release line '$ReleaseLine' (expected major.minor $($expectedProductParts.Major).$($expectedProductParts.Minor))."
}
$numericVersion = ConvertTo-IDVBNumericVersion -PublicVersion $Version -Patch $productParts.Patch
$assemblyVersion = "$($productParts.BaseVersion).0"
$workRoot = Join-Path $releaseRoot '.work'
$publishDir = Join-Path $workRoot 'publish'
$updaterPublishDir = Join-Path $workRoot 'updater-publish'
$installerScript = Join-Path $PSScriptRoot 'IDVB.iss'
$dependencyDirectory = Join-Path $PSScriptRoot 'dependencies'
$installerName = "IDVB-Setup-$Version-x64.exe"
$installerPath = Join-Path $releaseRoot $installerName
$hashPath = "$installerPath.sha256"
$manifestPath = Join-Path $releaseRoot "IDVB-$Version-x64-manifest.json"
$generatedReleaseNotesPath = Join-Path $releaseRoot "IDVB-$Version-x64-release-notes.md"

function Assert-ChildPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Parent, [Parameter(Mandatory)][string]$Purpose)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Purpose is outside its allowed directory: $fullPath"
    }
    return $fullPath
}

function Get-IsccPath {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'Inno Setup 6 compiler ISCC.exe was not found.'
}

function Invoke-ReleaseSigning {
    param([Parameter(Mandatory)][string]$Path)

    $hasSignTool = -not [string]::IsNullOrWhiteSpace($SignToolPath)
    $hasCertificate = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
    if ($hasSignTool -ne $hasCertificate) {
        throw 'SignToolPath and CertificateThumbprint must be supplied together.'
    }
    if (-not $hasSignTool) {
        if ($RequireSignedRelease) {
            throw 'A signed release requires SignToolPath and CertificateThumbprint.'
        }
        return
    }

    if (-not (Test-Path -LiteralPath $SignToolPath)) {
        throw "SignTool was not found: $SignToolPath"
    }

    & $SignToolPath sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $Path with exit code $LASTEXITCODE"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') {
        throw "Authenticode validation failed for ${Path}: $($signature.Status)"
    }
}

function Get-GitHubCliPath {
    $command = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        "${env:ProgramFiles}\GitHub CLI\gh.exe",
        "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe",
        "${env:LOCALAPPDATA}\Programs\GitHub CLI\gh.exe"
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw 'GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and run gh auth login.'
}

function Invoke-GitHubRelease {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string[]]$Assets,
        [Parameter(Mandatory)][string]$NotesFile,
        [Parameter(Mandatory)][string]$TargetCommit,
        [Parameter(Mandatory)][string]$Title
    )

    $gh = Get-GitHubCliPath

    & $gh auth status
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub CLI is not authenticated. Run gh auth login before publishing.'
    }

    $repositoryArguments = @()
    if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) {
        $repositoryArguments = @('--repo', $GitHubRepository)
    }

    & $gh release view $Tag @repositoryArguments 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw "GitHub Release $Tag already exists. Use a new version; existing releases are not overwritten."
    }

    $arguments = @(
        'release', 'create', $Tag,
        '--target', $TargetCommit,
        '--title', $Title,
        '--notes-file', $NotesFile
    )
    if ($Prerelease) { $arguments += '--prerelease' }
    $arguments += $repositoryArguments
    $arguments += $Assets
    & $gh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Release creation failed with exit code $LASTEXITCODE."
    }
}

function Assert-Dependency {
    param([Parameter(Mandatory)][string]$FileName, [Parameter(Mandatory)][string]$ExpectedHash)
    $path = Join-Path $dependencyDirectory $FileName
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing offline dependency: $path. See installer\DEPENDENCIES.md."
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedHash.ToLowerInvariant()) {
        throw "Offline dependency SHA-256 does not match: $FileName"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw "Offline dependency signature is invalid or not published by Microsoft: $FileName ($($signature.Status))"
    }
    return Get-Item -LiteralPath $path
}

function Assert-PublishContainsNoUserData {
    param([Parameter(Mandatory)][string]$PublishDirectory)

    # The publish folder is the only application payload accepted by Inno
    # Setup. Reject local state explicitly so a developer's home-page counters,
    # settings, calibration, map area, DPI, or research captures can never be
    # promoted into a release by a future project-file change.
    $forbiddenFileNames = @(
        'settings.json',
        'recognition-statistics.json',
        'maps.json',
        'attempts.jsonl',
        'startup.log'
    )
    $forbiddenDirectories = @(
        'AlignmentResearch',
        'Logs',
        'MapRuntime',
        'Maps'
    )

    $leaks = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -Force |
        Where-Object {
            $_.Name -in $forbiddenFileNames -or
            ($_.PSIsContainer -and $_.Name -in $forbiddenDirectories)
        } |
        Select-Object -ExpandProperty FullName
    if ($leaks) {
        throw "Published payload contains user-local state: $($leaks -join ', ')"
    }
}

if (-not (Test-Path -LiteralPath $projectPath)) { throw "Project file not found: $projectPath" }
if (-not (Test-Path -LiteralPath $installerScript)) { throw "Installer script not found: $installerScript" }
$sourceCommit = (git -C $SourceRoot rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw "Cannot determine the source Git commit from $SourceRoot."
}
if (-not [string]::IsNullOrWhiteSpace($TargetCommit)) {
    $resolvedTargetCommit = (git -C $SourceRoot rev-parse --verify "$TargetCommit^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $resolvedTargetCommit) {
        throw "SourceRoot must be checked out at TargetCommit. Expected $TargetCommit; found $sourceCommit."
    }
}

$webView2 = Assert-Dependency -FileName 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe' -ExpectedHash 'e99838c51bb3379b244654aa77e33032d42fc2b5d224c5babce432d9fd3dcb28'
$vcRedist = Assert-Dependency -FileName 'vc_redist.x64.exe' -ExpectedHash 'cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b'
$iscc = Get-IsccPath

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$safeWorkRoot = Assert-ChildPath -Path $workRoot -Parent $releaseRoot -Purpose 'Work directory'
if (Test-Path -LiteralPath $safeWorkRoot) { Remove-Item -LiteralPath $safeWorkRoot -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

foreach ($outputPath in @($installerPath, $hashPath, $manifestPath, $generatedReleaseNotesPath)) {
    $safeOutputPath = Assert-ChildPath -Path $outputPath -Parent $releaseRoot -Purpose 'Release output'
    if (Test-Path -LiteralPath $safeOutputPath) { Remove-Item -LiteralPath $safeOutputPath -Force }
}

Write-Host "Publishing $Version ($numericVersion) self-contained x64 application..."
$buildOutputRoot = Join-Path $workRoot 'build'
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true --nologo -o $publishDir -p:Platform=x64 -p:BaseOutputPath="$buildOutputRoot\" -p:PublishTrimmed=false -p:PublishReadyToRun=true -p:WindowsAppSDKSelfContained=true -p:Version=$productVersion -p:AssemblyVersion=$assemblyVersion -p:FileVersion=$numericVersion -p:InformationalVersion=$Version -p:IDVBProductVersion=$productVersion -p:IDVBReleaseLine=$ReleaseLine -p:IDVBBuildVersion=$Version -p:IDVBBuildCounterPath="$buildCounterPath" -p:WindowsPackageType=None
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# This is the final Inno-to-Velopack bridge installer. It must carry the
# independent updater so users on the last legacy build can enter the new
# update path without downloading a second migration utility.
& dotnet publish $updaterProjectPath -c Release -r win-x64 --self-contained true --nologo -o $updaterPublishDir -p:BaseOutputPath="$workRoot\updater-build\" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$productVersion -p:AssemblyVersion=$assemblyVersion -p:FileVersion=$numericVersion -p:InformationalVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "Updater publish failed with exit code $LASTEXITCODE" }
$updaterDestination = Join-Path $publishDir 'Updater'
if (Test-Path -LiteralPath $updaterDestination) { throw "Updater destination already exists: $updaterDestination" }
New-Item -ItemType Directory -Path $updaterDestination | Out-Null
Copy-Item -Path (Join-Path $updaterPublishDir '*') -Destination $updaterDestination -Recurse

$requiredFiles = @(
    'IDVB.exe',
    'IDVB.deps.json',
    'IDVB.runtimeconfig.json',
    'OpenCvSharpExtern.dll',
    'WebView2Loader.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll',
    'Assets\Square44x44Logo.targetsize-24_altform-unplated.png',
    'Assets\Icons\IDVB_icon_multisize.ico',
    'Assets\Icons\IDVB_icon_square_master.png',
    'Assets\Gate.png',
    'Assets\1F.png',
    'Assets\2F.png',
    'Assets\Guide\control-panel-end.png',
    'Assets\Guide\control-panel-start.png',
    'Assets\Guide\game-map-toggle.png',
    'Assets\Guide\quick-scan-complete.png',
    'Assets\Guide\quick-scan-map-open.png',
    'Assets\Guide\quick-scan-select-map.png',
    'Assets\Guide\quick-scan-start.png',
    'Assets\Guide\reset-alignment.png',
    'Assets\Guide\save-map-cache.png',
    'Assets\Guide\switch-floor.png',
    'Updater\IDVB.Updater.exe',
    'Updater\UpdateTrust\idvb-update-2026-01.pem'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $relativePath))) {
        throw "Published payload is missing required file: $relativePath"
    }
}
$forbiddenFiles = @('IDVBuff.exe', 'Install.cmd', 'Install.ps1', 'Uninstall.cmd', 'Uninstall.ps1')
foreach ($forbiddenFile in $forbiddenFiles) {
    if (Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter $forbiddenFile -ErrorAction SilentlyContinue) {
        throw "Published payload contains forbidden file: $forbiddenFile"
    }
}
Assert-PublishContainsNoUserData -PublishDirectory $publishDir
Invoke-ReleaseSigning -Path (Join-Path $publishDir 'IDVB.exe')
Invoke-ReleaseSigning -Path (Join-Path $publishDir 'Updater\IDVB.Updater.exe')

Write-Host 'Compiling the Chinese native installer...'
& $iscc "/DBuildOutput=$publishDir" "/DReleaseOutput=$releaseRoot" "/DPublicVersion=$Version" "/DNumericVersion=$numericVersion" $installerScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $installerPath)) { throw "Installer was not produced: $installerPath" }
Invoke-ReleaseSigning -Path $installerPath

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$installerHash  $installerName" -Encoding ascii
$appExePath = Join-Path $publishDir 'IDVB.exe'
$appHash = (Get-FileHash -LiteralPath $appExePath -Algorithm SHA256).Hash.ToLowerInvariant()
$updaterExePath = Join-Path $publishDir 'Updater\IDVB.Updater.exe'
$updaterHash = (Get-FileHash -LiteralPath $updaterExePath -Algorithm SHA256).Hash.ToLowerInvariant()
$installerSignature = Get-AuthenticodeSignature -LiteralPath $installerPath
$appSignature = Get-AuthenticodeSignature -LiteralPath $appExePath
$updaterSignature = Get-AuthenticodeSignature -LiteralPath $updaterExePath
$manifest = [ordered]@{
    productName = 'Identity Vision Bridge'; productShortName = 'IDVB'; productVersion = $productVersion; publicVersion = $Version; numericVersion = $numericVersion; targetRuntime = 'win-x64'; buildTimeUtc = [DateTime]::UtcNow.ToString('o'); commit = $sourceCommit
    installer = [ordered]@{ fileName = $installerName; sha256 = $installerHash; signatureStatus = [string]$installerSignature.Status }
    application = [ordered]@{ fileName = 'IDVB.exe'; sha256 = $appHash; signatureStatus = [string]$appSignature.Status }
    updater = [ordered]@{ fileName = 'Updater/IDVB.Updater.exe'; sha256 = $updaterHash; signatureStatus = [string]$updaterSignature.Status }
    dependencies = @(
        [ordered]@{ fileName = $webView2.Name; sha256 = (Get-FileHash -LiteralPath $webView2.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); version = $webView2.VersionInfo.ProductVersion },
        [ordered]@{ fileName = $vcRedist.Name; sha256 = (Get-FileHash -LiteralPath $vcRedist.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); version = $vcRedist.VersionInfo.ProductVersion }
    )
    installerCompiler = 'Inno Setup 6.7.3'
    releaseDataPolicy = [ordered]@{
        userStateIncluded = $false
        firstRunConfiguration = 'safe-defaults'
        localDataDirectory = '%LocalAppData%\IDVB'
        uninstallKeepsLocalDataByDefault = $true
    }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8

if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $notesFile = [System.IO.Path]::GetFullPath($ReleaseNotesPath)
    if (-not (Test-Path -LiteralPath $notesFile)) { throw "Release notes file not found: $notesFile" }
}
else {
    $releaseNotes = if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) { $ReleaseNotes } else { "# Identity Vision Bridge $Version (x64)`r`n`r`n- Chinese graphical installer and native uninstaller.`r`n- IDVB.exe is the published executable name.`r`n- WebView2 and Visual C++ offline redistributables are included.`r`n- Windows 10 and Windows 11 Settings can launch the native uninstaller.`r`n`r`nInstaller SHA-256: ``$installerHash`` `r`n`r`nSignature status: installer $($installerSignature.Status); application $($appSignature.Status)." }
    Set-Content -LiteralPath $generatedReleaseNotesPath -Value $releaseNotes -Encoding utf8
    $notesFile = $generatedReleaseNotesPath
}
if ([string]::IsNullOrWhiteSpace($ReleaseTitle)) { $ReleaseTitle = "Identity Vision Bridge $Version" }

if (-not $KeepWorkDirectory) { Remove-Item -LiteralPath $safeWorkRoot -Recurse -Force }

if ($PublishGitHubRelease) {
    Invoke-GitHubRelease -Tag $Version -Assets @($installerPath, $hashPath, $manifestPath) -NotesFile $notesFile -TargetCommit $sourceCommit -Title $ReleaseTitle
}

Get-Item -LiteralPath $installerPath, $hashPath, $manifestPath, $notesFile | Select-Object FullName, Length, LastWriteTime
