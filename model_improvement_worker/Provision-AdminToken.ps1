[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workerRoot = $PSScriptRoot
$repositoryRoot = Split-Path $workerRoot -Parent
$secretDirectory = Join-Path $repositoryRoot '.secrets'
$secretPath = Join-Path $secretDirectory 'idvb-model-improvement-admin-token.txt'

New-Item -ItemType Directory -Path $secretDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $secretPath)) {
    $tokenBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $token = [Convert]::ToHexString($tokenBytes).ToLowerInvariant()
    [IO.File]::WriteAllText($secretPath, $token, [Text.UTF8Encoding]::new($false))
}

$adminToken = [IO.File]::ReadAllText($secretPath).Trim()
if ($adminToken -notmatch '^[0-9a-f]{64}$') {
    throw "The stored admin token has an invalid format: $secretPath"
}

$adminToken | npx wrangler secret put ADMIN_TOKEN --config "$workerRoot\wrangler.toml"
if ($LASTEXITCODE -ne 0) {
    throw "Wrangler could not store ADMIN_TOKEN."
}

Write-Host "Admin token is stored locally at $secretPath"
