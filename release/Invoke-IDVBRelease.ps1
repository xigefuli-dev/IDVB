[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManifestPath,

    [ValidateSet('Validate', 'Build', 'BuildPayload', 'ReleaseTests', 'PackTest', 'VerifyTest', 'PublishTest', 'PackStable', 'VerifyStable', 'PublishStable', 'AuditSource', 'All')]
    [string]$Stage = 'Validate',

    # Publishing changes the configured R2 bucket. Every release must first run
    # the same publish stage with -DryRun and inspect the printed object list.
    [switch]$DryRun,
    [string]$R2Bucket = 'idvb-installer',
    [string]$TargetCommit = 'origin/master',

    # This path must point to an offline ECDSA P-256 private key. It must never
    # be staged, committed, copied into artifacts, or uploaded to R2.
    [string]$PrivateKeyPath,

    # Optional executable signing parameters accepted by vpk pack. IDVB does
    # not require Authenticode; update authenticity is established by the
    # ECDSA-signed feed and SHA-256 asset hashes.
    [string]$SignParams
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
$releaseTool = Join-Path $PSScriptRoot 'IDVBuff.ReleaseTool\IDVBuff.ReleaseTool.csproj'
$publicTrustRoot = Join-Path $PSScriptRoot 'trust'
$implementationBranch = -join @([char]0x66F4, [char]0x65B0, [char]0x5668, [char]0x6539, [char]0x9020)

function Assert-Success([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Initialize-RestartManagerInterop {
    if ($null -ne ('IDVB.RestartManager' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IDVB {
    public static class RestartManager {
        private const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        private enum RM_APP_TYPE {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ApplicationName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string ServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Restartable;
        }

        public sealed class ProcessInfo {
            public int ProcessId;
            public string ApplicationName;
            public string ServiceShortName;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(
            out uint sessionHandle,
            int sessionFlags,
            string sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(
            uint sessionHandle,
            uint fileCount,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] string[] fileNames,
            uint applicationCount,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] RM_UNIQUE_PROCESS[] applications,
            uint serviceCount,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] string[] services);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(
            uint sessionHandle,
            out uint processInfoNeeded,
            ref uint processInfoCount,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] RM_PROCESS_INFO[] processInfo,
            out uint rebootReasons);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint sessionHandle);

        public static ProcessInfo[] GetLockingProcesses(string path) {
            uint sessionHandle;
            int result = RmStartSession(out sessionHandle, 0, Guid.NewGuid().ToString("N"));
            if (result != 0) {
                throw new InvalidOperationException("RmStartSession failed with code " + result + ".");
            }

            try {
                result = RmRegisterResources(sessionHandle, 1, new[] { path }, 0, null, 0, null);
                if (result != 0) {
                    throw new InvalidOperationException("RmRegisterResources failed with code " + result + ".");
                }

                uint processInfoNeeded;
                uint processInfoCount = 0;
                uint rebootReasons;
                result = RmGetList(sessionHandle, out processInfoNeeded, ref processInfoCount, null, out rebootReasons);
                if (result == 0 && processInfoNeeded == 0) {
                    return new ProcessInfo[0];
                }
                if (result != ERROR_MORE_DATA) {
                    throw new InvalidOperationException("RmGetList failed with code " + result + ".");
                }

                processInfoCount = processInfoNeeded;
                var nativeInfo = new RM_PROCESS_INFO[(int)processInfoCount];
                result = RmGetList(sessionHandle, out processInfoNeeded, ref processInfoCount, nativeInfo, out rebootReasons);
                if (result != 0) {
                    throw new InvalidOperationException("RmGetList failed with code " + result + ".");
                }

                var resultInfo = new List<ProcessInfo>();
                for (int index = 0; index < processInfoCount; index++) {
                    resultInfo.Add(new ProcessInfo {
                        ProcessId = nativeInfo[index].Process.ProcessId,
                        ApplicationName = nativeInfo[index].ApplicationName,
                        ServiceShortName = nativeInfo[index].ServiceShortName
                    });
                }
                return resultInfo.ToArray();
            }
            finally {
                RmEndSession(sessionHandle);
            }
        }
    }
}
'@
}

function Write-BuildLockDiagnostics([string[]]$Output) {
    $pathPattern = '(?<path>[A-Za-z]:\\[^''"<>]+)'
    $paths = @()
    $lockOwners = @()
    $lockLines = @($Output | Where-Object {
        $_ -match '(?i)(CS2012|cannot access.*file|being used by another process|file.*locked)'
    })
    if ($lockLines.Count -eq 0) {
        $lockLines = @($Output)
    }
    foreach ($line in $lockLines) {
        foreach ($match in [regex]::Matches($line, $pathPattern)) {
            $path = $match.Groups['path'].Value.TrimEnd([char[]]@('.', ',', ';', ':', ')', ']'))
            $extension = [IO.Path]::GetExtension($path)
            if ((Test-Path -LiteralPath $path -PathType Leaf) -and
                ($extension -in @('.dll', '.exe', '.pdb', '.json', '.csproj')) -and
                ($paths -notcontains $path)) {
                $paths += $path
            }
        }
    }

    if ($paths.Count -eq 0) {
        Write-Host 'Build lock diagnostics: no existing file path was found in the failed command output.'
        return @()
    }

    Initialize-RestartManagerInterop
    foreach ($path in $paths) {
        try {
            $owners = @([IDVB.RestartManager]::GetLockingProcesses($path))
        }
        catch {
            Write-Host "Build lock diagnostics failed for '$path': $($_.Exception.Message)"
            continue
        }

        if ($owners.Count -eq 0) {
            Write-Host "Build lock diagnostics: no process currently owns '$path'; the lock may have been released before inspection."
            continue
        }

        foreach ($owner in $owners) {
            $processName = $owner.ApplicationName
            $processPath = ''
            $process = Get-Process -Id $owner.ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                $processName = $process.ProcessName
                try { $processPath = $process.Path } catch { }
            }
            $service = if ([string]::IsNullOrWhiteSpace($owner.ServiceShortName)) { '' } else { "; service '$($owner.ServiceShortName)'" }
            $executable = if ([string]::IsNullOrWhiteSpace($processPath)) { '' } else { "; executable '$processPath'" }
            $lockOwners += [pscustomobject]@{
                Path = $path
                ProcessId = [int]$owner.ProcessId
                ProcessName = $processName
                ApplicationName = $owner.ApplicationName
                ServiceName = $owner.ServiceShortName
                ProcessPath = $processPath
            }
            Write-Host "Build lock owner: '$path' -> PID $($owner.ProcessId), process '$processName'$service$executable; Restart Manager application '$($owner.ApplicationName)'."
        }
    }
    return @($lockOwners)
}

function Invoke-GracefulBuildLockCleanup($Owners) {
    $cleanable = @($Owners | Where-Object {
        $_.ProcessName -in @('dotnet', 'MSBuild', 'VBCSCompiler') -or
        $_.ApplicationName -match '(?i)(dotnet|msbuild|vbcscompiler)'
    })
    if ($cleanable.Count -eq 0) {
        Write-Host 'No automatically cleanable .NET/MSBuild owner was found. External services such as Microsoft Defender must be resolved by the user or allowed to finish scanning.'
        return $false
    }

    Write-Host 'Attempting the approved graceful cleanup: dotnet build-server shutdown.'
    & dotnet build-server shutdown 2>&1 | ForEach-Object { Write-Host ([string]$_) }
    $shutdownCode = $LASTEXITCODE
    if ($shutdownCode -ne 0) {
        Write-Host "Graceful build-server cleanup failed with exit code $shutdownCode."
        return $false
    }
    Start-Sleep -Milliseconds 500

    foreach ($path in @($Owners | Select-Object -ExpandProperty Path -Unique)) {
        try {
            $remaining = @([IDVB.RestartManager]::GetLockingProcesses($path))
        }
        catch {
            Write-Host "Could not recheck '$path' after cleanup: $($_.Exception.Message)"
            return $false
        }
        if ($remaining.Count -ne 0) {
            Write-Host "The lock remains on '$path' after graceful cleanup."
            return $false
        }
    }
    return $true
}

function Invoke-DotnetWithLockDiagnostics(
    [string]$Command,
    [string]$Description,
    [string[]]$Arguments
) {
    $cleanupAttempt = 0
    while ($true) {
        $output = @()
        & dotnet $Command @Arguments 2>&1 | ForEach-Object {
            $line = [string]$_
            $output += $line
            Write-Host $line
        }
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0) {
            return
        }

        Write-Host "Build lock diagnostics for '$Description':"
        $lockReported = @($output | Where-Object {
            $_ -match '(?i)(CS2012|cannot access.*file|being used by another process|file.*locked)'
        }).Count -ne 0
        $owners = @(Write-BuildLockDiagnostics $output)
        if (-not $lockReported -or $cleanupAttempt -ge 2) {
            throw "$Description failed with exit code $exitCode."
        }

        if ($owners.Count -eq 0) {
            $answer = Read-Host 'A file lock was reported, but no owner is visible now. Wait briefly and retry the current command? [Y/N]'
            if ($answer -notmatch '^(?i)y(es)?$') {
                throw "$Description failed with exit code $exitCode; the user declined the lock retry."
            }
            $cleanupAttempt++
            Start-Sleep -Seconds 1
            Write-Host "Retrying '$Description' after the transient lock window (attempt $cleanupAttempt)..."
            continue
        }

        $answer = Read-Host 'A live process is holding a build file. Attempt graceful cleanup of supported build services and retry this command? [Y/N]'
        if ($answer -notmatch '^(?i)y(es)?$') {
            throw "$Description failed with exit code $exitCode; the user declined lock cleanup."
        }
        $cleanupAttempt++
        if (-not (Invoke-GracefulBuildLockCleanup $owners)) {
            throw "$Description failed with exit code $exitCode; the blocking process could not be cleared safely."
        }
        Write-Host "Retrying '$Description' after graceful lock cleanup (attempt $cleanupAttempt)..."
    }
}

function Write-Utf8WithoutBom([string]$Path, [string]$Value) {
    # Signed payload bytes are immutable. Always choose an explicit encoding so
    # Windows PowerShell 5 and PowerShell 7 produce identical signatures.
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Read-ReleaseManifest {
    if (-not (Test-Path -LiteralPath $manifestFile)) {
        throw "Release manifest not found: $manifestFile"
    }
    $value = Import-PowerShellDataFile -LiteralPath $manifestFile
    $required = @(
        'SchemaVersion', 'Draft', 'PublicVersion', 'ProductVersion',
        'ReleaseLine', 'MinimumVersion', 'MigrationBaseline', 'KeyId',
        'VelopackVersion', 'ReleaseNotesPath'
    )
    foreach ($name in $required) {
        if (-not $value.ContainsKey($name)) {
            throw "Release manifest is missing '$name'."
        }
    }
    if ($value.SchemaVersion -ne 1) {
        throw "Unsupported release manifest schema $($value.SchemaVersion)."
    }
    if ($value.VelopackVersion -ne '1.2.0') {
        throw 'The release manifest must use the pinned Velopack 1.2.0 toolchain.'
    }
    $publicVersionMatch = [regex]::Match(
        $value.PublicVersion,
        '^b(?<major>\d{2})\.(?<minor>\d+)-(?<year>\d{2})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<build>\d{4})$')
    if (-not $publicVersionMatch.Success) {
        throw "Invalid PublicVersion '$($value.PublicVersion)'."
    }
    $productVersionMatch = [regex]::Match(
        $value.ProductVersion,
        '^(?<major>\d{1,3})\.(?<minor>\d{1,3})\.(?<patch>\d{1,3})(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$')
    $productMatchesReleaseLine = [int]$publicVersionMatch.Groups['major'].Value -eq [int]$productVersionMatch.Groups['major'].Value -and [int]$publicVersionMatch.Groups['minor'].Value -eq [int]$productVersionMatch.Groups['minor'].Value
    if (-not $productVersionMatch.Success -or -not $productMatchesReleaseLine) {
        throw 'PublicVersion release line and ProductVersion do not match.'
    }
    [void][DateTime]::new(
        2000 + [int]$publicVersionMatch.Groups['year'].Value,
        [int]$publicVersionMatch.Groups['month'].Value,
        [int]$publicVersionMatch.Groups['day'].Value)
    $notesPath = [IO.Path]::GetFullPath((Join-Path (Split-Path $manifestFile) $value.ReleaseNotesPath))
    if (-not (Test-Path -LiteralPath $notesPath)) {
        throw "Release notes not found: $notesPath"
    }
    $value['ReleaseNotes'] = [IO.File]::ReadAllText($notesPath, [Text.Encoding]::UTF8)
    return $value
}

function Get-VelopackVersion($Manifest) {
    if ($Manifest.PublicVersion -notmatch '^b\d{2}\.\d+-(?<year>\d{2})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<build>\d{4})$') {
        throw 'Cannot map the public version to SemVer.'
    }
    $date = "20$($Matches.year)$($Matches.month)$($Matches.day)"
    return "$($Manifest.ProductVersion)-build.$date.$([int]$Matches.build)"
}

function Get-RunContext($Manifest) {
    $sourceCommit = (git -C $repositoryRoot rev-parse --verify "$TargetCommit^{commit}").Trim()
    Assert-Success "Resolve target commit $TargetCommit"
    $root = Join-Path $repositoryRoot "artifacts\updates\$($Manifest.PublicVersion)"
    return [pscustomobject]@{
        SourceCommit = $sourceCommit
        Version = Get-VelopackVersion $Manifest
        Root = $root
        Source = Join-Path $root 'source'
        MainPublish = Join-Path $root 'publish\main'
        UpdaterPublish = Join-Path $root 'publish\updater'
        TestOutput = Join-Path $root 'channels\win-x64-test'
        StableOutput = Join-Path $root 'channels\win-x64-stable'
        Receipts = Join-Path $root 'receipts'
    }
}

function Write-Receipt($Context, [string]$Name, $Value) {
    [void][IO.Directory]::CreateDirectory($Context.Receipts)
    $path = Join-Path $Context.Receipts "$Name.json"
    if (Test-Path -LiteralPath $path) {
        throw "Receipt already exists; refusing to overwrite: $path"
    }
    Write-Utf8WithoutBom $path ($Value | ConvertTo-Json -Depth 8)
}

function Assert-SourceReady($Manifest, $Context) {
    $branchOutput = @(git -C $repositoryRoot branch --show-current)
    Assert-Success 'Read current branch'
    $branch = ($branchOutput -join '').Trim()
    if ($branch -ne '' -and $branch -notin @($implementationBranch, 'remaster', 'main', 'developer-a', 'developer-b', 'integration')) {
        throw "Release must run from the updater implementation branch or remaster; current branch is '$branch'."
    }
    $status = @(git -C $repositoryRoot status --porcelain --untracked-files=all)
    Assert-Success 'Read Git status'
    if ($status.Count -ne 0) {
        throw "Release requires a clean worktree. Review every path before continuing.`n$($status -join "`n")"
    }
    # Windows PowerShell may materialize native-command output as string[].
    # Joining is required before regex matching; calling string methods on the
    # raw git-show result previously broke validation despite valid source.
    $projectText = (git -C $repositoryRoot show "$($Context.SourceCommit):IDVBuff.csproj") -join "`n"
    Assert-Success 'Read target project version'
    if ($projectText -notmatch '<IDVBProductVersion>([^<]+)</IDVBProductVersion>' -or $Matches[1] -ne $Manifest.ProductVersion) {
        throw 'Manifest ProductVersion does not match IDVBuff.csproj.'
    }
    $toolManifest = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot '.config\dotnet-tools.json') | ConvertFrom-Json
    if ($toolManifest.tools.vpk.version -ne $Manifest.VelopackVersion) {
        throw 'Pinned vpk version does not match the release manifest.'
    }
    $publicKeyRelative = "release/trust/$($Manifest.KeyId).pem"
    git -C $repositoryRoot cat-file -e "$($Context.SourceCommit):$publicKeyRelative"
    Assert-Success "Verify target trust key $publicKeyRelative"
    $publicKey = Join-Path $publicTrustRoot "$($Manifest.KeyId).pem"
    # Keep the same array-to-text normalization for PEM content. Newline style
    # is normalized before comparing the locked and local public key.
    $targetPublicKey = ((git -C $repositoryRoot show "$($Context.SourceCommit):$publicKeyRelative") -join "`n").Replace("`r`n", "`n").Trim()
    Assert-Success 'Read target update trust key'
    $localPublicKey = [IO.File]::ReadAllText($publicKey).Replace("`r`n", "`n").Trim()
    if (-not [string]::Equals($targetPublicKey, $localPublicKey, [StringComparison]::Ordinal)) {
        throw 'The local update trust key does not match the locked public source commit.'
    }
    return $publicKey
}

function Assert-StageCommit($Manifest, $Context, [string]$ReceiptName) {
    [void](Assert-SourceReady $Manifest $Context)
    $receiptPath = Join-Path $Context.Receipts "$ReceiptName.json"
    if (-not (Test-Path -LiteralPath $receiptPath)) {
        throw "Required prior-stage receipt not found: $receiptPath"
    }
    $receipt = [IO.File]::ReadAllText($receiptPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($receipt.commit -ne $Context.SourceCommit) {
        throw "Prior stage used commit $($receipt.commit), but the locked source commit is $($Context.SourceCommit)."
    }
}

function Invoke-Validate($Manifest, $Context) {
    $publicKey = Assert-SourceReady $Manifest $Context
    & dotnet tool restore --tool-manifest (Join-Path $repositoryRoot '.config\dotnet-tools.json')
    Assert-Success 'Restore pinned vpk tool'
    & dotnet build $releaseTool -c Release --nologo
    Assert-Success 'Build release signing tool'
    Write-Receipt $Context 'validate' ([ordered]@{
        publicVersion = $Manifest.PublicVersion
        velopackVersion = $Context.Version
        commit = $Context.SourceCommit
        publicKey = $publicKey
        validatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    })
}

function Assert-PublishPayload([string]$PublishDirectory) {
    $forbiddenNames = @('settings.json', 'maps.json', 'recognition-statistics.json', 'attempts.jsonl', 'startup.log')
    foreach ($file in Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File) {
        if ($forbiddenNames -contains $file.Name -or $file.FullName -match '\\(Logs|MapRuntime|AlignmentResearch)\\') {
            throw "Published payload contains user or diagnostic data: $($file.FullName)"
        }
        if ($file.Extension -in @('.pdb', '.trx')) {
            throw "Published payload contains a forbidden build artifact: $($file.FullName)"
        }
    }
    foreach ($required in @(
        'IDVB.exe',
        'Updater\IDVB.Updater.exe',
        'Updater\UpdateTrust\idvb-update-2026-01.pem',
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
        'Assets\Guide\switch-floor.png'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $required))) {
            throw "Published payload is missing $required"
        }
    }
}

function Invoke-BuildPayload($Manifest, $Context) {
    [void](Assert-SourceReady $Manifest $Context)
    if (Test-Path -LiteralPath $Context.Source) {
        throw "Source snapshot already exists: $($Context.Source)"
    }
    [void][IO.Directory]::CreateDirectory($Context.Root)
    $archive = Join-Path $Context.Root 'source.zip'
    if (Test-Path -LiteralPath $archive) {
        throw "Source archive already exists: $archive"
    }
    git -C $repositoryRoot archive --format=zip --output=$archive $Context.SourceCommit
    Assert-Success 'Create source archive'
    Expand-Archive -LiteralPath $archive -DestinationPath $Context.Source

    # The public target is deliberately code-only. Inject local build tooling
    # into the detached snapshot without changing the source commit recorded in
    # the signed update metadata or publishing those tools to origin/master.
    # Do not omit web_installer: UpdateReleasePolicyTests reads its route source.
    # Do not omit test.runsettings either: the public archive intentionally does
    # not contain local release support files, and tests otherwise run in parallel.
    foreach ($relative in @(
        '.config',
        'installer',
        'release',
        'web_installer',
        'Assets',
        'Infrastructure\Configuration',
        'IDVBuff.Tests',
        'IDVB.PluginSystem.Tests',
        'IDVBuff.PluginContracts.Tests',
        'IDVB.PluginTestHost'
    )) {
        $source = Join-Path $repositoryRoot $relative
        $destination = Join-Path $Context.Source $relative
        if (Test-Path -LiteralPath $destination) {
            Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
        }
        else {
            Copy-Item -LiteralPath $source -Destination $destination -Recurse
        }
    }
    foreach ($relative in @(
        'LICENSE',
        'Directory.Build.targets',
        'IDVBuff.Tests\test.runsettings',
        'Tools\Generate-IDVBBuildVersion.ps1',
        'Tools\check-line-counts.ps1'
    )) {
        $source = Join-Path $repositoryRoot $relative
        $destination = Join-Path $Context.Source $relative
        [void][IO.Directory]::CreateDirectory((Split-Path $destination))
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    # Keep these commands serial. Several projects share intermediate paths and
    # concurrent compiler processes can lock generated WinUI files. If a command
    # fails, the wrapper reports the exact file and current Restart Manager owner,
    # asks whether to attempt graceful cleanup of supported build services, and
    # retries the same command only after explicit user approval. It never force-
    # kills an external process or deletes a candidate directory.
    Invoke-DotnetWithLockDiagnostics 'restore' 'Restore solution' @(
        (Join-Path $Context.Source 'IDVBuff.slnx')
        '-m:1'
        '-nr:false'
        '--nologo'
        "-p:IDVBBuildVersion=$($Manifest.PublicVersion)"
    )
    Invoke-DotnetWithLockDiagnostics 'build' 'Build main application' @(
        (Join-Path $Context.Source 'IDVBuff.csproj')
        '-c'
        'Release'
        '-m:1'
        '-nr:false'
        '--no-restore'
        '--nologo'
        "-p:IDVBBuildVersion=$($Manifest.PublicVersion)"
        '-p:PublishTrimmed=false'
    )
    # Release builds emit PDBs unless both symbol properties are disabled. PDBs
    # are rejected by Assert-PublishPayload and must never enter update packages.
    Invoke-DotnetWithLockDiagnostics 'publish' 'Publish updater' @(
        (Join-Path $Context.Source 'Updater\IDVBuff.Updater.csproj')
        '-c'
        'Release'
        '-r'
        'win-x64'
        '-m:1'
        '-nr:false'
        '--self-contained'
        'true'
        '--nologo'
        '-o'
        $Context.UpdaterPublish
        '-p:PublishSingleFile=false'
        '-p:PublishTrimmed=false'
        '-p:DebugSymbols=false'
        '-p:DebugType=None'
    )
    Invoke-DotnetWithLockDiagnostics 'publish' 'Publish main application' @(
        (Join-Path $Context.Source 'IDVBuff.csproj')
        '-c'
        'Release'
        '-r'
        'win-x64'
        '-m:1'
        '-nr:false'
        '--self-contained'
        'true'
        '--nologo'
        '-o'
        $Context.MainPublish
        '-p:Platform=x64'
        '-p:PublishSingleFile=false'
        '-p:PublishTrimmed=false'
        '-p:PublishReadyToRun=true'
        '-p:WindowsAppSDKSelfContained=true'
        '-p:ErrorOnDuplicatePublishOutputFiles=false'
        '-p:DebugSymbols=false'
        '-p:DebugType=None'
        "-p:IDVBBuildVersion=$($Manifest.PublicVersion)"
    )

    $updaterDestination = Join-Path $Context.MainPublish 'Updater'
    if (Test-Path -LiteralPath $updaterDestination) {
        throw 'Main publish unexpectedly already contains an Updater directory.'
    }
    [void][IO.Directory]::CreateDirectory($updaterDestination)
    Copy-Item -Path (Join-Path $Context.UpdaterPublish '*') -Destination $updaterDestination -Recurse
    Assert-PublishPayload $Context.MainPublish
    Write-Receipt $Context 'build' ([ordered]@{
        commit = $Context.SourceCommit
        completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        mainPublish = $Context.MainPublish
        updaterPublish = $Context.UpdaterPublish
    })
}


function Invoke-ReleaseTests($Manifest, $Context) {
    Assert-StageCommit $Manifest $Context 'build'
    if (-not (Test-Path -LiteralPath $Context.Source)) {
        throw 'Build source snapshot is missing; release tests cannot run.'
    }

    # Release tests intentionally run after package construction and verification.
    # If they pass, no compiler, publisher, Velopack packer, or
    # signing step remains before the external publication gate.
    Invoke-DotnetWithLockDiagnostics 'test' 'Run release tests' @(
        (Join-Path $Context.Source 'IDVBuff.Tests\IDVBuff.Tests.csproj')
        '-c'
        'Release'
        '-m:1'
        '-nr:false'
        '--no-restore'
        '--nologo'
        '--settings'
        (Join-Path $Context.Source 'IDVBuff.Tests\test.runsettings')
        "-p:IDVBBuildVersion=$($Manifest.PublicVersion)"
    )

    Write-Receipt $Context 'release-tests' ([ordered]@{
        commit = $Context.SourceCommit
        completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        testProject = 'IDVBuff.Tests\IDVBuff.Tests.csproj'
    })
}

function Invoke-Build($Manifest, $Context) {
    # Compatibility stage for older manual callers. The transactional workflow
    # uses BuildPayload, performs package verification, then runs ReleaseTests.
    Invoke-BuildPayload $Manifest $Context
    Invoke-ReleaseTests $Manifest $Context
}

function Assert-PrivateKeyIsOutsideGit([string]$Path) {
    $private = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $private)) {
        throw "Private key not found: $private"
    }
    # Windows PowerShell 5 runs on a framework without Path.GetRelativePath.
    # Use a canonical root-prefix comparison so signing works on both PS5 and PS7.
    $root = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    if ($private.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $private.Substring($rootPrefix.Length)
        # Avoid `git ls-files --error-unmatch`: with ErrorActionPreference=Stop,
        # its expected stderr for an untracked key becomes a terminating PS5 error.
        # Plain ls-files is quiet; any returned path means the key is tracked.
        $trackedPath = (& git -C $repositoryRoot ls-files -- $relative) -join ''
        Assert-Success 'Inspect private key Git status'
        if (-not [string]::IsNullOrWhiteSpace($trackedPath)) {
            throw 'The private signing key must never be tracked by Git.'
        }
    }
    return $private
}

function New-SignedEnvelope($Manifest, $Context, [string]$Channel, [string]$OutputDirectory, [string]$SetupPath) {
    if ([string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
        throw 'PrivateKeyPath is required to create the signed update envelope.'
    }
    $private = Assert-PrivateKeyIsOutsideGit $PrivateKeyPath
    $feedPath = Join-Path $OutputDirectory "releases.$Channel.json"
    if (-not (Test-Path -LiteralPath $feedPath)) {
        throw "Velopack feed not found: $feedPath"
    }
    $setupFile = Get-Item -LiteralPath $SetupPath
    $payload = [ordered]@{
        schemaVersion = 1
        channel = $Channel
        packageId = 'IdentityVisionBridge'
        publicVersion = $Manifest.PublicVersion
        productVersion = $Manifest.ProductVersion
        velopackVersion = $Context.Version
        minimumVersion = $Manifest.MinimumVersion
        migrationBaseline = [bool]$Manifest.MigrationBaseline
        publishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        commit = $Context.SourceCommit
        releaseNotes = $Manifest.ReleaseNotes
        feedJson = [IO.File]::ReadAllText($feedPath, [Text.Encoding]::UTF8)
        installer = [ordered]@{
            fileName = $setupFile.Name
            sha256 = (Get-FileHash -LiteralPath $setupFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            size = $setupFile.Length
        }
    }
    $payloadPath = Join-Path $OutputDirectory 'feed-payload.json'
    $envelopePath = Join-Path $OutputDirectory 'feed-envelope.json'
    Write-Utf8WithoutBom $payloadPath ($payload | ConvertTo-Json -Depth 8 -Compress)
    & dotnet run --project $releaseTool -c Release --no-build -- sign --payload $payloadPath --private $private --output $envelopePath --key-id $Manifest.KeyId
    Assert-Success "Sign $Channel envelope"
}

function Restore-DeltaBasePackage($Manifest, $Context, [string]$Channel, [string]$OutputDirectory) {
    # Delta generation needs the last full package in vpk's output directory.
    # Fetch it from the public, signed channel rather than relying on a developer
    # having kept old artifacts locally. A missing feed simply makes this channel's
    # first release a full-package baseline.
    $channelUri = "https://download.xgflee.com/updates/$Channel/"
    $remoteEnvelope = Join-Path $OutputDirectory 'previous-feed-envelope.json'
    $verifiedPayload = Join-Path $OutputDirectory 'previous-feed-payload.json'
    try {
        Invoke-WebRequest -Uri ($channelUri + 'feed-envelope.json') -UseBasicParsing -OutFile $remoteEnvelope | Out-Null
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) {
            Write-Host "No existing $Channel feed was found; creating a full-package baseline."
            return $null
        }
        throw "Could not retrieve the existing signed $Channel feed for delta packaging: $($_.Exception.Message)"
    }

    $publicKey = Join-Path $publicTrustRoot "$($Manifest.KeyId).pem"
    # Keep the verifier's human-readable version output out of this function's
    # return value; the caller needs exactly one base-package filename.
    & dotnet run --project $releaseTool -c Release --no-build -- verify --envelope $remoteEnvelope --public $publicKey --key-id $Manifest.KeyId --channel $Channel --payload-output $verifiedPayload | Out-Host
    Assert-Success "Verify existing $Channel feed"
    $payload = [IO.File]::ReadAllText($verifiedPayload, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $feed = $payload.feedJson | ConvertFrom-Json
    $base = @($feed.Assets | Where-Object { $_.Type -eq 'Full' } | Select-Object -Last 1)
    if ($base.Count -ne 1) {
        throw "Existing $Channel feed does not contain exactly one latest full package for delta packaging."
    }
    if ([IO.Path]::GetFileName($base[0].FileName) -ne $base[0].FileName) {
        throw "Existing $Channel full package name is unsafe: $($base[0].FileName)"
    }
    $basePath = Join-Path $OutputDirectory $base[0].FileName
    if (Test-Path -LiteralPath $basePath) {
        throw "Delta base path already exists; refusing to overwrite: $basePath"
    }

    $foundLocal = $false
    $candidateBases = @(Get-ChildItem -Path (Join-Path $repositoryRoot 'artifacts\updates') -Filter $base[0].FileName -Recurse -File -ErrorAction SilentlyContinue)
    foreach ($cand in $candidateBases) {
        if ($cand.Length -eq [int64]$base[0].Size) {
            $candHash = (Get-FileHash -LiteralPath $cand.FullName -Algorithm SHA256).Hash
            if ($candHash -eq $base[0].SHA256) {
                Copy-Item -LiteralPath $cand.FullName -Destination $basePath -Force
                Write-Host "Restored signed delta base package from local cache: $($base[0].FileName)"
                $foundLocal = $true
                break
            }
        }
    }

    if (-not $foundLocal) {
        $partialPath = "$basePath.partial"
        if (Test-Path -LiteralPath $partialPath) {
            [IO.File]::Delete($partialPath)
        }
        $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
        if ($curl) {
            & curl.exe -f -s -S -L --retry 3 --retry-delay 2 ($channelUri + $base[0].FileName) -o $partialPath
            Assert-Success "Download delta base package $($base[0].FileName)"
        }
        else {
            Invoke-WebRequest -Uri ($channelUri + $base[0].FileName) -UseBasicParsing -OutFile $partialPath | Out-Null
        }
        $downloaded = Get-Item -LiteralPath $partialPath
        $hash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash
        if ($downloaded.Length -ne [int64]$base[0].Size -or $hash -ne $base[0].SHA256) {
            throw "Downloaded delta base package failed the signed size or SHA-256 check: $($base[0].FileName)"
        }
        Move-Item -LiteralPath $partialPath -Destination $basePath
        Write-Host "Restored signed delta base package: $($base[0].FileName)"
    }
    # This helper's only caller needs the immutable filename. Returning a scalar
    # prevents command output from ever changing the receipt's object shape.
    return [string]$base[0].FileName
}

function Keep-OnlyTargetFeedAssets($Context, [string]$Channel, [string]$OutputDirectory) {
    # The downloaded base is only a local vpk input. Older Updaters require
    # every public feed asset to use the signed target version, so never retain
    # a previous-version full package in the published feed.
    $feedPath = Join-Path $OutputDirectory "releases.$Channel.json"
    $feed = [IO.File]::ReadAllText($feedPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $targetAssets = @($feed.Assets | Where-Object { $_.Version -eq $Context.Version })
    $targetFullPackages = @($targetAssets | Where-Object { $_.Type -eq 'Full' })
    if ($targetFullPackages.Count -ne 1) {
        throw 'Velopack did not produce exactly one target-version full package.'
    }
    Write-Utf8WithoutBom $feedPath (([ordered]@{ Assets = $targetAssets } | ConvertTo-Json -Depth 8 -Compress))
}

function Invoke-Pack($Manifest, $Context, [string]$Channel, [string]$OutputDirectory, [bool]$Stable) {
    Assert-StageCommit $Manifest $Context 'build'
    if (-not (Test-Path -LiteralPath $Context.MainPublish)) {
        throw 'Build stage has not completed.'
    }
    if (Test-Path -LiteralPath $OutputDirectory) {
        throw "Channel output already exists: $OutputDirectory"
    }
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    # vpk treats a prerelease tag such as unstable.2 as not newer than unstable
    # when the old full package is present in its output directory. Test
    # prereleases therefore publish a full package only; stable releases retain
    # the signed delta-base path below.
    $deltaBase = if ($Manifest.ProductVersion -match '-') {
        $null
    }
    else {
        Restore-DeltaBasePackage $Manifest $Context $Channel $OutputDirectory
    }
    $packDirectory = Join-Path $Context.Root "pack-input\$Channel"
    if (Test-Path -LiteralPath $packDirectory) {
        throw "Channel pack input already exists: $packDirectory"
    }
    [void][IO.Directory]::CreateDirectory($packDirectory)
    Copy-Item -Path (Join-Path $Context.MainPublish '*') -Destination $packDirectory -Recurse
    # This marker keeps an installed test build on the test feed. Legacy Inno
    # builds have no marker and therefore default safely to the stable channel.
    Write-Utf8WithoutBom (Join-Path $packDirectory 'update-channel.txt') $Channel
    $notesPath = Join-Path $OutputDirectory 'release-notes.md'
    Write-Utf8WithoutBom $notesPath $Manifest.ReleaseNotes
    $arguments = @(
        'tool', 'run', 'vpk', '--', 'pack',
        '--packId', 'IdentityVisionBridge',
        '--packVersion', $Context.Version,
        '--packDir', $packDirectory,
        '--mainExe', 'IDVB.exe',
        '--channel', $Channel,
        '--outputDir', $OutputDirectory,
        '--packTitle', 'Identity Vision Bridge',
        '--icon', (Join-Path $Context.Source 'Assets\Icons\IDVB_icon_multisize.ico'),
        '--framework', 'webview2,vcredist143-x64',
        '--releaseNotes', $notesPath
    )
    if (-not [string]::IsNullOrWhiteSpace($SignParams)) {
        $arguments += @('--signParams', $SignParams)
    }
    & dotnet @arguments
    Assert-Success "Pack $Channel"
    Keep-OnlyTargetFeedAssets $Context $Channel $OutputDirectory

    $setup = Get-ChildItem -LiteralPath $OutputDirectory -File -Filter '*-Setup.exe' | Select-Object -First 1
    if ($null -eq $setup) {
        throw "Velopack did not produce Setup.exe for $Channel."
    }
    $versionedSetup = Join-Path $OutputDirectory "IDVB-Setup-$($Manifest.PublicVersion)-x64.exe"
    # Deliver vpk's native Setup byte-for-byte; do not wrap it in Inno.
    if (Test-Path -LiteralPath $versionedSetup) {
        throw "Versioned native Setup already exists: $versionedSetup"
    }
    Copy-Item -LiteralPath $setup.FullName -Destination $versionedSetup
    if ((Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $versionedSetup -Algorithm SHA256).Hash) {
        throw 'Versioned Setup does not match the native Velopack installer.'
    }
    New-SignedEnvelope $Manifest $Context $Channel $OutputDirectory $versionedSetup
    Write-Receipt $Context "pack-$Channel" ([ordered]@{
        commit = $Context.SourceCommit
        channel = $Channel
        completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        output = $OutputDirectory
        deltaBaseFile = $deltaBase
    })
}

function Invoke-Verify($Manifest, $Context, [string]$Channel, [string]$OutputDirectory, [bool]$Stable) {
    Assert-StageCommit $Manifest $Context "pack-$Channel"
    $publicKey = Join-Path $publicTrustRoot "$($Manifest.KeyId).pem"
    $envelopePath = Join-Path $OutputDirectory 'feed-envelope.json'
    $verifiedPayload = Join-Path $OutputDirectory 'feed-payload.verified.json'
    & dotnet run --project $releaseTool -c Release --no-build -- verify --envelope $envelopePath --public $publicKey --key-id $Manifest.KeyId --channel $Channel --payload-output $verifiedPayload
    Assert-Success "Verify $Channel envelope"
    $payload = [IO.File]::ReadAllText($verifiedPayload, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($payload.publicVersion -ne $Manifest.PublicVersion -or $payload.velopackVersion -ne $Context.Version) {
        throw 'Verified payload version mismatch.'
    }
    $feed = $payload.feedJson | ConvertFrom-Json
    foreach ($asset in $feed.Assets) {
        if ([IO.Path]::GetFileName($asset.FileName) -ne $asset.FileName) {
            throw "Feed asset is not a safe leaf name: $($asset.FileName)"
        }
        $assetPath = Join-Path $OutputDirectory $asset.FileName
        if (-not (Test-Path -LiteralPath $assetPath)) {
            throw "Feed asset is missing: $($asset.FileName)"
        }
        $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash
        if ($asset.SHA256 -and $hash -ne $asset.SHA256) {
            throw "Feed SHA-256 mismatch: $($asset.FileName)"
        }
    }
    $setupPath = Join-Path $OutputDirectory $payload.installer.fileName
    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
    if ($setupHash -ne $payload.installer.sha256) {
        throw 'Versioned installer SHA-256 mismatch.'
    }
    if ($Stable) {
        $fullPackage = @($feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Context.Version })
        if ($fullPackage.Count -ne 1) {
            throw 'Stable feed must contain exactly one target-version full package.'
        }
        $targetDeltas = @($feed.Assets | Where-Object { $_.Type -eq 'Delta' -and $_.Version -eq $Context.Version })
        $packReceipt = [IO.File]::ReadAllText((Join-Path $Context.Receipts "pack-$Channel.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($null -ne $packReceipt.deltaBaseFile -and $targetDeltas.Count -eq 0) {
            throw 'A signed delta base was restored but vpk did not produce a target-version delta package.'
        }
        $packagePath = Join-Path $OutputDirectory $fullPackage[0].FileName
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $mainEntry = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)IDVB\.exe$' })
            $updaterEntry = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)Updater/IDVB\.Updater\.exe$' })
            if ($mainEntry.Count -ne 1 -or $updaterEntry.Count -ne 1) {
                throw 'Stable package does not contain exactly one main executable and updater executable.'
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    $setupHashPath = "$setupPath.sha256"
    Write-Utf8WithoutBom $setupHashPath "$($setupHash.ToLowerInvariant())  $([IO.Path]::GetFileName($setupPath))"
    $releaseManifestPath = Join-Path $OutputDirectory "IDVB-$($Manifest.PublicVersion)-x64-manifest.json"
    $releaseManifest = [ordered]@{
        schemaVersion = 1
        productName = 'Identity Vision Bridge'
        productVersion = $Manifest.ProductVersion
        publicVersion = $Manifest.PublicVersion
        velopackVersion = $Context.Version
        channel = $Channel
        commit = $Context.SourceCommit
        keyId = $Manifest.KeyId
        trustModel = 'ecdsa-feed-sha256-assets'
        installer = [ordered]@{
            fileName = [IO.Path]::GetFileName($setupPath)
            sha256 = $setupHash.ToLowerInvariant()
            size = (Get-Item -LiteralPath $setupPath).Length
        }
        envelope = [ordered]@{
            fileName = 'feed-envelope.json'
            sha256 = (Get-FileHash -LiteralPath $envelopePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    Write-Utf8WithoutBom $releaseManifestPath ($releaseManifest | ConvertTo-Json -Depth 6)
    Write-Receipt $Context "verify-$Channel" ([ordered]@{
        commit = $Context.SourceCommit
        channel = $Channel
        completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        trustModel = 'ecdsa-feed-sha256-assets'
    })
}

function Invoke-Publish($Manifest, $Context, [string]$Channel, [string]$OutputDirectory, [bool]$Stable) {
    Assert-StageCommit $Manifest $Context "verify-$Channel"
    Assert-StageCommit $Manifest $Context 'release-tests'
    if ($Manifest.Draft -and -not $DryRun) {
        throw 'Draft manifests cannot write to R2; use -DryRun or finalize a new manifest before building.'
    }
    $payload = [IO.File]::ReadAllText((Join-Path $OutputDirectory 'feed-payload.verified.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
    $feed = $payload.feedJson | ConvertFrom-Json
    $packReceipt = [IO.File]::ReadAllText((Join-Path $Context.Receipts "pack-$Channel.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
    $deltaBaseFile = $packReceipt.deltaBaseFile
    # The feed has already been filtered to target-version assets. Delta bases
    # stay local to packaging, preserving compatibility with older Updaters;
    # never overwrite an immutable release asset from an earlier version.
    $orderedFiles = @($feed.Assets |
        Where-Object { [string]::IsNullOrWhiteSpace($deltaBaseFile) -or $_.FileName -ne $deltaBaseFile } |
        ForEach-Object { $_.FileName })
    $orderedFiles += $payload.installer.fileName
    $orderedFiles += "$($payload.installer.fileName).sha256"
    $orderedFiles += "IDVB-$($Manifest.PublicVersion)-x64-manifest.json"
    $orderedFiles += "releases.$Channel.json"
    # This signed pointer is always uploaded last. Until it changes, clients see
    # the previous complete release even if an earlier object upload is retried.
    $orderedFiles += 'feed-envelope.json'
    foreach ($name in $orderedFiles | Select-Object -Unique) {
        $source = Join-Path $OutputDirectory $name
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Publish asset is missing: $source"
        }
        $target = "$R2Bucket/updates/$Channel/$name"
        if ($DryRun) {
            Write-Host "DRY RUN: npx wrangler r2 object put $target --file $source"
            continue
        }
        # `--remote` is mandatory. Without it Wrangler reads/writes its local
        # Miniflare state under .wrangler, so an existence check can falsely say
        # ABSENT while the real R2 object exists. The cache also dirties Git status;
        # keep .wrangler locally excluded rather than deleting it during a release.
        & npx wrangler r2 object put $target --file $source --remote
        Assert-Success "Upload $name"
    }
    if (-not $DryRun) {
        Write-Receipt $Context "publish-$Channel" ([ordered]@{
            commit = $Context.SourceCommit
            channel = $Channel
            completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        })
    }
}

function Invoke-AuditSource {
    Write-Host '=== Working tree ==='
    git -C $repositoryRoot status --short
    Assert-Success 'Inspect working tree'
    Write-Host '=== Staged files ==='
    git -C $repositoryRoot diff --cached --name-status
    Assert-Success 'Inspect staged files'
    Write-Host '=== Recent commits ==='
    git -C $repositoryRoot log -n 5 --oneline
    Assert-Success 'Inspect recent commits'
}

$manifest = Read-ReleaseManifest
$context = Get-RunContext $manifest

# Stages are explicit and resumable. No stage deletes or overwrites an existing
# source snapshot, channel directory, or receipt. Use a new version when output
# already exists, and inspect why a prior run stopped before continuing.
switch ($Stage) {
    'Validate' { Invoke-Validate $manifest $context }
    'Build' { Invoke-Build $manifest $context }
    'BuildPayload' { Invoke-BuildPayload $manifest $context }
    'ReleaseTests' { Invoke-ReleaseTests $manifest $context }
    'PackTest' { Invoke-Pack $manifest $context 'win-x64-test' $context.TestOutput $false }
    'VerifyTest' { Invoke-Verify $manifest $context 'win-x64-test' $context.TestOutput $false }
    'PublishTest' { Invoke-Publish $manifest $context 'win-x64-test' $context.TestOutput $false }
    'PackStable' { Invoke-Pack $manifest $context 'win-x64-stable' $context.StableOutput $true }
    'VerifyStable' { Invoke-Verify $manifest $context 'win-x64-stable' $context.StableOutput $true }
    'PublishStable' { Invoke-Publish $manifest $context 'win-x64-stable' $context.StableOutput $true }
    'AuditSource' { Invoke-AuditSource }
    'All' {
        Invoke-Validate $manifest $context
        Invoke-BuildPayload $manifest $context
        Invoke-Pack $manifest $context 'win-x64-test' $context.TestOutput $false
        Invoke-Verify $manifest $context 'win-x64-test' $context.TestOutput $false
        Invoke-Pack $manifest $context 'win-x64-stable' $context.StableOutput $true
        Invoke-Verify $manifest $context 'win-x64-stable' $context.StableOutput $true
        Invoke-ReleaseTests $manifest $context
        Invoke-Publish $manifest $context 'win-x64-test' $context.TestOutput $false
        Invoke-Publish $manifest $context 'win-x64-stable' $context.StableOutput $true
        Invoke-AuditSource
    }
}
