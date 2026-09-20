# 测试脚本：验证侧门扫描的缩放值传递
# 用法：启动应用后，在侧门场景下触发识别，检查日志输出

Write-Host "侧门扫描缩放值传递测试" -ForegroundColor Cyan
Write-Host ""
Write-Host "预期行为："
Write-Host "1. 侧门扫描阶段："
Write-Host "   - 日志：'侧门扫描 XX#1f · 150ms'"
Write-Host "   - 多尺度探索找到最佳缩放（例如 0.85）"
Write-Host ""
Write-Host "2. 对齐阶段（修复后）："
Write-Host "   - 日志：'门检测完成 · 模式 WarmScaleSearch'"
Write-Host "   - 日志：'scalesEvaluated: 7'（窄带搜索）"
Write-Host "   - 找到的缩放值与扫描阶段一致（±1%）"
Write-Host ""
Write-Host "3. 单门验证："
Write-Host "   - 日志：'单门身份识别成功'"
Write-Host "   - scaleAgreement > 0.90"
Write-Host "   - 最终置信度 > 0.65"
Write-Host ""
Write-Host "如果看到以下情况，说明修复未生效：" -ForegroundColor Yellow
Write-Host "   - 门检测模式为 'FullSearch'（全量搜索）"
Write-Host "   - scalesEvaluated > 15"
Write-Host "   - scaleAgreement < 0.50"
Write-Host "   - 对齐被拒绝（置信度不足）"
Write-Host ""
Write-Host "测试步骤：" -ForegroundColor Green
Write-Host "1. 启用侧门扫描策略（设置页面）"
Write-Host "2. 进入游戏，只露出侧门（不要同时显示大门）"
Write-Host "3. 触发地图识别（全局热键）"
Write-Host "4. 确认地图后，查看日志输出"
Write-Host ""
Write-Host "日志位置：%LOCALAPPDATA%\IDVBuff\Logs\" -ForegroundColor Cyan
