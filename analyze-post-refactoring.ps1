#!/usr/bin/env pwsh
# 分析重构后的对齐数据（2026-08-03及以后）

$researchDir = "$env:LOCALAPPDATA\IDVBuff\AlignmentResearch"
$cutoffDate = [DateTime]::Parse("2026-08-03T00:00:00Z")

$total = 0
$accepted = 0
$rejected = 0
$high = 0
$medium = 0
$low = 0

Write-Host "Analyzing post-refactoring data (2026-08-03+)..." -ForegroundColor Cyan

Get-ChildItem -Path $researchDir -Filter "attempts.jsonl" -Recurse | ForEach-Object {
    $lines = Get-Content $_.FullName -ErrorAction SilentlyContinue
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $attempt = $line | ConvertFrom-Json
            $observedAt = [DateTime]::Parse($attempt.observedAt)

            # 只统计8月3日及之后的数据
            if ($observedAt -lt $cutoffDate) { continue }

            $total++
            if ($attempt.accepted) { $accepted++ } else { $rejected++ }

            $conf = $attempt.confidence
            if ($conf -ge 0.82) { $high++ }
            elseif ($conf -ge 0.62) { $medium++ }
            else { $low++ }
        } catch { }
    }
}

Write-Host "`n========================================================"
Write-Host "  Post-Refactoring Analysis (2026-08-03+)"
Write-Host "========================================================"
Write-Host "`nTotal attempts: $total"
Write-Host "  Accepted: $accepted" -ForegroundColor Green
Write-Host "  Rejected: $rejected" -ForegroundColor Red

if ($total -gt 0) {
    $passRate = [Math]::Round($accepted * 100.0 / $total, 2)
    Write-Host "`n========================================================"
    $color = if ($passRate -ge 95) { "Green" } elseif ($passRate -ge 90) { "Yellow" } else { "Red" }
    Write-Host "  Overall Pass Rate: $passRate%" -ForegroundColor $color

    if ($passRate -ge 95) {
        Write-Host "  STATUS: TARGET ACHIEVED (95%+)" -ForegroundColor Green
    } elseif ($passRate -ge 90) {
        Write-Host "  STATUS: NEAR TARGET (90-95%)" -ForegroundColor Yellow
    } else {
        Write-Host "  STATUS: BELOW TARGET (<90%)" -ForegroundColor Red
    }
    Write-Host "========================================================"

    Write-Host "`nConfidence Distribution:"
    $highPct = [Math]::Round($high * 100.0 / $total, 1)
    $medPct = [Math]::Round($medium * 100.0 / $total, 1)
    $lowPct = [Math]::Round($low * 100.0 / $total, 1)
    Write-Host "  High (82%+): $high ($highPct%)" -ForegroundColor Green
    Write-Host "  Medium (62-82%): $medium ($medPct%)" -ForegroundColor Yellow
    Write-Host "  Low (<62%): $low ($lowPct%)" -ForegroundColor Red

    # 对比基线
    $baselinePassRate = 73.17
    $improvement = $passRate - $baselinePassRate
    $improvementPct = [Math]::Round($improvement, 2)

    Write-Host "`nComparison to Baseline (pre-refactoring):"
    Write-Host "  Baseline: $baselinePassRate%"
    Write-Host "  Current:  $passRate%"
    if ($improvement -gt 0) {
        Write-Host "  Improvement: +$improvementPct%" -ForegroundColor Green
    } else {
        Write-Host "  Change: $improvementPct%" -ForegroundColor Red
    }
} else {
    Write-Host "`nNo post-refactoring data found!" -ForegroundColor Red
}

Write-Host "`nAnalysis complete!" -ForegroundColor Green
