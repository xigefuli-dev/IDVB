# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

Identity Vision Bridge（简称 **IDVB**）是一个 **WinUI 3 桌面应用**，为游戏《第五人格》(Identity V) 提供实时地图叠加层。它通过 OpenCV 计算机视觉识别游戏画面中的地图，并在游戏窗口上方渲染一个透明、可穿透点击的原生图层，显示对齐后的地图。

## 项目身份与命名约定

- 全称：**Identity Vision Bridge**
- 缩写：**IDVB**
- 数据包格式：**IDVM（Identity Vision Model）**，文件扩展名为 `.idvm`
- `IDVBuff` 命名空间、程序集名、项目目录名和现有本地数据目录是代码兼容标识；本次品牌更新不重命名它们。
- 新增或修改的用户可见文本应使用 Identity Vision Bridge 或 IDVB，不再使用 IDV Buff 作为产品名称。

## GitHub 推送内容约束

- 每次推送到 GitHub 前，必须先核对最终提交内容；推送结果只能包含项目所需的源代码及明确属于项目交付物的必要文件。
- 严禁将任何无关的 `.ps1` 脚本、Inno Setup 文件、Markdown 文档、无关构建产物、临时文件、缓存、日志、调试输出或测试专用代码推送到 GitHub。
- 测试、验证、分析和构建过程中产生的专用文件，除非明确确认属于正式项目源码或交付物，否则一律不得包含在推送中。
- 推送前必须使用 Git 检查暂存区和提交文件清单，并在发现不属于本次代码变更的文件时移出暂存区；不能仅依赖 `.gitignore` 或构建成功来判断推送内容正确。

## 技术栈

- **.NET 10** (`net10.0-windows10.0.19041.0`)，仅 x64
- **WinUI 3 / Windows App SDK** (`Microsoft.WindowsAppSDK 1.*`)
- **OpenCVSharp4** (`4.13.0`) — 门模板匹配、结构配准、楼层指示器识别
- **xUnit 2.9.3** — 测试框架
- 语言版本 `latest`，开启 `Nullable` 与 `ImplicitUsings`

## 构建与运行

```bash
# 构建主项目（WinUI 3 应用）
dotnet build IDVBuff.csproj

# 构建整个解决方案（含 Core / Infrastructure / Services / Pipeline / 模块契约 / 测试 / Probe）
dotnet build IDVBuff.slnx

# 运行测试（全部）
dotnet test IDVBuff.Tests\IDVBuff.Tests.csproj

# 运行单个测试
dotnet test IDVBuff.Tests\IDVBuff.Tests.csproj --filter "FullyQualifiedName~MapModelsTests"

# 构建 RealCLI 集成测试 CLI（独立于 slnx，csproj 自带 RuntimeIdentifier win-x64）
dotnet build IDVBuff.RealCLI\IDVBuff.RealCLI.csproj

# 单张截图识别（走完整 SessionOrchestrator 管线；截图经 FileBasedCapture 从文件读取）
IDVB.RealCLI.exe run --image FILE [--out RESULT.json] [--settings DIR]

# 批量评估对齐准确率
IDVB.RealCLI.exe batch --files "samples/**/*.png" [--parallel N] [--out SUMMARY.json]

# 查看 RealCLI 全部命令与参数
IDVB.RealCLI.exe --help
```

### 构建号计数约定

- 构建号（`bNN.N-YY.MM.DD.NNNN` 中最后四位 `NNNN`）由仓库根目录的 `.idvb-build-count` 计数器驱动，该文件已加入 `.gitignore`，不入库。
- 每次 `dotnet build IDVBuff.csproj`，`Directory.Build.targets` 的 `GenerateIDVBBuildVersion` 目标都会经 `Tools\Generate-IDVBBuildVersion.ps1` 原子递增计数器，并把生成版本号编译进程序集（`BuildVersionInfo.BuildVersion`）。未显式传 `-BuildVersion` 的普通构建即自动 +1。
- 发布构建号由 Agent 自行从计数器读取/递增，**不得手动指定**——不得在 `Build-Release.ps1 -Version`、`release/manifests/*.psd1` 或提交信息中凭空拍号。
- 取下一个构建号并递增：调用 `New-IDVBBuildVersion -ReleaseLine b01.4 -CounterPath <repo>\.idvb-build-count`（原子操作，避免读改写竞态）；或读取计数器文件后按 +1 语义推导。
- 计数器在编译前递增，即使编译因文件锁失败也消耗一个号——正常现象，不必回滚。

## 解决方案结构

主项目 `IDVBuff.csproj` 为 WinUI 3 应用；识别管线已拆分为无 UI 依赖的分层项目。`IDVBuff.RealCLI` 未加入 `.slnx`，需单独构建。

| 项目 | 说明 |
|------|------|
| `IDVBuff.csproj` | 主 WinUI 3 应用，包含 XAML 视图与 `Features/` |
| `Core\` | 无 UI 依赖的核心契约与模型（`Contracts/`、`Models/`） |
| `Infrastructure\` | 基础设施（`Configuration/`、`Imaging/`、`Win32/`） |
| `Services\` | 服务层（`Alignment/`、`Capture/`、`Detection/`、`Input/`、`Overlay/`、`Recognition/`、`Repository/` 等） |
| `Pipeline\` | 识别管线（`PipelineFactory`、`PipelineOrchestrator`、`ScanPipelineContext`、`Stages/`） |
| `IDVBuff.ModuleContracts\` | 无 UI 依赖的共享契约，只有 `IAppModule` 接口 |
| `IDVBuff.Tests\` | xUnit 测试，通过 `<Compile Include="..\Features\..." Link="..." />` 链接生产代码 |
| `IDVBuff.RealCLI\` | 集成测试 CLI：DI 容器 + Stub IO 驱动完整 `SessionOrchestrator`（真实测试，非绕开管线的谎言测试）；链接 `Features/Maps/**/*.cs` 源文件 |
| `IDVBuff.MapAlignment.Probe\` | **已废弃**——早期对齐探针（绕过 `SessionOrchestrator` 自行拼装算法组件），由 RealCLI 取代，不再维护 |

`.slnx` 是新的 Visual Studio 解决方案格式（17.10+）。

## 架构核心概念

### 模块系统

应用使用**可插拔模块架构**。`Modules/ModuleRegistration.cs` 是唯一的组合根：

- `IAppModule`（定义在 `IDVBuff.ModuleContracts`）——无框架依赖的契约，三个成员：`Id`、`DisplayName`、`IconKey`、`CreateView()`
- `ModuleCatalog` 持有已注册模块的有序列表，`ModuleRegistration.CreateCatalog()` 在此注册内置模块
- `NavigationNode` 定义导航树的层级结构；`NavigationEntry.CreateRoots()` 保留真实父子关系和折叠状态
- `MainPage.xaml` 左侧使用嵌套 `ItemsControl` 与按钮渲染导航，右侧 `ContentPresenter` 承载模块视图；不要重新引入 `ListView` 的默认选择效果
- 接入第三方 C# 项目时遵循 `MODULES.md` 中的三步流程

### 核心功能：地图识别与叠加 (`Features/Maps/`)

这是应用的核心——约 30 个文件，构成了一个实时 CV 识别管线：

**识别流程**（由 `MapRuntimeService` 编排）：
1. 全局热键或游戏内地图打开触发扫描
2. `DwrGameWindowCaptureService` 截取 `dwrg.exe` 窗口的指定区域
3. `FloorIndicatorRecognizer` 通过模板匹配判断 1F/2F
4. `GateTemplateDetector` 检测门图标（大门 + 侧门）
5. `MapCvRecognitionScript.RankGeometry()` 将门坐标与所有已注册地图的指纹逐一比对，按向量误差排名
6. `MapStructureRegistrar` 通过静态结构（边缘）配准复核门对齐结果
7. `MapOverlayWindow` 通过 `MapOverlayNativeWindow`（原生分层窗口，`WS_EX_LAYERED` + `WS_EX_TRANSPARENT`）渲染叠加地图
8. `MapPlayerMarkerDetector` 追踪玩家位置标记

**关键服务**：
- `MapRuntimeService` — 总编排器，管理会话生命周期、输入绑定、识别管线
- `MapRuntimeHost` — 静态单例持有者，在 `App.OnLaunched` 中初始化并绑定到 `DispatcherQueue`
- `MapCvRecognitionService` — 应用生命周期的门检测器与几何识别器，缓存地图指纹
- `MapRepository` — 从 `%LOCALAPPDATA%\IDVBuff\Maps\maps.json` 读写地图目录
- `MapRuntimeSettingsRepository` — 持久化运行设置（校准区域、热键绑定、调优参数）

**识别来源优先级**：手动框选 > 双门完整对齐 > 单门跟踪 > 辅助锚点跟踪 > 结构配准 > 复用上次变换

**会话状态机**：`MapSessionModels.cs` 中的 `MapOpenSession` 维护一个严格的状态机：`Closed → OpeningDetected → WaitingForStableFrames → IdentifyingMap → CoarseLocating → FineLocating → Confirming → Locked`，中间可转换到 `LowConfidence` / `RecalibrationRequired` / `Lost`。

**对齐追踪模式**：`MapAlignmentTrackingMode` — `None` / `NeedsGatePair` / `GatePairLocked` / `SingleGateTracking` / `StructureMatched` / `HoldingLastTransform` 等

### 数据存储

- 地图目录：`%LOCALAPPDATA%\IDVB\Maps\maps.json` — `MapCatalogDocument`（含 `List<MapRecord>`）
- 运行设置：`%LOCALAPPDATA%\IDVB\MapRuntime\settings.json` — `MapRuntimeSettings`
- 每张地图包含两张原始图片（一楼/二楼）、识别配置文件（`MapRecognitionProfile`，含锚点坐标、识别区域等），版本化迁移（当前 SchemaVersion = 4）

### 叠加渲染

`MapOverlayWindow` 使用双层渲染：
- **背景层**（锁定）：地图图片 + 状态文字，缓存为 `Bitmap`，仅在识别结果变化时重新渲染
- **动态层**（每帧合成）：玩家标记等频繁变化元素，通过 `MapOverlayBitmapRenderer.ComposeDynamic()` 叠加
- 使用 `SetLayeredWindowAttributes` 或 `UpdateLayeredWindow` 实现像素级透明和点击穿透

### 测试策略

- 测试项目通过 `Link` 直接编译生产源文件（非项目引用）——这是有意为之，让测试访问 `internal` 类型
- 测试覆盖：地图模型、仓库、视口稳定性、门检测、楼层识别、玩家标记检测、几何识别、结构配准、覆盖渲染、覆盖窗口样式
- 测试中的资产文件（`Gate.png`、`1F.png` 等）通过 `CopyToOutputDirectory` 复制
- `IDVBuff.RealCLI` 是集成测试 CLI（走完整 `SessionOrchestrator` 管线），**不是**自动化测试套件；它的 `batch` 命令可以批量评估对齐准确率

## Windows App SDK / Windows App Runtime 使用边界（禁止乱用）

Windows App Runtime 通过 NuGet 包 `Microsoft.WindowsAppSDK` 引入。**只有真正的 WinUI 3 项目才允许引用它**；无 UI 依赖的分层项目一律不得引用，否则会把 XAML/控件运行时拖进与 UI 无关的层。

**允许引用 `Microsoft.WindowsAppSDK` 的项目（仅限以下两个 + 一个明确的借用点）：**

- `IDVBuff.csproj` — 主应用，WinUI 3（`UseWinUI=true`），`WindowsAppSDKSelfContained=true` 自包含打包 runtime，不依赖用户机器上单独安装的 Windows App Runtime
- `Survey\Editor\IDVBuff.Survey.Editor.WinUI.csproj` — 测绘编辑器，WinUI 3，随主应用发布
- `IDVBuff.RealCLI\IDVBuff.RealCLI.csproj` — **唯一的非 WinUI 例外**：纯控制台应用引用 WindowsAppSDK 仅为在 `Program.cs` 中用 `DispatcherQueueController.CreateOnCurrentThread()` 创建同步调度器（无 WinUI 消息泵），不渲染任何 XAML。新增此类借用必须在注释中说明理由

**禁止引用 `Microsoft.WindowsAppSDK` 的项目：**

- `Core` / `Infrastructure` / `Services` / `Pipeline` / `IDVBuff.ModuleContracts` / `UpdateCore`
- `Survey` 下除 `Editor` 外的全部子项目（Domain / Contracts / Application / Persistence / Preprocessing / Registration / PoseGraph / Fusion / Idvm）
- `Updater\IDVBuff.Updater.csproj` — WinForms 独立更新器，必须保持**不依赖 Windows App Runtime**（`WindowsAppSDKSelfContained=false`、无 WindowsAppSDK 引用），由 `UpdateReleasePolicyTests.IndependentUpdaterDoesNotDependOnWindowsAppRuntime` 守护

**接入新项目的规则：** 非 WinUI 3 项目不得新增 `Microsoft.WindowsAppSDK` 引用；确需 `DispatcherQueue` 时，沿用 RealCLI 的 `DispatcherQueueController` 模式并注明理由。

## 关键注意事项

- 每个楼层的缩放尺度必须完全独立。1F 和 2F 不得复用或相互推导 scale seed、缩放缓存、对齐 transform 或楼层尺寸换算结果；VPSG 和全尺度扫描都只能使用目标楼层自身的会话/缓存，否则从目标楼层的中性种子独立估算。
- 由于 WinUI 3 / MSIX 工具链的复杂性，**不要在 Visual Studio 之外修改 `.csproj` 中的 `<ProjectCapability Include="Msix"/>`** 部分
- `MapOverlayNativeWindow` 是原生 Win32 分层窗口——修改渲染逻辑时必须保持 `WS_EX_TRANSPARENT` 行为，否则会阻挡游戏鼠标输入
- 地图目录 `maps.json` 的手动编辑应以迁移脚本（如 `Tools/MigrateCurrentMapRegionsV3.ps1`）为模板，确保 `SchemaVersion` 和 `UpdatedAt` 正确更新
- 全局热键通过 `MapGlobalInputService` 注册——`IsModifierKey()` 明确排除修饰键单独绑定
- `MapRuntimeService.DisposeAsync()` 的关闭顺序是精心编排的（先取消令牌 → 等待活跃操作排空 → 释放扫描门 → 释放子资源），修改时必须保持此顺序
- `MapCvRecognitionService` 中 `_structureCache`（`MapStructureReferenceCache`）会缓存预处理后的参考图像到磁盘以加速后续匹配——如有预处理逻辑变更，需清空该缓存目录
