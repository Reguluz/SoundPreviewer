# 安装、升级与 GitHub 发布

Windows 与 macOS 使用同一套 App、Core 和 Worker。版本统一由根目录 `Version.props` 定义，目前为 **0.4.0 预发行版**。应用本体、Worker 与 Windows x86 宿主均自带对应的 .NET 8 运行库，测试朋友无需安装 .NET SDK。

小修递增 patch，较大功能递增 minor；任何 major 递增均须用户明确确认，并更新 `version-policy.json` 的 `approvedMajor`。`0.x` 始终标记为 GitHub prerelease，即使 tag 没有 `-beta` 后缀。日常直接在 `main` 开发，不自动新建分支或 PR。

## 给测试朋友的安装说明

- Windows 10 1809 / Windows 11 x64：下载 `SoundPreviewer-版本-win-x64-setup.exe`，以当前用户安装。开始菜单打开 SoundPreviewer。升级时退出应用，再运行新安装包，沿用同一个安装目录。需要免安装时可下载 `win-x64-portable.zip`，解压到新的目录后启动。
- Apple Silicon Mac：下载 `osx-arm64.dmg`；Intel Mac：下载 `osx-x64.dmg`。将 `SoundPreviewer.app` 复制到可写的独立目录，例如 `~/Applications/SoundPreviewer`，再从本机打开；ZIP 中提供相同 app。升级时退出旧版本并替换 app，保留旁边的 `Preview` 及定位文件。不要直接在只读 DMG 中启动。
- 安装包尚未配置 Windows 代码签名和 Apple Developer 签名、公证。系统可能要求确认来源；发布页的 SHA-256 校验文件可用于检查下载完整性。

发行包包含 UI、索引和缓存功能、独立 Worker、固定版本的 Python 桥接源文件及 requirements。Windows 另包含 Sylenth1 x86 VST2 宿主及其许可声明；该可选原生宿主仍需要本机 Microsoft VC++ x86 运行库。商业音源、用户预制、授权文件、Python 和 DawDreamer 二进制均由使用者另行准备，不混入安装包。真实音源的已验证范围见仓库 `docs/rendering.md`；CI 空宿主或 UI 自检不扩大兼容性声明。

## 数据与升级约定

安装程序只管理程序文件。新应用默认将索引、设置、手工标签与分类、收藏、试听缓存和默认启动声音基准集中到可迁移的 `Preview` 目录：Windows 为应用程序目录下的 `Preview`；macOS 为 `SoundPreviewer.app` 旁的 `Preview`，不写入 app bundle 内。程序目录须可写，移动程序时应连同 `Preview` 和旁边的 `library-location.json` 一起保留。

明确指定 `--data-directory` 的目录继续优先使用，已有定位记录、旧版用户数据目录和 `local-data` 按兼容规则继续处理。请在首次切换版本前备份旧库；若旧版数据没有自动出现，在设置中打开原有音色库，成功打开后新版本会记住该位置。需要搬移时使用应用提供的迁移操作，并核对预览路径和空间后再处理原目录。

Windows 升级与卸载均保留生成的 `Preview`；macOS 替换或删除 app 也不删除旁边的 `Preview`。备份时先退出应用，再复制整个目录。数据库需要升级时先保留原始备份，预览 WAV 和已有音色记录继续复用。手工标签和分类与自动识别结果分别保存，重新扫描或重算规则不会覆盖手工编辑，明确清空也会保留；恢复自动识别会采用最近的自动结果。当前这些字段沿用数据库 schema 2，未知 JSON 字段随记录保留。应用版本、缓存格式和渲染器身份分别管理，升级应用不会仅因应用版本改变就清空缓存。

关联旧库只记住原位置，不搬移文件。请保留原 `local-data` 与原音频，不要随旧版程序文件夹删除；若预览或 Python 环境也位于旧程序目录，整个原目录暂需保留。发布脚本发现旧发布目录中含用户数据库、预览、日志或自定义规则时会拒绝清理，应先保留该目录并在新的工作副本构建。

旧程序不保证能读取新程序修改后的数据库。需要回退时使用升级前的完整数据备份，不要让两个版本同时打开同一份数据。

## 本地构建安装包

要求 .NET 8 SDK（见 `global.json`）。Windows 还需 Inno Setup；macOS 的 app、ZIP 和 DMG 必须在 macOS 上构建。

```powershell
./scripts/Publish-Desktop.ps1
./src/SoundPreviewer.Vst2Host/verify-self-test.ps1 -HostPath artifacts/publish-win-x64/Vst2Host-x86/SoundPreviewer.Vst2Host.exe
./scripts/Test-DesktopPackage.ps1 -Runtime win-x64
./scripts/Package-Desktop.ps1
```

```bash
bash scripts/Publish-Desktop.sh osx-arm64
pwsh -File scripts/Test-DesktopPackage.ps1 -Runtime osx-arm64
bash scripts/Package-Desktop.sh osx-arm64
```

Intel Mac 将 `osx-arm64` 替换为 `osx-x64`。输出位于 `artifacts/releases`。发布脚本只打包构建输出；发现旧输出或打包暂存中含 `Preview`（包括空目录）、`local-data`、定位文件、私人规则或缓存时会拒绝清理及打包，须保留原目录并从新工作副本构建。发行版程序旁的 `Prepare-DawDreamer.ps1` / `.sh` 可按固定 requirements 准备独立 Python 环境，默认写入 `Preview/renderers`；macOS 将环境放在 app 旁，不放入 bundle。显式指定 `-DataDirectory` / `--data-directory` 可复用迁移后的目录。运行时明确指定匹配架构的 CPython 3.11 / 3.12，Serum 2 加上 `-IncludeSerum2` / `--include-serum2`。

## GitHub Actions 与新版本

`Desktop build and packages` 在分支推送和手动运行时执行三平台矩阵；PR 由 `Release desktop` 先验证版本格式，再复用完全相同的矩阵：Windows x64、macOS Apple Silicon、macOS Intel。每个目标执行 C# 回归、Python 桥接测试、独立 Worker 发布和隔离空数据的 UI 自检，然后构建安装包与 SHA-256 校验文件。Windows 另执行内置 x86 宿主的无插件自检。Mac 归档在上传前完成，保留 app 内执行权限。包作为 Actions artifacts 保留 14 天；UI 诊断保留 7 天。

Windows CI 还会真实执行当前用户安装、同一安装包覆盖升级、安装后 UI 自检及卸载，并校验应用默认 `Preview` 中专属标记文件和 SQLite 测试库的 SHA-256 始终未变。该脚本只允许在 Windows GitHub Actions runner 上运行，安装目标限定到本次 `artifacts/installer-smoke-*`；不在开发者本机执行。它验证安装器的数据保留合同；旧版真实库的迁移和缓存记录兼容另由 C# 回归覆盖。

PR 会自动验证 Release 的版本检查与三平台打包入口，不创建公开 Release。GitHub 的手动工作流入口须先存在于默认分支；合入后在 Actions 手动运行 `Release desktop` 也只生成测试包。首次运行仍需在真实 GitHub 托管 runner 上通过；向朋友分发前，还应在干净测试机核对真实旧版库升级和 macOS app 启动。

后续发布步骤：

1. 按版本政策在 `Version.props` 更新版本，补充变更和兼容性说明；直接在 `main` 提交并等待三平台构建通过。
2. 用户明确要求发布后，为目标提交创建与版本完全一致的 tag，如 `v0.4.0` 或 `v0.4.1-beta.1`，再推送该 tag。未推 tag 不公开 Release。
3. `Release desktop` 检查版本与批准 major、tag 一致，重新运行同一套测试及打包。只有全部成功才校验三平台包，先上传至草稿，再公开 GitHub Release；中途失败可在草稿上重试，已公开版本不覆盖；`0.x` 和带预发行后缀的 tag 均标记为 prerelease。
4. 从 Releases 下载对应平台安装包，核对 `SHA256SUMS.txt`，先在测试数据副本上检查旧缓存、收藏、路径和升级行为，再分发给朋友。

创建 tag 会触发公开发布。当前实现提供从 GitHub 手动下载安装包的升级路径，不包含应用内自动下载或替换程序。发布 job 使用仓库的 `GITHUB_TOKEN` 和 `contents: write`；无需个人访问令牌。若组织策略限制此权限，需要在仓库 Actions 设置中允许该 workflow 写入 Releases。

GitHub 依据：[工作流触发与手动运行](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)、[工作流权限](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)、[GitHub CLI 发布](https://cli.github.com/manual/gh_release_create)、[归档执行权限](https://github.com/actions/upload-artifact#permission-loss)。Windows 安装使用 Inno Setup 的[当前用户模式](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)。
