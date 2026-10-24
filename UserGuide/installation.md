# 安装与启动

**简体中文（默认）** · [English](en/installation.md)

从 [GitHub Releases](https://github.com/Reguluz/SoundPreviewer/releases) 下载对应平台的包。如果尚无对应版本的 Release，可到 [自动构建页面](https://github.com/Reguluz/SoundPreviewer/actions/workflows/desktop.yml)，打开标记为成功的构建，在 Artifacts 下载 `packages-平台`。Actions 测试包有保留期限，下载可能需要登录 GitHub。

| 你的电脑 | 选择的包 | 打开方式 |
| --- | --- | --- |
| Windows 10 1809 及以上 / Windows 11，x64 | `win-x64-setup.exe`；免安装选 `win-x64-portable.zip` | 安装后从开始菜单打开；免安装版先完整解压，再打开 `SoundPreviewer.exe` |
| macOS 12 及以上，Apple Silicon（M 系列） | `osx-arm64.dmg` 或 `osx-arm64.zip` | 将 `SoundPreviewer.app` 复制到可写的独立文件夹后打开，如 `~/Applications/SoundPreviewer` |
| macOS 12 及以上，Intel | `osx-x64.dmg` 或 `osx-x64.zip` | 与 Apple Silicon 版相同，注意选择 x64 包 |

表中系统下限来自安装包配置，不表示每个旧系统版本都已实机验证。Mac 请勿直接在只读 DMG 中运行。安装包尚未做 Windows 代码签名和 Apple 签名、公证，系统可能提示来源未知；请核对下载来源，以及同一发布页或构建产物中的 `SHA256SUMS-平台.txt`。Linux 和 Windows ARM64 尚无专用发行包。

程序已包含所需的 .NET 运行库，普通使用无需安装开发工具。**音源插件、音色文件和授权需自行准备**；本项目不附带商业音源或私人音色。生成新预览需要本机安装相应音源，播放已有预览不需要再次加载音源。

**首次完整解压后即可打开，无需先复制旧 Preview 或 Config。** 缺失的必要内容会自动建立，再按[快速开始](quick-start.md)添加音色目录、扫描并生成。要继续使用原有收藏、标签、手工分类和预览，请保留或手动恢复旧目录；新建空库不会自动找回这些个人数据。

<a id="check"></a>

## 启动后先检查

确认程序目录有写入权限，且已有旧库时没有丢失原来的 Preview、Config 和个人 JSON 文件。新建空库可直接使用；需要旧收藏和预览时，先阅读[数据恢复与旧库导入](storage.md#import)，不要覆盖唯一一份旧库。

下一步：[完成最小配置并测试一个音色](quick-start.md#configure)。

---

[操作说明首页](index.md) · [快速开始](quick-start.md) · [安装与启动](installation.md) · [音源配置](synths.md) · [生成与试听](rendering.md) · [音色库整理](library.md) · [数据维护](storage.md) · [语言与外观](appearance.md) · [常见问题](troubleshooting.md)

适用于 SoundPreviewer 1.0.1。正文和截图可离线阅读，外部下载与反馈链接需要联网。
