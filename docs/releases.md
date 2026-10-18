# 安装、升级与 GitHub 发布

Windows 与 macOS 使用同一套 App、Core 和 Worker。版本统一由根目录 `Version.props` 定义，目前为 **1.4.0**。应用本体、Worker 与 Windows x86 宿主均自带对应的 .NET 8 运行库，测试朋友无需安装 .NET SDK。

## 给测试朋友的安装说明

- Windows 10 1809 / Windows 11 x64：下载 `SoundPreviewer-版本-win-x64-setup.exe`，以当前用户安装。开始菜单打开 SoundPreviewer。升级时退出应用，再运行新安装包，沿用同一个安装目录。需要免安装时可下载 `win-x64-portable.zip`，解压到新的目录后启动。
- Apple Silicon Mac：下载 `osx-arm64.dmg`；Intel Mac：下载 `osx-x64.dmg`。打开磁盘映像，将 `SoundPreviewer.app` 拖入 Applications。升级时退出旧版本，再替换 Applications 中的 app。ZIP 中提供相同 app。
- 安装包尚未配置 Windows 代码签名和 Apple Developer 签名、公证。系统可能要求确认来源；发布页的 SHA-256 校验文件可用于检查下载完整性。

发行包包含 UI、索引和缓存功能、独立 Worker、固定版本的 Python 桥接源文件及 requirements。Windows 另包含 Sylenth1 x86 VST2 宿主及其许可声明；该可选原生宿主仍需要本机 Microsoft VC++ x86 运行库。商业音源、用户预制、授权文件、Python 和 DawDreamer 二进制均由使用者另行准备，不混入安装包。真实音源的已验证范围见仓库 `docs/rendering.md`；CI 空宿主或 UI 自检不扩大兼容性声明。

## 数据与升级约定

安装程序只管理程序文件。索引、设置、收藏、试听缓存和默认启动声音基准保存到稳定的用户数据目录，不带版本号，也不随安装目录变化：Windows 使用 `%LOCALAPPDATA%\SoundPreviewer`，macOS 使用 `~/Library/Application Support/SoundPreviewer`。明确指定 `--data-directory` 的数据目录继续优先使用；旧便携版的 `local-data` 按应用的兼容定位规则继续处理。请在首次切换安装版前备份旧 `local-data`；若旧版数据没有自动出现，在设置中「打开已有音色库」选择原目录，成功打开后新版本会记住该位置。也可使用 `--data-directory` 明确打开原目录，再核对设置中的数据路径。

Windows 升级与卸载均不删除用户数据；macOS 替换或删除 app 也不删除用户数据。备份时先退出应用，再复制整个数据目录。数据库升级前会保留原始备份，预览 WAV 和已有音色记录继续复用。应用版本、缓存格式和渲染器身份分别管理；升级应用不会仅因应用版本改变就清空缓存。配置的插件和源预制仍须保留在本机可访问的位置。

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

Intel Mac 将 `osx-arm64` 替换为 `osx-x64`。输出位于 `artifacts/releases`。发布脚本会重新创建专用发布目录，包中只包含构建输出；不打包源码目录中的 `local-data`、`artifacts` 或个人音源。发行版程序旁的 `Prepare-DawDreamer.ps1` / `.sh` 可按固定 requirements 准备独立 Python 环境，默认写入稳定用户数据目录的 `renderers`，不写入 app。运行时明确指定匹配架构的 CPython 3.11 / 3.12，Serum 2 加上 `-IncludeSerum2` / `--include-serum2`；具体参数见仓库 `docs/rendering.md`。

## GitHub Actions 与新版本

`Desktop build and packages` 在分支推送和手动运行时执行三平台矩阵；PR 由 `Release desktop` 先验证版本格式，再复用完全相同的矩阵：Windows x64、macOS Apple Silicon、macOS Intel。每个目标执行 C# 回归、Python 桥接测试、独立 Worker 发布和隔离空数据的 UI 自检，然后构建安装包与 SHA-256 校验文件。Windows 另执行内置 x86 宿主的无插件自检。Mac 归档在上传前完成，保留 app 内执行权限。包作为 Actions artifacts 保留 14 天；UI 诊断保留 7 天。

Windows CI 还会真实执行当前用户安装、同一安装包覆盖升级、安装后 UI 自检及卸载，并校验稳定用户目录中专属标记文件和 SQLite 测试库的 SHA-256 始终未变。该脚本只允许在 Windows GitHub Actions runner 上运行，安装目标限定到本次 `artifacts/installer-smoke-*`；不在开发者本机执行。它验证安装器的数据保留合同；旧版真实库的迁移和缓存记录兼容另由 C# 回归覆盖。

PR 会自动验证 Release 的版本检查与三平台打包入口，不创建公开 Release。GitHub 的手动工作流入口须先存在于默认分支；合入后在 Actions 手动运行 `Release desktop` 也只生成测试包。首次运行仍需在真实 GitHub 托管 runner 上通过；向朋友分发前，还应在干净测试机核对真实旧版库升级和 macOS app 启动。

后续发布步骤：

1. 在 `Version.props` 更新版本，补充变更和兼容性说明；提交 PR，等待三平台构建通过。
2. 合并后为目标提交创建与版本完全一致的 tag，如 `v1.4.0` 或 `v1.4.1-beta.1`，再推送该 tag。
3. `Release desktop` 检查 tag 与版本一致，重新运行同一套测试及打包。只有全部成功才校验三平台包，先上传至草稿，再公开 GitHub Release；中途失败可在草稿上重试，已公开版本不覆盖；带预发行后缀的 tag 自动标记为 prerelease。
4. 从 Releases 下载对应平台安装包，核对 `SHA256SUMS.txt`，先在测试数据副本上检查旧缓存、收藏、路径和升级行为，再分发给朋友。

创建 tag 会触发公开发布。当前实现提供从 GitHub 手动下载安装包的升级路径，不包含应用内自动下载或替换程序。发布 job 使用仓库的 `GITHUB_TOKEN` 和 `contents: write`；无需个人访问令牌。若组织策略限制此权限，需要在仓库 Actions 设置中允许该 workflow 写入 Releases。

GitHub 依据：[工作流触发与手动运行](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)、[工作流权限](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)、[GitHub CLI 发布](https://cli.github.com/manual/gh_release_create)、[归档执行权限](https://github.com/actions/upload-artifact#permission-loss)。Windows 安装使用 Inno Setup 的[当前用户模式](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)。
