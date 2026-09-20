#!/usr/bin/env pwsh
# 简化版对齐数据分析

$researchDir = "$env:LOCALAPPDATA\IDVBuff\AlignmentResearch"
$total = 0
$accepted = 0
$rejected = 0
$high = 0
$medium = 0
$low = 0

Write-Host "Analyzing AlignmentResearch data..." -ForegroundColor Cyan

Get-ChildItem -Path $researchDir -Filter "attempts.jsonl" -Recurse | ForEach-Object {
    $lines = Get-Content $_.FullName -ErrorAction SilentlyContinue
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $attempt = $line | ConvertFrom-Json
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
Write-Host "  Alignment Attempts Analysis (Pre-Refactoring)"
Write-Host "========================================================"
Write-Host "`nTotal attempts: $total"
Write-Host "  Accepted: $accepted" -ForegroundColor Green
Write-Host "  Rejected: $rejected" -ForegroundColor Red

$passRate = if ($total -gt 0) { [Math]::Round($accepted * 100.0 / $total, 2) } else { 0 }
Write-Host "`n========================================================"
Write-Host "  Overall Pass Rate: $passRate%" -ForegroundColor $(if ($passRate -ge 95) { "Green" } else { "Yellow" })
Write-Host "========================================================"

Write-Host "`nConfidence Distribution:"
if ($total -gt 0) {
    $highPct = [Math]::Round($high * 100.0 / $total, 1)
    $medPct = [Math]::Round($medium * 100.0 / $total, 1)
    $lowPct = [Math]::Round($low * 100.0 / $total, 1)
    Write-Host "  High (82%+): $high ($highPct%)" -ForegroundColor Green
    Write-Host "  Medium (62-82%): $medium ($medPct%)" -ForegroundColor Yellow
    Write-Host "  Low (<62%): $low ($lowPct%)" -ForegroundColor Red
}

Write-Host "`nAnalysis complete!" -ForegroundColor Green
