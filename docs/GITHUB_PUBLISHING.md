# GitHub 发布规范

本规范适用于 Identity Vision Bridge（IDVB）的源代码仓库、预发布版本和正式 GitHub Release。它的目标是让每一次公开内容都可追溯、可复现，并且不包含用户数据、未授权资源或未确认的法律声明。

## 1. 发布状态与阻断项

在以下项目全部完成前，仓库不得标记为开源发布完成，也不得接受外部代码贡献：

- [x] 权利人确认非商业源码许可、版权归属和年份，并在仓库根目录提交 `LICENSE`。
- [ ] 对 `installer/LICENSE.txt` 的安装/使用许可进行复核；它不能替代开源 `LICENSE`，两者的适用关系必须写明。
- [ ] 为源代码、文档、图标、截图、地图和示例 IDVM 数据逐项确认可公开再分发的权利。
- [ ] 启用 GitHub 私密漏洞报告，或在 `SECURITY.md` 中提供可用的私下安全联络渠道。
- [ ] 将 `README.md`、`CONTRIBUTING.md`、`CODE_OF_CONDUCT.md`、`SECURITY.md` 与本规范一同发布。
- [ ] 使用干净的克隆环境完成构建、测试和发布包检查。

许可证类型属于权利人决定；维护者不得用口头说明或安装包摘要替代根目录的完整许可证文件。当前许可包含非商业及用途限制，对外应称为“源码公开”或“非商业源码许可”，不得宣称为 OSI 定义的开源许可证。

## 2. 仓库公开前的内容审查

### 允许提交

- 可复现构建所需的源代码、受许可的资产、测试和文档。
- 经脱敏且已取得再分发许可的示例数据。
- 对应版本的变更说明、校验和与发布清单。

### 禁止提交

- `%LocalAppData%\IDVBuff` 中的任何本地状态，包括 `settings.json`、地图、日志、校准数据、截图、研究数据或识别统计。
- 用户名、设备路径、令牌、密钥、会话信息、崩溃转储和未脱敏日志。
- 未确认授权的游戏素材、地图截图、第三方图标、字体、数据包或反编译产物。
- `bin/`、`obj/`、`publish/`、`buildverify/`、测试结果及其他可再生成的构建输出。
- 只适用于内部验证的安装包、签名证书、离线依赖安装程序和发布工作目录。

提交者须在推送前检查 `git status` 和暂存区，确认没有意外的二进制文件或本地数据。`.idvm` 文件默认按数据资产处理；除非具有明确的再分发许可且完成脱敏审查，否则不得提交。

## 3. 分支、提交与拉取请求

- `main` 仅接收已审阅、可构建且测试通过的提交。
- 开发从 `feature/<主题>`、`fix/<主题>` 或 `docs/<主题>` 分支开始；不得直接向 `main` 推送功能性变更。
- 提交标题使用简短动词句，例如 `feat: add IDVM import validation`、`fix: retain overlay scale`、`docs: clarify release policy`。
- 每个拉取请求只处理一个可审阅主题，说明用户影响、测试方式、数据/资源来源及兼容性影响。
- 涉及 `.idvm` 格式、持久化设置、地图数据或公开 API 的改动，必须同步更新格式说明、迁移策略和测试。
- 涉及用户可见文本时，使用 `Identity Vision Bridge` 或 `IDVB`；不得擅自重命名 `IDVBuff` 的命名空间、程序集或本地路径。

合并前至少需要一位维护者审阅；发布、安全、许可和数据资产变更需要权利人或指定发布负责人额外确认。

## 4. 本地验证基线

维护者在本地 Windows 环境执行以下最低检查：

```powershell
dotnet restore IDVBuff.csproj
dotnet build IDVBuff.csproj -c Release --no-restore
dotnet test IDVBuff.Tests\IDVBuff.Tests.csproj -c Release --no-restore
```

完成验证后，使用本地的 Inno Setup 构建并通过 GitHub CLI 发布：

```powershell
.\installer\Build-Release.ps1 -ReleaseLine b01.1 -PublishGitHubRelease
```

发布前先运行 `gh auth login`。附加 `-Prerelease` 可创建预发布版本；附加 `-GitHubRepository owner/repo` 可指定目标仓库。

自动检查通过不等于可以发布。维护者仍须人工确认许可证、隐私、资源授权、发布说明和安装包签名状态。若有可用签名证书，可额外传入 `-RequireSignedRelease`、`-SignToolPath` 和 `-CertificateThumbprint` 生成已签名安装包。

## 5. 版本与 Release

当前展示版本遵循 `bNN.N-YY.M.D.HHmm`，例如 `b01.1-26.8.4.1129`。创建 GitHub Release 时，以下字段必须一致：

- Git 标签；
- Release 标题；
- 应用内“当前版本”；
- 安装包名称、发布说明和发布清单中的公开版本。

如需改变版本规则，先在同一拉取请求中更新本节、构建脚本和用户可见版本文本。不要只改其中一个位置。

每个 Release 至少包含：

1. 安装包，并在发布清单和发行说明中标明其签名状态；
2. 安装包 SHA-256 文件；
3. 发布清单，记录提交 SHA、构建时间、目标运行时和依赖版本；
4. 面向用户的变更说明、升级注意事项和已知限制；
5. 对应的源代码标签与可复现构建说明。

发布前运行 `installer/Build-Release.ps1` 时，保持其对本地用户数据的拒绝检查，不得弱化该检查。发布负责人须验证包内没有 `settings.json`、地图、日志、校准数据或研究数据。

## 6. GitHub 仓库设置

- 默认分支为 `main`，启用拉取请求审阅、状态检查和禁止强制推送。
- 启用私密漏洞报告，限制 Release 创建和标签推送权限。
- Issue 使用仓库模板；安全问题不使用公开 Issue。
- Release 附件必须由本地当前 Git 提交构建、校验并通过 GitHub CLI 与标签同时上传。
- 在仓库首页、Release 页和文档中统一使用 Identity Vision Bridge / IDVB 品牌。

## 7. 发布后处置

发现敏感信息、侵权资产、恶意依赖或严重安全问题时，立即停止后续发布，限制受影响版本的下载，并保留审计记录。撤回或替换 Release 后，应在新的发布说明中说明影响范围、缓解措施和安全升级路径；不得删除问题痕迹来掩盖风险。
