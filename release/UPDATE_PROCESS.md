# Identity Vision Bridge 热更新固定流程

本文与 `Invoke-IDVBRelease.ps1`、`manifests/*.psd1` 共同构成 IDVB 每次更新的固定执行流程。脚本按阶段运行，已有源码快照、通道目录和回执一律不删除、不覆盖；失败后先查明原因，再使用新的公开版本号重新执行。

日常发布统一从 `Invoke-IDVBUpdateWorkflow.ps1` 进入。它编排底层固定阶段，默认只打印外部发布清单；只有显式提供 `-Publish` 才会写入 R2 或创建 GitHub Release。

## 日常 workflow

```powershell
$manifest = '.\release\manifests\本次版本.psd1'

# 查看全部阶段和回执状态
.\release\Invoke-IDVBUpdateWorkflow.ps1 -ManifestPath $manifest -Phase Status

# 构建、打包、验证 test，并打印 R2 上传清单
.\release\Invoke-IDVBUpdateWorkflow.ps1 -ManifestPath $manifest -Phase Test

# 确认清单后发布 test，并回读线上签名 envelope
.\release\Invoke-IDVBUpdateWorkflow.ps1 -ManifestPath $manifest -Phase Test -Publish

# test 与 stable 由发布者选择；可直接生成 stable 上传清单
.\release\Invoke-IDVBUpdateWorkflow.ps1 -ManifestPath $manifest -Phase Stable

# 发布 stable，并回读线上签名 envelope
.\release\Invoke-IDVBUpdateWorkflow.ps1 -ManifestPath $manifest -Phase Stable -Publish

# stable 成功后，把 stable 的同一个 Velopack Setup 发布到 GitHub
.\release\Invoke-IDVBUpdateWorkflow.ps1 -ManifestPath $manifest -Phase GitHub -Publish

# 最终源码与回执审计
.\release\Invoke-IDVBUpdateWorkflow.ps1 -ManifestPath $manifest -Phase Audit
```

workflow 支持按回执续跑：已完成的不可变阶段会跳过，dry-run 后可以使用同一命令加 `-Publish` 继续。test 与 stable 是独立目标，是否先发布 test 由发布者决定；GitHub Release 必须存在 stable 发布回执且目标提交完全一致。

## 架构和信任边界

- 主程序：WinUI 3 `IDVB.exe`，启动时首先执行 Velopack 生命周期入口。
- 更新程序：`Updater/IDVB.Updater.exe`，可由设置页手动打开；正式安装版也会在主程序启动后至多每 24 小时后台检查一次，仅在发现新版本时显示窗口。它负责检查、下载、引导关闭主程序、应用更新和重新启动；无更新或网络暂不可用时不会打扰用户，也不会阻塞主程序启动。
- 更新通道：`win-x64-test` 与 `win-x64-stable`。安装包内的 `update-channel.txt` 决定后续通道；旧 Inno 安装默认进入 stable。发布者可以任选一个通道，stable 不依赖 test。
- 官网下载入口：`https://idvb.xgflee.com/download`。面向用户的后续下载与更新说明统一指向该页面；Windows 安装包和 IDVM 地图包仍由 R2 下载服务提供。
- 更新分发：Cloudflare Worker `download.xgflee.com` 从现有 R2 桶读取 `updates/{channel}/{file}`，该机器接口仅供更新器与发布校验使用。
- 清单签名：离线 ECDSA P-256/SHA-256。Updater 只信任随程序发布的 `release/trust/*.pem` 公钥。
- 自行验证：stable 不要求 Authenticode。更新器只接受内置公钥验证通过的 ECDSA P-256/SHA-256 清单，并逐个校验清单声明的资产文件名、长度和 SHA-256；GitHub 直接复用 stable 的同一个 Velopack Setup，并同时发布 `.sha256`、构建清单和签名 envelope。
- 发布原子性：不可变包和版本化安装器先上传，签名的 `feed-envelope.json` 最后上传。客户端在此之前仍看到上一份完整版本。
- 差分更新：`PackTest` / `PackStable` 会先从对应公开通道读取并验签上一份 feed，下载并校验其最新 full 包，然后由 Velopack 生成指向本次版本的 delta 包。首次发布没有基准包时只生成 full 包；不能生成或应用 delta 时客户端自动回退完整包。
- 差分基准包仍保留在新签名 feed 中，但它是既有的不可变 R2 对象，发布阶段只上传本次 full/delta 和元数据，绝不覆盖历史包。
- 传统安装器：Inno Setup 只保留为旧版本迁移桥，不再参与每个日常版本。新用户和内置更新使用同一个 Velopack Setup。
- 源码一致性：test、stable 和 GitHub Release 全部锁定同一个 `origin/master` code-only 提交；本地发布工具只注入临时源码快照，不改变签名清单记录的提交。

更新失败不会删除 `%LocalAppData%\IDVB` 中的设置、地图、日志或 IDVM 数据。Updater 不强杀主程序：它通过仅限当前用户的命名管道请求主程序释放服务并退出，等待确认后才交给 Velopack。

## 一次性准备

1. 安装 .NET 10 SDK、Node/npm、Cloudflare Wrangler，并完成 R2 登录。
2. 执行 `dotnet tool restore`。仓库把 Velopack CLI 固定在 `.config/dotnet-tools.json` 的 1.2.0。
3. 首次建立更新信任根时执行：

   ```powershell
   .\release\New-IDVBUpdateSigningKey.ps1
   ```

   私钥写入被 Git 忽略的 `.secrets`；公钥写入 `release/trust` 并随 Updater 发布。必须把私钥离线备份到受控介质，禁止提交、上传 R2 或放入构建产物。丢失私钥后，旧客户端不能信任新密钥，必须先用仍受信任的旧密钥发布一次密钥轮换版本。

## 每次发布

1. 从上一份 `release/manifests/*.psd1` 复制新清单，分配从未使用过的 `PublicVersion`，更新产品版本、最低版本、密钥 ID、迁移标记和说明文件路径。正式候选清单在构建前设置 `Draft = $false` 并提交；草稿只允许 `-DryRun`，不要复用失败版本的目录。
2. 在 `release/notes` 新建 UTF-8 Markdown 发布说明。可执行 PSD1 保持 ASCII，以兼容 Windows PowerShell 5。
3. 确认当前分支是 `remaster` 或更新器改造分支，工作区完全干净。脚本会再次强制检查。
4. 逐阶段执行，并在每一步检查 `artifacts/updates/{PublicVersion}/receipts`：

   ```powershell
   $manifest = '.\release\manifests\本次版本.psd1'
   $private = '.\.secrets\idvb-update-2026-01-private.pem'

   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage Validate
   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage Build
   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage PackTest -PrivateKeyPath $private
   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage VerifyTest
   ```

5. 先打印 test 上传清单，不写 R2：

   ```powershell
   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage PublishTest -DryRun
   ```

   核对对象仅位于 `updates/win-x64-test/`，本次 `-delta.nupkg` 已列入上传清单、历史基准 full 包没有被列为待覆盖对象，且 `feed-envelope.json` 最后。只有从构建开始就是 `Draft = $false`、工作区仍干净且所有回执提交号一致的候选版本，才能去掉 `-DryRun`；构建后不得再修改清单或提交。
6. 如果选择 test，安装并人工验收：设置页入口、当前/目标版本、发布说明、下载取消与重试、主程序安全关闭、更新后重启、GUI 单实例、CLI 多实例、设置/地图/IDVM 数据保持不变。该验收是推荐步骤，不是 stable 的脚本前置条件。
7. 如果选择 stable，可直接使用同一个锁定提交执行 pack/verify：

   ```powershell
   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage PackStable -PrivateKeyPath $private
   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage VerifyStable
   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage PublishStable -DryRun
   ```

   核对无误后才允许去掉 `-DryRun`。`VerifyStable` 会验证 ECDSA 清单、所有资产 SHA-256、版本和提交一致性；有已签名基准包时还会强制要求生成本次 delta，并确认目标完整包中恰好包含主程序与独立 Updater。
8. stable 发布后，GitHub Release 不再重新运行 Inno 构建；它直接上传 stable 输出目录中的同一个版本化 Velopack Setup、`.sha256`、构建清单和 `feed-envelope.json`。
9. 发布后运行源码审计：

   ```powershell
   .\release\Invoke-IDVBRelease.ps1 -ManifestPath $manifest -Stage AuditSource
   git status --short
   git diff --cached --name-status
   ```

   GitHub 提交或推送只能包含正式源码与明确的交付文件。不得包含 `.secrets`、`artifacts`、构建输出、日志、缓存、临时验证文件或测试专用产物。

## 失败处理

- 不要删除脚本留下的快照、包、部分下载或回执；这些是诊断证据。
- 不要让脚本覆盖同名对象来“修复”一个已经分配的版本。查清原因后创建新版本清单。
- test 与 stable 使用相同的 ECDSA 清单和 SHA-256 资产校验；两者由发布者独立选择，均必须通过各自的完整构建、校验和 dry-run。
- R2 上传中断时，未上传最后的签名 envelope 不会切换客户端。确认不可变对象完整后，可按同一清单重试发布阶段。
- Worker 部署是独立的外部变更。先在本地检查 `web_installer/src/index.js`，再明确授权部署；发布脚本不会自动部署 Worker，也不会删除 R2 对象。
