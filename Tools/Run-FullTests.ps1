[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$testProject = Join-Path $repositoryRoot 'IDVBuff.Tests\IDVBuff.Tests.csproj'
$runSettings = Join-Path $repositoryRoot 'IDVBuff.Tests\test.runsettings'

function Assert-Success([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git') -PathType Container)) {
    throw "The script must run inside the IDVB Git working tree: $repositoryRoot"
}

foreach ($path in @($testProject, $runSettings)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required full-test input is missing: $path"
    }
}

Write-Host '=== Running the IDVB full regression suite ==='
Write-Host "Project: $testProject"
Write-Host "Run settings: $runSettings"
Write-Host 'Mode: Release, one MSBuild process, serialized xUnit collections'

& dotnet test $testProject `
    -c Release `
    -m:1 `
    -nr:false `
    --nologo `
    --settings $runSettings
Assert-Success 'Run the full IDVB regression suite'

Write-Host 'Full regression suite passed.'
