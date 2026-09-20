[CmdletBinding()]
param(
    [string]$KeyId = 'idvb-update-2026-01'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$privatePath = Join-Path $repositoryRoot ".secrets\$KeyId-private.pem"
$publicPath = Join-Path $PSScriptRoot "trust\$KeyId.pem"

# The private PEM is created only under the gitignored .secrets directory.
# The tool refuses to overwrite either half of the pair. Back up the private
# key securely: losing it requires publishing a client update that trusts a new key.
& dotnet run --project (Join-Path $PSScriptRoot 'IDVBuff.ReleaseTool\IDVBuff.ReleaseTool.csproj') `
    -c Release -- generate-key `
    --private $privatePath `
    --public $publicPath
if ($LASTEXITCODE -ne 0) { throw 'IDVB update signing key generation failed.' }

Write-Host "Private key (never commit): $privatePath"
Write-Host "Public key (commit with the updater): $publicPath"
