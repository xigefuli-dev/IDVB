param(
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

$catalogPath = Join-Path $env:LOCALAPPDATA "IDVBuff\Maps\maps.json"
if (-not (Test-Path -LiteralPath $catalogPath)) {
    throw "Map catalog not found: $catalogPath"
}

# All 28 source images use the same 1206x2622 guide layout. The common
# first-floor canvas was manually reviewed on the 181x393 audit contact sheet;
# keeping one canvas preserves the maps' relative door-distance scale.
$regions = @{}
foreach ($sequence in 7..34) {
    $regions[$sequence] = @(0, 65, 181, 315)
}

function Set-ObjectProperty {
    param($Object, [string]$Name, $Value)
    if ($Object.PSObject.Properties[$Name]) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function New-NormalizedRegion {
    param([object[]]$CellBounds)
    $left, $top, $right, $bottom = $CellBounds
    [pscustomobject][ordered]@{
        X = [Math]::Round($left / 181.0, 12)
        Y = [Math]::Round($top / 393.0, 12)
        Width = [Math]::Round(($right - $left) / 181.0, 12)
        Height = [Math]::Round(($bottom - $top) / 393.0, 12)
    }
}

function Test-SameRegion {
    param($Left, $Right)
    if ($null -eq $Left -or $null -eq $Right) {
        return $false
    }
    $epsilon = 0.0000001
    return [Math]::Abs($Left.X - $Right.X) -le $epsilon `
        -and [Math]::Abs($Left.Y - $Right.Y) -le $epsilon `
        -and [Math]::Abs($Left.Width - $Right.Width) -le $epsilon `
        -and [Math]::Abs($Left.Height - $Right.Height) -le $epsilon
}

function Convert-AnchorBounds {
    param($Bounds, $OldRegion, $NewRegion)
    if ($null -eq $Bounds) {
        return $null
    }

    $source = [pscustomobject]@{
        X = $OldRegion.X + ($Bounds.X * $OldRegion.Width)
        Y = $OldRegion.Y + ($Bounds.Y * $OldRegion.Height)
        Width = $Bounds.Width * $OldRegion.Width
        Height = $Bounds.Height * $OldRegion.Height
    }
    $epsilon = 0.000001
    $contained = $source.X -ge $NewRegion.X - $epsilon `
        -and $source.Y -ge $NewRegion.Y - $epsilon `
        -and $source.X + $source.Width -le $NewRegion.X + $NewRegion.Width + $epsilon `
        -and $source.Y + $source.Height -le $NewRegion.Y + $NewRegion.Height + $epsilon
    if (-not $contained) {
        return $null
    }

    [pscustomobject][ordered]@{
        X = [Math]::Round(($source.X - $NewRegion.X) / $NewRegion.Width, 12)
        Y = [Math]::Round(($source.Y - $NewRegion.Y) / $NewRegion.Height, 12)
        Width = [Math]::Round($source.Width / $NewRegion.Width, 12)
        Height = [Math]::Round($source.Height / $NewRegion.Height, 12)
    }
}

$catalog = Get-Content -Raw -Encoding UTF8 -LiteralPath $catalogPath | ConvertFrom-Json
$maps = @($catalog.Maps | Sort-Object SequenceNumber)
$actualSequences = @($maps.SequenceNumber)
$missing = @(7..34 | Where-Object { $_ -notin $actualSequences })
$unexpected = @($actualSequences | Where-Object { $_ -notin 7..34 })
if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
    throw "Expected map sequences 7..34. Missing: $($missing -join ','); unexpected: $($unexpected -join ',')"
}

$clearedOptionalAnchors = 0
foreach ($map in $maps) {
    $newRegion = New-NormalizedRegion $regions[[int]$map.SequenceNumber]
    $firstFloor = $map.Recognition.FirstFloor
    $oldRegion = if ($null -ne $firstFloor.RecognitionRegion) {
        $firstFloor.RecognitionRegion
    }
    else {
        [pscustomobject]@{ X = 0.0; Y = 0.0; Width = 1.0; Height = 1.0 }
    }

    $regionChanged = -not (Test-SameRegion $oldRegion $newRegion)
    if ($regionChanged) {
        foreach ($anchor in @($firstFloor.Anchors)) {
            $converted = Convert-AnchorBounds $anchor.Bounds $oldRegion $newRegion
            if ($null -ne $anchor.Bounds -and $null -eq $converted) {
                if ($anchor.Key -in @("main-entrance", "side-entrance")) {
                    throw "Required anchor '$($anchor.Key)' is outside map $($map.SequenceNumber)'s reviewed region."
                }
                $clearedOptionalAnchors++
            }
            $anchor.Bounds = $converted
        }
        Set-ObjectProperty $firstFloor "RecognitionPixelWidth" 0
        Set-ObjectProperty $firstFloor "RecognitionPixelHeight" 0
    }

    Set-ObjectProperty $firstFloor "RecognitionRegion" $newRegion
    $firstFloor.PSObject.Properties.Remove("RequiredAnchors")

    foreach ($anchor in @($firstFloor.Anchors)) {
        if ($anchor.Key -in @("main-entrance", "side-entrance")) {
            $anchor.Role = 0
            Set-ObjectProperty $anchor "Weight" 1.0
            $anchor.IsBuiltIn = $true
        }
    }

    $secondFloor = $map.Recognition.SecondFloor
    $secondFloor.PSObject.Properties.Remove("RequiredAnchors")
    foreach ($anchor in @($secondFloor.Anchors)) {
        if ($anchor.Key -eq "second-floor-primary") {
            $anchor.Role = 1
            Set-ObjectProperty $anchor "Weight" 0.35
            $anchor.IsBuiltIn = $true
        }
    }

    $map.Recognition.SchemaVersion = 3
    $map.UpdatedAt = [DateTimeOffset]::UtcNow.ToString("O")
}

Write-Host "Validated $($maps.Count) maps; $clearedOptionalAnchors out-of-region optional anchors will be cleared."
if (-not $Apply) {
    Write-Host "Dry run only. Re-run with -Apply to write the migrated catalog."
    exit 0
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupPath = Join-Path (Split-Path $catalogPath) "maps.pre-schema-v3-$timestamp.json"
Copy-Item -LiteralPath $catalogPath -Destination $backupPath
$temporaryPath = "$catalogPath.v3.tmp"
$json = $catalog | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporaryPath -Destination $catalogPath -Force
Write-Host "Migrated catalog: $catalogPath"
Write-Host "Backup: $backupPath"
