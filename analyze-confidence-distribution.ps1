#!/usr/bin/env pwsh
# 详细分析置信度分布

$researchDir = "$env:LOCALAPPDATA\IDVBuff\AlignmentResearch"
$cutoffDate = [DateTime]::Parse("2026-08-03T00:00:00Z")

$confidenceRanges = @{
    "0-10%" = 0
    "10-20%" = 0
    "20-30%" = 0
    "30-40%" = 0
    "40-50%" = 0
    "50-60%" = 0
    "60-62%" = 0
    "62-70%" = 0
    "70-80%" = 0
    "80-82%" = 0
    "82-90%" = 0
    "90-100%" = 0
}

$allConfidences = @()

Write-Host "Analyzing confidence distribution..." -ForegroundColor Cyan

Get-ChildItem -Path $researchDir -Filter "attempts.jsonl" -Recurse | ForEach-Object {
    $lines = Get-Content $_.FullName -ErrorAction SilentlyContinue
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $attempt = $line | ConvertFrom-Json
            $observedAt = [DateTime]::Parse($attempt.observedAt)
            if ($observedAt -lt $cutoffDate) { continue }

            $conf = $attempt.confidence
            $allConfidences += $conf

            if ($conf -lt 0.10) { $confidenceRanges["0-10%"]++ }
            elseif ($conf -lt 0.20) { $confidenceRanges["10-20%"]++ }
            elseif ($conf -lt 0.30) { $confidenceRanges["20-30%"]++ }
            elseif ($conf -lt 0.40) { $confidenceRanges["30-40%"]++ }
            elseif ($conf -lt 0.50) { $confidenceRanges["40-50%"]++ }
            elseif ($conf -lt 0.60) { $confidenceRanges["50-60%"]++ }
            elseif ($conf -lt 0.62) { $confidenceRanges["60-62%"]++ }
            elseif ($conf -lt 0.70) { $confidenceRanges["62-70%"]++ }
            elseif ($conf -lt 0.80) { $confidenceRanges["70-80%"]++ }
            elseif ($conf -lt 0.82) { $confidenceRanges["80-82%"]++ }
            elseif ($conf -lt 0.90) { $confidenceRanges["82-90%"]++ }
            else { $confidenceRanges["90-100%"]++ }
        } catch { }
    }
}

$total = $allConfidences.Count

Write-Host "`n========================================================"
Write-Host "  Detailed Confidence Distribution"
Write-Host "========================================================"

$cumulative = 0
foreach ($range in @("0-10%", "10-20%", "20-30%", "30-40%", "40-50%", "50-60%", "60-62%", "62-70%", "70-80%", "80-82%", "82-90%", "90-100%")) {
    $count = $confidenceRanges[$range]
    $cumulative += $count
    $pct = if ($total -gt 0) { [Math]::Round($count * 100.0 / $total, 1) } else { 0 }
    $cumulativePct = if ($total -gt 0) { [Math]::Round($cumulative * 100.0 / $total, 1) } else { 0 }

    $color = "White"
    if ($range -eq "82-90%" -or $range -eq "90-100%") { $color = "Green" }
    elseif ($range -eq "62-70%" -or $range -eq "70-80%" -or $range -eq "80-82%") { $color = "Yellow" }
    elseif ($count -gt 100) { $color = "Red" }

    $bar = "#" * [Math]::Min(50, [Math]::Floor($pct))
    Write-Host ("  {0,-10} : {1,4} ({2,5}%) {3}" -f $range, $count, $pct, $bar) -ForegroundColor $color
}

if ($allConfidences.Count -gt 0) {
    $avg = [Math]::Round(($allConfidences | Measure-Object -Average).Average, 3)
    $median = [Math]::Round(($allConfidences | Sort-Object)[[Math]::Floor($allConfidences.Count / 2)], 3)
    $p25 = [Math]::Round(($allConfidences | Sort-Object)[[Math]::Floor($allConfidences.Count * 0.25)], 3)
    $p75 = [Math]::Round(($allConfidences | Sort-Object)[[Math]::Floor($allConfidences.Count * 0.75)], 3)

    Write-Host "`nStatistics:"
    Write-Host "  Average: $avg"
    Write-Host "  Median: $median"
    Write-Host "  25th percentile: $p25"
    Write-Host "  75th percentile: $p75"
}

Write-Host "`n========================================================"
Write-Host "Analysis complete!" -ForegroundColor Green
