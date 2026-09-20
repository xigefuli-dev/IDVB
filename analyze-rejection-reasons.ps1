#!/usr/bin/env pwsh
# 深入分析拒绝原因

$researchDir = "$env:LOCALAPPDATA\IDVBuff\AlignmentResearch"
$cutoffDate = [DateTime]::Parse("2026-08-03T00:00:00Z")

$rejections = @{}
$lowConfAccepted = 0
$lowConfRejected = 0
$medConfAccepted = 0
$medConfRejected = 0
$highConfAccepted = 0
$highConfRejected = 0

Write-Host "Analyzing rejection reasons (post-refactoring)..." -ForegroundColor Cyan

Get-ChildItem -Path $researchDir -Filter "attempts.jsonl" -Recurse | ForEach-Object {
    $lines = Get-Content $_.FullName -ErrorAction SilentlyContinue
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $attempt = $line | ConvertFrom-Json
            $observedAt = [DateTime]::Parse($attempt.observedAt)
            if ($observedAt -lt $cutoffDate) { continue }

            $conf = $attempt.confidence
            $accepted = $attempt.accepted

            # 按置信度分组
            if ($conf -ge 0.82) {
                if ($accepted) { $highConfAccepted++ } else { $highConfRejected++ }
            } elseif ($conf -ge 0.62) {
                if ($accepted) { $medConfAccepted++ } else { $medConfRejected++ }
            } else {
                if ($accepted) { $lowConfAccepted++ } else { $lowConfRejected++ }
            }

            # 收集拒绝原因
            if (-not $accepted) {
                $reason = $attempt.failureReason
                if ([string]::IsNullOrWhiteSpace($reason)) { $reason = "Unknown" }
                if ($rejections.ContainsKey($reason)) {
                    $rejections[$reason]++
                } else {
                    $rejections[$reason] = 1
                }
            }
        } catch { }
    }
}

Write-Host "`n========================================================"
Write-Host "  Confidence vs Acceptance Analysis"
Write-Host "========================================================"

$totalHigh = $highConfAccepted + $highConfRejected
$totalMed = $medConfAccepted + $medConfRejected
$totalLow = $lowConfAccepted + $lowConfRejected

if ($totalHigh -gt 0) {
    $highRate = [Math]::Round($highConfAccepted * 100.0 / $totalHigh, 1)
    Write-Host "`nHigh Confidence (82%+): $totalHigh attempts"
    Write-Host "  Accepted: $highConfAccepted ($highRate%)" -ForegroundColor Green
    Write-Host "  Rejected: $highConfRejected" -ForegroundColor Red
}

if ($totalMed -gt 0) {
    $medRate = [Math]::Round($medConfAccepted * 100.0 / $totalMed, 1)
    Write-Host "`nMedium Confidence (62-82%): $totalMed attempts"
    Write-Host "  Accepted: $medConfAccepted ($medRate%)" -ForegroundColor Green
    Write-Host "  Rejected: $medConfRejected" -ForegroundColor Red
}

if ($totalLow -gt 0) {
    $lowRate = [Math]::Round($lowConfAccepted * 100.0 / $totalLow, 1)
    Write-Host "`nLow Confidence (<62%): $totalLow attempts"
    Write-Host "  Accepted: $lowConfAccepted ($lowRate%)" -ForegroundColor Yellow
    Write-Host "  Rejected: $lowConfRejected" -ForegroundColor Red
}

Write-Host "`n========================================================"
Write-Host "  Top 10 Rejection Reasons"
Write-Host "========================================================"

$sortedReasons = $rejections.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 10
$totalRejections = ($rejections.Values | Measure-Object -Sum).Sum

foreach ($reason in $sortedReasons) {
    $pct = [Math]::Round($reason.Value * 100.0 / $totalRejections, 1)
    $reasonText = $reason.Key
    if ($reasonText.Length -gt 100) {
        $reasonText = $reasonText.Substring(0, 97) + "..."
    }
    Write-Host "`n[$($reason.Value) times, $pct%]"
    Write-Host "  $reasonText" -ForegroundColor Yellow
}

Write-Host "`n========================================================"
Write-Host "Analysis complete!" -ForegroundColor Green
