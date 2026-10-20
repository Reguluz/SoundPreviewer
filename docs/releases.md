# 安装、升级与 GitHub 发布

Windows 与 macOS 使用同一套 App、Core 和 Worker。版本统一由根目录 `Version.props` 定义，目前为 **0.7.7 预发行版**（2026-10-21）。应用本体、Worker 与 Windows x86 宿主均自带对应的 .NET 8 运行库，测试朋友无需安装 .NET SDK。

0.7.7 增加空格键快速试听：从头播放当前选中音色的已有预览，播放区显示显著快捷键提示。文本输入与筛选控件保留原有空格操作。此更新不改变预览缓存格式、数据库 schema 2、Config / Preview 存储约定或 4 GiB RSS 目标；已有音色记录和有效预览继续使用。以下 0.7.6 及更早的验证记录保留各自版本范围，不表示 0.7.7 已完成相同验收。

0.7.6 修复首次解压以及 Preview / Config 缺失后的初始化：无需先复制旧目录即可打开，缺失时自动建立必要默认配置，无可迁入旧库时建立空库；已有 Preview 继续读取，已有 Config 保留。仅 Preview 缺失时可沿用 Config 重新扫描、生成；仅 Config 缺失时从现有库设置重建目录配置，分类规则优先保留兼容旧副本，外置默认文件缺失时从程序内置资源恢复。旧自动定位库不存在时提示并新建当前 Preview；手动指定导入的不存在来源仍报错，损坏的已有文件不被默认值覆盖。缓存键、schema 2 和 4 GiB RSS 目标保持不变。

0.7.6 程序提交 `f7b612e1496e8b62c55b9e544518e49de7664f2e` 已通过 [GitHub 三平台 CI](https://github.com/Reguluz/SoundPreviewer/actions/runs/39470173665)，Windows x64、Apple Silicon 与 Intel Mac 均为 success。各平台 C# 56/56、Python 63 项，本地含静音原生播放的完整回归 57/57。发布程序的隔离副本分别验证 Preview 与 Config 都缺失、仅 Config 缺失、仅 Preview 缺失三个正常启动场景；空库、可用默认分类、固定保存位置、缺失旧库提示、自定义分类原始哈希及目录未知字段保留均通过。连续两次发布、Worker 协议、旧库迁移重开、原件保留、Windows 安装覆盖与卸载保留、Mac DMG 及封装应用检查通过。Windows 本地安装包和 Portable 的程序版本对应上述提交，根目录仍为 12 个文件，包内没有个人配置或缓存，SHA-256 已核对。

0.7.5 按 16 GB 及以上内存电脑的使用需求，将主程序和全部可见生成后代的总 RSS 目标从 2 GiB 调整为 **4 GiB（4096 MiB）**；实际占用达到 **3.75 GiB（3840 MiB）** 暂停启动新任务，超过 4 GiB 停止本批并保留已完成预览。启动仍计算实际占用与预测预留，因此可能提前限流，不保证占满预算或达到 8 条并行；采样保护不是操作系统硬配额。并行档位、缓存键及数据库 schema 2 不变，已有 Preview、Config 和有效缓存继续使用。

0.7.5 程序提交 `339d8949cf76aeaf17f8d17afe40c555266371fb` 已通过 [GitHub 三平台 CI](https://github.com/Reguluz/SoundPreviewer/actions/runs/39466344628)，Windows x64、Apple Silicon 与 Intel Mac 均为 success；各平台 C# 56/56、Python 63 项，本地含静音原生播放的完整回归 57/57。资源回归通过注入计数验证真实隔离子进程在 2–3.5 GiB 区间继续运行、超过 4 GiB 后停止并恢复未完成项，以及实占 581 MiB 加预留 1139 MiB 时仍可增加任务；这些计数是调度测试模型。两次发布后的 UI 明确检查 4096 MiB 分母及固定进度区域，Worker 协议、旧库迁移重开、原文件哈希保留、Windows 安装覆盖与卸载保留、Mac DMG 及封装应用检查均通过。Windows 本地安装包与 Portable 的程序版本对应上述提交，包内无个人配置和缓存，SHA-256 已核对。

0.7.4 将个人目录配置、定位文件和分类规则集中到程序旁 Config，兼容旧位置并保留原件。并行默认开启，显式关闭仍保留，更新音源不自动重置。修正共享主程序内存增长在各任务预测中重复计入、历史重音源压低后续轻音源显示的问题，当时保留 2 GiB 目标与停止保护。队列显示所选、去重、待识别、暂不支持和来源缺失的明细；进度区域固定高度，避免描述换行推动下方列表。

Sylenth1 x86 宿主 v3 精确支持已实测的 1028 字节 `1lys` 状态头 2202/2211→2200 改写，其余状态仍须相同；旧 v2/v1 结果继续识别。真实 15 样本检查中，6 个原误判恢复、3 个原成功保持成功，3 个未加载及 3 个不兼容状态被拒绝且不发布 WAV；完整 quick Worker 链另验证两个恢复样本和一个不兼容样本，源文件与插件哈希未变。明确不兼容返回具体格式信息并在本批停止重试。App 与 Worker 改为单文件发布，不裁剪，仍保留必需的外置原生库与 x86 子目录，旧 Portable 覆盖升级不删除旧依赖。

macOS DMG 按暂存文件的逻辑大小预留 25% 加 64 MiB 并显式指定 HFS+ 容量，避免单文件程序在自动估容的镜像内复制失败；打包日志记录镜像容量及宿主剩余空间。三平台 CI 连续发布两次，再验证外置渲染资源、独立 Worker 协议与界面，覆盖增量发布路径。

0.7.4 程序提交 `e31baa4df8a4783ba4ff5c000f2f4ececf0892c3` 已通过 [GitHub 三平台 CI](https://github.com/Reguluz/SoundPreviewer/actions/runs/39460512145)，Windows x64、Apple Silicon 与 Intel Mac 的正式结论均为 success。各平台 C# 56/56、Python 63 项通过；本地完整回归含静音原生播放为 57/57。连续两次发布后的 Worker 协议、外置资源、队列选择计数、两种尺寸的进度区域稳定性均验证通过；旧库迁入、Config 手改后无参数重开保留原文件哈希，Windows 安装、覆盖升级和卸载保留数据，两 Mac DMG 与封装 app 检查通过。本地 Windows Portable 共 492 个文件、根目录 12 个，不含个人配置、数据库或缓存；App 与 Worker 版本均指向该程序提交，包内可执行文件与发布输出一致，安装包及 ZIP 的 SHA-256 已核对。真实 Sylenth1 样本范围见上文，自动检查不扩大其他音源或 DAW 的验证范围。

0.7.3 修正跨来源副本的局部筛选及主项生成映射，来源显示总量、去重量和跨来源相同条数。生成进度实时区分成功、新生成、复用、失败与未配置，运行分母使用综合音源多实例许可、内存和剩余任务的有效上限，另列设定及内存上限。目录配置从原设置首次导出到程序旁 `directories.json`，后续启动 JSON 优先并回存数据库供旧版兼容；支持手工修改、保留来源 ID 和未知字段，损坏或外部冲突拒绝覆盖。打包不带个人配置，安装、升级和卸载保留该文件及 Preview。

0.7.3 程序提交 `b273e465c069181ca76d0562edb2d421b6f303bd` 已通过 [GitHub 三平台 CI](https://github.com/Reguluz/SoundPreviewer/actions/runs/39448926111)，Windows x64、Apple Silicon 与 Intel Mac 的正式结论均为 success。本地完整回归 56/56（含静音原生播放）；发布包 UI 检查、旧库迁入及 JSON 手改后无参数重开均通过，原始数据库与音频哈希保留。Windows 安装、覆盖升级、卸载检查把安装根 `directories.json` 纳入哈希保护。Portable 共 706 项，不含用户 Preview、目录配置、数据库、日志或音频；包版本指向该程序提交，SHA-256 已核对。以上不扩大真实插件或 DAW 的已验证范围。

0.7.2 增加来源路径的索引数量与重复副本提示；修复未配好的音源阻止其他设置保存，区分 Python、插件与内置宿主，保留离线配置和已有缓存。Windows 内置 Sylenth1 x86 宿主在声音校准不稳定时可使用逐音色精确状态核验，核验失败仍拒绝提交，旧完整预览继续优先复用。此路径不扩大其他音源的已验证范围。

0.7.1 修正数据位置：正式运行固定使用程序旁的 Preview，旧库位置只作兼容迁移来源；先复制校验并保留原始数据库与音频，不继续向旧目录生成。已有 Preview 不被旧库覆盖，程序目录不可写时明确报错。

0.7.0 增加同音源优先调度、原音色或音色库文件定位及独立音色文件拖出，保留原 2 GiB 目标与旧库兼容规则。当前按用户认可的文件定位方案过渡，Windows FL Studio 自动创建音源仍待专用适配，具体接收方式见 [DAW 音色传递](daw-transfer.md)。当前仍按音色创建新宿主，不能将排序优化解释为插件只加载一次。

0.6.0 新增按物理核心自动选择并行、1 / 2 / 4 / 8 / 16 手动档，以及父进程和全部生成后代合计 2 GiB RSS 目标下的预留、限流和超限停止恢复。新安装默认自动，旧串行选择与同音源多开许可保留。采样保护不等于操作系统硬配额。调度设置不改变旧缓存键、数据库 schema 或手工标签 / 分类。行为与历史小样本测速见[并行说明](parallel-rendering.md)。

小修递增 patch，较大功能递增 minor；任何 major 递增均须用户明确确认，并更新 `version-policy.json` 的 `approvedMajor`。`0.x` 始终标记为 GitHub prerelease，即使 tag 没有 `-beta` 后缀。日常直接在 `main` 开发，不自动新建分支或 PR。

## 给测试朋友的安装说明

- Windows 10 1809 / Windows 11 x64：下载 `SoundPreviewer-版本-win-x64-setup.exe`，以当前用户安装。开始菜单打开 SoundPreviewer。升级时退出应用，再运行新安装包，沿用同一个安装目录。需要免安装时可下载 `win-x64-portable.zip`，解压到新的目录后启动。
- Apple Silicon Mac：下载 `osx-arm64.dmg`；Intel Mac：下载 `osx-x64.dmg`。将 `SoundPreviewer.app` 复制到可写的独立目录，例如 `~/Applications/SoundPreviewer`，再从本机打开；ZIP 中提供相同 app。升级时退出旧版本并替换 app，保留旁边的 `Preview`。旧定位文件请保留供兼容迁移查找原库。不要直接在只读 DMG 中启动。
- 安装包尚未配置 Windows 代码签名和 Apple Developer 签名、公证。系统可能要求确认来源；发布页的 SHA-256 校验文件可用于检查下载完整性。

发行包包含 UI、索引和缓存功能、独立 Worker、固定版本的 Python 桥接源文件及 requirements。Windows 另包含 Sylenth1 x86 VST2 宿主及其许可声明；该可选原生宿主仍需要本机 Microsoft VC++ x86 运行库。商业音源、用户预制、授权文件、Python 和 DawDreamer 二进制均由使用者另行准备，不混入安装包。真实音源的已验证范围见仓库 `docs/rendering.md`；CI 空宿主或 UI 自检不扩大兼容性声明。

## 数据与升级约定

安装程序只管理程序文件。0.7.1 正式运行将索引、设置、手工标签与分类、收藏、试听缓存和默认启动声音基准固定集中到程序旁的 `Preview`：Windows 为 `SoundPreviewer.exe` 同目录下的 `Preview`，安装版与 Portable 相同；macOS 为 `SoundPreviewer.app` 旁的 `Preview`，不写入 app bundle 内。程序目录须可写，不可写时明确报错，不回退到用户目录。用户界面不再提供任意外部数据或新音频输出目录。

首次完整解压无需先恢复 Preview 或 Config，程序可从缺失状态建立必要内容。推荐在原目录直接升级并保留两者，以延续个人记录、配置和有效缓存；也可退出程序并备份当前目录后手动恢复旧目录。音色和预览可以重新扫描、生成，但未恢复的收藏、标签、反馈、手工分类等个人数据不会由空库重新推导；这些记录需要旧 Preview，个人分类规则还需对应 Config 或旧规则副本。清理试听缓存请使用「清空预览」，保留数据库和个人记录。

旧定位记录、旧版用户数据目录和 `local-data` 仅用于确定首次兼容迁移来源。新程序先复制一致数据库快照、个人记录、规则与引用音频，校验成功后使用程序旁的 `Preview`；保留原数据库与原音频作为回退备份。已有 `Preview` 不被另一套旧库覆盖，只归并自身引用的外部音频和加载防错参照。首次切换版本前请备份旧库，迁入后核对记录及试听再处理原目录。普通历史 `--data-directory` 仅指定导入来源；`--smoke-test --data-directory` 保留给构建、测试与隔离诊断，不是正式使用的外部保存位置设置。内部 `--test-installed-storage` 跑同一正式启动迁移后再执行 UI 自检。

设置提供「打开 Preview 文件夹」「清空预览」「导入旧版音色库」。手动导入必须在界面确认；先在暂存目录复制所选源库与全部引用音频，校验副本和记录、设置的可读性，再把整个当前 Preview 原子改名为程序旁 `Preview-before-import-*` 并发布暂存库，不合并。失败且目标空缺时尽量原子恢复原名，并明确提示保留的备份位置。备份内仍有原 Preview 的绝对路径，不能直接作为异名源导入；恢复时先退出并备份当前 Preview，再将所需恢复目录还原名 Preview。

Windows 升级与卸载均保留生成的 `Preview`；macOS 替换或删除 app 也不删除旁边的 `Preview`。备份时先退出应用，再复制整个目录。首次导入只升级暂存副本，保留源数据库及其 schema、WAL 中的已提交记录与原文件；预览 WAV 和已有音色记录继续复用。缺失的已生成音频保留原映射并提示，不自动清空。手工标签和分类与自动识别结果分别保存，重新扫描或重算规则不会覆盖手工编辑，明确清空也会保留；恢复自动识别会采用最近的自动结果。当前这些字段沿用数据库 schema 2，记录与应用设置的未知 JSON 字段保留。应用版本、缓存格式和渲染器身份分别管理，升级应用不会仅因应用版本改变就清空缓存。

0.7.0 及更早版本可能只关联原库而继续向旧目录写音频；0.7.1 不再沿用该行为。兼容迁入不会删除原 `local-data`、旧用户库、原音频或源音色。若可选 Python 环境仍依赖原程序路径，请先保留原目录并核对或重新准备。发布脚本发现旧发布目录中含用户数据库、预览、日志或自定义规则时会拒绝清理，应先保留该目录并在新的工作副本构建。

Portable 指免安装，升级时可在原目录替换程序文件。移动程序前先退出并备份，连同 `Preview` 一起保留；音色来源、插件和可选 Python 环境的绝对路径仍需核对。可选 Python 环境搬移后可能需要按新路径重新准备。

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

Intel Mac 将 `osx-arm64` 替换为 `osx-x64`。输出位于 `artifacts/releases`。发布脚本只打包构建输出；发现旧输出或打包暂存中含 `Preview`（包括空目录）、`local-data`、定位文件、私人规则或缓存时会拒绝清理及打包，须保留原目录并从新工作副本构建。发行版程序旁的 `Prepare-DawDreamer.ps1` / `.sh` 可按固定 requirements 准备独立 Python 环境，正常使用时写入当前程序旁的 `Preview/renderers`；macOS 将环境放在 app 旁，不放入 bundle。脚本的数据目录参数仅用于隔离验证等开发诊断。运行时明确指定匹配架构的 CPython 3.11 / 3.12，Serum 2 加上 `-IncludeSerum2` / `--include-serum2`。

## GitHub Actions 与新版本

`Desktop build and packages` 在分支推送和手动运行时执行三平台矩阵；PR 由 `Release desktop` 先验证版本格式，再复用完全相同的矩阵：Windows x64、macOS Apple Silicon、macOS Intel。每个目标执行旧库迁移专项检查、完整 C# 回归、Python 桥接测试及独立 Worker 发布；发布程序分别验证隔离空库 UI，以及旧库首次迁入、无参数再次启动和原文件哈希保留，再构建安装包与 SHA-256 校验文件。Windows 另执行内置 x86 宿主的无插件自检。Mac 归档在上传前完成，保留 app 内执行权限。包作为 Actions artifacts 保留 14 天；UI 诊断保留 7 天。

Windows CI 还会真实执行当前用户安装、同一安装包覆盖升级、安装后 UI 自检及卸载，并校验应用默认 `Preview` 中专属标记文件和 SQLite 测试库的 SHA-256 始终未变。该脚本只允许在 Windows GitHub Actions runner 上运行，安装目标限定到本次 `artifacts/installer-smoke-*`；不在开发者本机执行。它验证安装器的数据保留合同；旧版真实库的迁移和缓存记录兼容另由 C# 回归覆盖。

PR 会自动验证 Release 的版本检查与三平台打包入口，不创建公开 Release。GitHub 的手动工作流入口须先存在于默认分支；合入后在 Actions 手动运行 `Release desktop` 也只生成测试包。0.7.2（程序提交 `21bf0ff`）已在 GitHub 托管 runner 上完成[三平台构建与打包](https://github.com/Reguluz/SoundPreviewer/actions/runs/39443006871)：各平台旧库迁移专项 1/1、完整 C# 53/53、Python 63 项通过；来源数量、部分音源设置保存、旧库迁入、无参数重开及原文件哈希保留均验证完成。Windows 内置宿主程序位置边界检查、安装、同包覆盖升级及卸载保留数据，两 Mac 封装 app 自检也均通过。真实音源与 DAW 接收的验收范围未因这些自动检查扩大，向朋友分发前仍应在干净测试机核对真实旧库升级与音源运行。

后续发布步骤：

1. 按版本政策在 `Version.props` 更新版本，补充变更和兼容性说明；直接在 `main` 提交并等待三平台构建通过。
2. 用户明确要求发布后，为目标提交创建与版本完全一致的 tag，如 `v0.4.0` 或 `v0.4.1-beta.1`，再推送该 tag。未推 tag 不公开 Release。
3. `Release desktop` 检查版本与批准 major、tag 一致，重新运行同一套测试及打包。只有全部成功才校验三平台包，先上传至草稿，再公开 GitHub Release；中途失败可在草稿上重试，已公开版本不覆盖；`0.x` 和带预发行后缀的 tag 均标记为 prerelease。
4. 从 Releases 下载对应平台安装包，核对 `SHA256SUMS.txt`，先在测试数据副本上检查旧缓存、收藏、路径和升级行为，再分发给朋友。

创建 tag 会触发公开发布。当前实现提供从 GitHub 手动下载安装包的升级路径，不包含应用内自动下载或替换程序。发布 job 使用仓库的 `GITHUB_TOKEN` 和 `contents: write`；无需个人访问令牌。若组织策略限制此权限，需要在仓库 Actions 设置中允许该 workflow 写入 Releases。

GitHub 依据：[工作流触发与手动运行](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)、[工作流权限](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)、[GitHub CLI 发布](https://cli.github.com/manual/gh_release_create)、[归档执行权限](https://github.com/actions/upload-artifact#permission-loss)。Windows 安装使用 Inno Setup 的[当前用户模式](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)。
