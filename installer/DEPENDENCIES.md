# IDVB b01 离线依赖锁定清单

构建前将以下官方离线安装包放入本目录。`Build-Release.ps1` 会校验 SHA-256 和 Authenticode 签名；任一校验失败即停止构建。

| 文件 | 用途 | SHA-256 |
| --- | --- | --- |
| `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` | Microsoft Edge WebView2 Evergreen Standalone Installer（x64） | `e99838c51bb3379b244654aa77e33032d42fc2b5d224c5babce432d9fd3dcb28` |
| `vc_redist.x64.exe` | Microsoft Visual C++ 2015–2022 Redistributable（x64） | `cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b` |

来源：Microsoft 官方 WebView2 下载页与 Visual C++ Redistributable 发布渠道。更新任一依赖时，须重新验证 Microsoft Authenticode 签名、更新本表、变更 b 版本并重新完成干净系统验收。
