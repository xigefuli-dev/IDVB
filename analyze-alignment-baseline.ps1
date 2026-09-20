#!/usr/bin/env pwsh
# 分析对齐研究数据，统计重构前的通过率

$researchDir = "$env:LOCALAPPDATA\IDVBuff\AlignmentResearch"
$allAttempts = @()
$totalAttempts = 0
$accepted = 0
$rejected = 0
$highConfidence = 0
$mediumConfidence = 0
$lowConfidence = 0

Write-Host "正在分析 AlignmentResearch 数据..." -ForegroundColor Cyan

Get-ChildItem -Path $researchDir -Filter "attempts.jsonl" -Recurse | ForEach-Object {
    $file = $_.FullName
    $lines = Get-Content $file -ErrorAction SilentlyContinue

    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        try {
            $attempt = $line | ConvertFrom-Json
            $totalAttempts++

            if ($attempt.accepted) {
                $accepted++
            } else {
                $rejected++
            }

            $conf = $attempt.confidence
            if ($conf -ge 0.82) {
                $highConfidence++
            } elseif ($conf -ge 0.62) {
                $mediumConfidence++
            } else {
                $lowConfidence++
            }

            $allAttempts += [PSCustomObject]@{
                AttemptId = $attempt.attemptId
                ObservedAt = $attempt.observedAt
                MapId = $attempt.mapId
                FloorKey = $attempt.floorKey
                Confidence = $attempt.confidence
                Accepted = $attempt.accepted
                IsHighConfidence = $attempt.isHighConfidence
                FailureReason = $attempt.failureReason
                EvidenceKind = $attempt.evidenceKind
                ScaleSeedSource = $attempt.scaleSeedSource
            }
        } catch {
            Write-Warning "解析失败: $($_.Exception.Message)"
        }
    }
}

Write-Host "`n════════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host "  对齐尝试统计（重构前数据）" -ForegroundColor Yellow
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Yellow

Write-Host "`n总尝试次数: $totalAttempts"
$acceptPct = ($accepted/$totalAttempts*100).ToString('F1')
$rejectPct = ($rejected/$totalAttempts*100).ToString('F1')
Write-Host "  ✓ 接受: $accepted ($acceptPct%25)" -ForegroundColor Green
Write-Host "  ✗ 拒绝: $rejected ($rejectPct%25)" -ForegroundColor Red

Write-Host "`n置信度分布:"
$highPct = ($highConfidence/$totalAttempts*100).ToString('F1')
$medPct = ($mediumConfidence/$totalAttempts*100).ToString('F1')
$lowPct = ($lowConfidence/$totalAttempts*100).ToString('F1')
Write-Host "  高置信度 82%25+: $highConfidence ($highPct%25)" -ForegroundColor Green
Write-Host "  中等置信度 62-82%25: $mediumConfidence ($medPct%25)" -ForegroundColor Yellow
Write-Host "  低置信度 62%25-: $lowConfidence ($lowPct%25)" -ForegroundColor Red

$passRate = $accepted / $totalAttempts * 100
$passRateStr = $passRate.ToString('F2')
Write-Host "`n════════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host "  总体通过率: $passRateStr%25" -ForegroundColor $(if ($passRate -ge 95) { "Green" } else { "Yellow" })
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Yellow

# 按拒绝原因分组
if ($rejected -gt 0) {
    Write-Host "`n拒绝原因统计:" -ForegroundColor Cyan
    $rejectionReasons = $allAttempts | Where-Object { -not $_.Accepted } |
        Group-Object -Property FailureReason |
        Sort-Object Count -Descending |
        Select-Object -First 10

    foreach ($reason in $rejectionReasons) {
        $reasonPct = ($reason.Count / $rejected * 100).ToString("F1")
        $reasonText = $reason.Name.Substring(0, [Math]::Min(80, $reason.Name.Length))
        Write-Host "  [$($reason.Count) 次, $reasonPct%25] $reasonText"
    }
}

# 按证据类型分组
Write-Host "`n证据类型分布:" -ForegroundColor Cyan
$evidenceTypes = @{
    0 = "None"
    1 = "DualGate"
    2 = "SingleGate"
    4 = "Structure"
    8 = "Auxiliary"
}

$allAttempts | Group-Object -Property EvidenceKind | Sort-Object Count -Descending | ForEach-Object {
    $typeName = if ($evidenceTypes.ContainsKey($_.Name)) { $evidenceTypes[[int]$_.Name] } else { "Unknown($($_.Name))" }
    $typePct = ($_.Count / $totalAttempts * 100).ToString("F1")
    $acceptedInGroup = ($_.Group | Where-Object { $_.Accepted }).Count
    $groupPassRate = if ($_.Count -gt 0) { ($acceptedInGroup / $_.Count * 100).ToString("F1") } else { "0.0" }
    Write-Host "  $typeName : $($_.Count) 次 ($typePct%25), 通过率 $groupPassRate%25"
}

Write-Host "`n导出详细数据到 alignment-analysis.csv" -ForegroundColor Cyan
$allAttempts | Export-Csv -Path "alignment-analysis.csv" -NoTypeInformation -Encoding UTF8

Write-Host "`n分析完成！" -ForegroundColor Green
