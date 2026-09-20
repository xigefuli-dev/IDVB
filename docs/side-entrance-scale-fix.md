# 侧门扫描尺度一致性修复报告

## 问题描述

置信度系统重构后，侧门扫描策略无法正确对齐的问题复现。表现为：侧门扫描能识别地图，但后续对齐阶段失败，置信度不足。

## 根本原因

置信度重构引入了 `scaleAgreement` 检查，但侧门扫描的缩放值没有正确传递到对齐阶段的门检测器。

### 重构前的逻辑（正常工作）

单门置信度计算**没有**尺度一致性检查：

```csharp
// 重构前：MapCvRecognitionScript.cs
public static double ComputeSingleGateTrackingConfidence(
    double gateScore,
    double lockedSessionConfidence,
    double trackingWeight = 0.6d)
```

### 重构后的逻辑（引入问题）

新的置信度计算**增加了** `scaleAgreement` 参数（占 10% 权重）：

```csharp
// 重构后：MapAlignmentConfidence.cs
public static double ComputeSideEntranceSingleGateConfidence(
    double sideEntrancePrior,    // 40%
    double gateScore,             // 50%
    double scaleAgreement)        // 10% ← 新增！
```

其中 `scaleAgreement` 的计算依赖于门检测找到的缩放值：

```csharp
var scaleAgreement = MapAlignmentConfidence.ComputeScaleAgreement(
    gate.Scale,                    // 对齐阶段门检测找到的缩放
    session.BaselineGateScale);    // 扫描阶段侧门特征的缩放
```

### 问题链条

1. **扫描阶段**：`SideEntranceScanPipeline` 通过多尺度探索找到缩放值 A（例如 0.85）
2. **对齐阶段**：`AlignSelected` 被调用时 `alignmentSearchContext: null`
3. 门检测器使用默认的 `GateSearchMode.FullSearch`，重新全量搜索
4. 全量搜索可能找到不同的缩放档位 B（例如 0.92）
5. **置信度计算**：`ComputeScaleAgreement(0.92, 0.85)` → deviation = 8.2% → scaleAgreement = 0.32
6. 最终置信度被拉低，无法达到 `MinimumConfidence` 阈值，对齐被拒绝

### 为什么会"完全失效"

不是找不到门，而是**找到的门缩放值与侧门扫描不一致**，导致新引入的 `scaleAgreement` 检查失败，最终置信度不足被拒绝。

## 修复方案

在侧门扫描后调用 `AlignSelected` 时，构造 `AlignmentSearchContext`，指定使用 `WarmScaleSearch` 模式，并将侧门扫描得到的缩放值作为 `WarmScale` 传递给门检测器。

### 关键洞察

`ComputeScaleAgreement` 的容差曲线：

```csharp
// 在12%以内线性衰减
if (deviation <= 0.12d)
    return 1d - (deviation / 0.12d);
```

示例：
- deviation 0% → scaleAgreement = 1.00
- deviation 1% → scaleAgreement = 0.92
- deviation 5% → scaleAgreement = 0.58
- deviation 10% → scaleAgreement = 0.17
- deviation 12%+ → scaleAgreement < 0

如果门检测的全量搜索找到了相差 8% 以上的缩放值，`scaleAgreement` 会降到 0.33 以下，导致最终置信度不足（通常需要 0.65+）。

### 修复代码（MapRuntimeService.cs:1648）

```csharp
// 侧门扫描已通过多尺度探索确定了准确的缩放值，
// 将其作为 warm scale 传递给门检测器，避免全量搜索找到不一致的缩放。
AlignmentSearchContext? sideEntranceSearchContext = null;
if (usesGateAlignment && sideSeed.BaselineGateScale > 0d)
{
    sideEntranceSearchContext = new AlignmentSearchContext
    {
        GateSearch = new GateSearchContext
        {
            Mode = GateSearchMode.WarmScaleSearch,  // 窄带搜索
            WarmScale = sideSeed.BaselineGateScale, // 侧门扫描的缩放值
            AllowSingleGateEarlyExit = true,
            SingleGateScoreThreshold = GateTemplateRules.EarlyExitScoreThreshold,
            SingleGateScaleTolerance = GateTemplateRules.SingleGateScaleTolerance,
            AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap,
        }
    };
    if (tuning.WarmGateSearchBudgetMs > 0)
        sideEntranceSearchContext.GateSearch.TimeBudgetMilliseconds =
            tuning.WarmGateSearchBudgetMs;
}

var attempt = await Task.Run(
    () => usesGateAlignment
        ? _recognition.AlignSelected(
            // ...
            alignmentSearchContext: sideEntranceSearchContext,  // ← 传递上下文
            // ...
```

### 修复效果

**修复前**：
- 门检测：`FullSearch` 模式，~20+ 缩放档位
- 找到缩放：0.92（与侧门扫描的 0.85 不一致）
- `scaleAgreement`：0.32（8.2% deviation）
- 最终置信度：不足，对齐被拒绝

**修复后**：
- 门检测：`WarmScaleSearch` 模式，~7 缩放档位（围绕 0.85）
- 找到缩放：0.86（与侧门扫描的 0.85 一致）
- `scaleAgreement`：0.92（1.2% deviation）
- 最终置信度：足够，对齐成功

## 验证方法

1. 启用侧门扫描策略
2. 在只有侧门可见的场景下触发地图识别
3. 检查日志输出：
   ```
   门检测完成 · 模式 WarmScaleSearch · 找到 1 个候选
   单门身份识别成功 · scaleAgreement: 0.92
   置信度: 0.78 (通过)
   ```

## 相关代码

- `Features/Maps/MapRuntimeService.cs:1648` - 修复点
- `Features/Maps/MapAlignmentConfidence.cs:101-113` - 侧门单门置信度计算
- `Features/Maps/MapAlignmentConfidence.cs:223-244` - 尺度一致性计算
- `Features/Maps/MapCvRecognitionService.cs:819-843` - 单门处理逻辑

## 置信度重构的影响

置信度重构（commit 3104353）引入了更精确的置信度计算，包括：

1. **场景分离**：不同对齐场景使用独立的置信度公式
2. **新增检查**：单门跟踪增加了 `scaleAgreement` 检查（10% 权重）
3. **语义清晰**：每个参数有明确的语义和权重

这些改进提高了置信度的准确性，但也要求**缩放值在整个流程中保持一致**。侧门扫描场景下，如果对齐阶段的门检测没有使用扫描阶段的缩放值，就会触发 `scaleAgreement` 检查失败。

## 总结

这个问题的本质是：**置信度重构引入了更严格的一致性检查，暴露了侧门扫描流程中缩放值传递不完整的问题**。

修复通过显式传递 `WarmScaleSearch` 上下文，确保对齐阶段的门检测使用与扫描阶段一致的缩放基准，从而满足新的置信度计算要求。

## 修复方案

在侧门扫描后调用 `AlignSelected` 时，构造一个 `AlignmentSearchContext`，指定使用 `WarmScaleSearch` 模式，并将侧门扫描得到的缩放值作为 `WarmScale`：

```csharp
// 侧门扫描已通过多尺度探索确定了准确的缩放值，
// 将其作为 warm scale 传递给门检测器，避免重复全量搜索。
AlignmentSearchContext? sideEntranceSearchContext = null;
if (usesGateAlignment && sideSeed.BaselineGateScale > 0d)
{
    sideEntranceSearchContext = new AlignmentSearchContext
    {
        GateSearch = new GateSearchContext
        {
            Mode = GateSearchMode.WarmScaleSearch,
            WarmScale = sideSeed.BaselineGateScale,
            AllowSingleGateEarlyExit = true,
            SingleGateScoreThreshold = GateTemplateRules.EarlyExitScoreThreshold,
            SingleGateScaleTolerance = GateTemplateRules.SingleGateScaleTolerance,
            AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap,
        }
    };
    if (tuning.WarmGateSearchBudgetMs > 0)
        sideEntranceSearchContext.GateSearch.TimeBudgetMilliseconds =
            tuning.WarmGateSearchBudgetMs;
}
```

## 验证方法

1. 启用侧门扫描策略
2. 在只有侧门可见的场景下触发地图识别
3. 检查日志中的门检测模式：
   - 修复前：`门检测完成 · 模式 FullSearch`（全量搜索，~20+ 尺度）
   - 修复后：`门检测完成 · 模式 WarmScaleSearch`（窄带搜索，~7 尺度）
4. 验证对齐成功率和置信度

## 相关文件

- `Features/Maps/MapRuntimeService.cs:1648-1683` - 修复点
- `Features/Maps/SideEntranceScanPipeline.cs` - 侧门扫描管线（多尺度探索实现）
- `Features/Maps/GateTemplateDetector.cs` - 门检测器（支持不同搜索模式）
- `Features/Maps/MapCvRecognitionService.cs:398-460` - AlignSelected 方法

## 注意事项

此修复确保了侧门扫描的多尺度探索结果能正确传递到对齐阶段，恢复了侧门策略的完整语义：

1. 扫描阶段：多尺度探索 → 确定地图身份 + 初始缩放
2. 对齐阶段：使用已知缩放的窄带搜索 → 精确定位

这与置信度系统的设计一致：侧门扫描已解决"这是哪张地图"和"初始缩放是多少"，对齐阶段只需解决"视口在哪里"。
