# Windows / macOS 共用版本

更新：2026-10-18，当前版本 v1.4.0。此页记录移植与双平台后续改进，历史实测范围按版本保留。

## 共用范围

`SoundPreviewer.App` 改为 Avalonia / .NET 8，共用同一套 XAML、事件处理、视图模型和波形控件。三个主页面、五个设置页、组合筛选、可选列、收藏与反馈、三音区播放、任务停止和继续均沿用原有控制器。Core、Worker 和回归测试统一使用 `net8.0`。

两端共用预制识别、MD5 / SHA256、SQLite schema / 迁移、分类规则、JSON 工作进程协议、队列、原子音频提交、半秒裁剪、音区映射、启动基准与结果校验。Windows 继续通过 WinMM 播放；macOS 通过 AudioQueue 播放相同 PCM16 数据。SQLite 分别加载系统的 `winsqlite3.dll` 与 `libsqlite3.dylib`。

macOS 扫描只读解析 XML / binary Info.plist 和 Mach-O，识别 `.app`、`.vst`、`.vst3` 的实际可执行文件及 x64 / arm64 / Universal 架构，不通过加载插件猜测身份。Windows 保留 PE / 版本 / 导出检查；旧发现结果缺少 Platform 时仍解释为 Windows。自动关联只选本平台和当前进程兼容的已识别产品。手选 VST 插件与配置外部宿主仍是不同操作。

Windows 的独立 x86 VST.NET 宿主保留，不加入 macOS 发布。macOS 的 Serum 1 / Sylenth1 FXP 路径使用相同 DawDreamer 协议，接受匹配架构的 VST2 `.vst` bundle；Python、DawDreamer 与插件必须使用相同架构。原来的 MSVC 运行库检查仅在 Windows 执行。Vital 使用各平台独立程序；从 macOS 选择 `.app` 时解析到其真实可执行文件。

Serum 2 的共用 DawDreamer 适配使用 VST3 和原生 `.SerumPreset`。适配器从实际插件的 fresh state 获取状态格式和版本，转换预制后加载，再保存状态并核对目标内容；无法确认时拒绝提交。旧 Serum FXP 须先在 Serum 2 中导入并另存为 `.SerumPreset`。这套实现随 Windows / macOS 版本发布，本轮真实执行仅覆盖 Apple Silicon 上的 Serum 2 2.0.23。

Spire 增加了音色文件识别、设置行和 `.spf` / `.spf2` / `.sbf` / `.swzip` 索引。原生预制与银行统一显示「待适配」，不展开未知银行格式、不自动生成预览；共享 `.fxp` 需人工核对归属。尚无 Spire 真机加载或渲染验证。

## 音源扫描

主工具栏提供「扫描音源」和「扫描音色」两个独立动作。「已发现音源」页也可启动扫描或进入安装目录设置，扫描后直接展示结果。

首次使用和空安装目录列表会采用平台默认位置，并保留已有插件配置推断出的自定义安装目录。macOS 默认位置为：

```text
/Library/Audio/Plug-Ins/VST
/Library/Audio/Plug-Ins/VST3
~/Library/Audio/Plug-Ins/VST
~/Library/Audio/Plug-Ins/VST3
/Applications
~/Applications
```

Windows 默认采用 Program Files 下的 `VstPlugins`、`Steinberg/VstPlugins` 和 Common Files 下的 `VST3`。用户已配置的扫描目录不会被默认值覆盖；不可访问目录不会被静默删除，扫描保留该位置既有发现记录并展示警告。已配置到 `Vital.app/Contents/MacOS/Vital` 的路径会推断至 `.app` 外层安装目录，避免只扫描包内 MacOS 文件夹。Vital 的版本和身份来自产品元数据，包文件名不必固定为 `Vital.app`。

## 数据与升级

程序目录可整体替换；新安装的数据位于独立用户目录：

```text
Windows: %LOCALAPPDATA%\SoundPreviewer
macOS:   ~/Library/Application Support/SoundPreviewer

用户数据目录/
  library.db                     数据库及同目录 WAL
  application.log                运行日志
  Previews/                      WAV / 启动基准 / 同卷临时目录
  Config/preset-categories.json   用户分类规则
  Backups/                       数据库结构升级前的一致快照
  renderers/                     可选 Python 宿主环境、下载与诊断
```

程序目录只包含 App、Worker、Renderers、默认分类规则和可选 Windows x86 宿主。Windows 安装器升级和卸载均不删除用户库。可编辑规则只在首次缺失时从旧版 `Config/preset-categories.json` 导入，随后保存在用户库中；内置默认规则更新不覆盖自定义规则。自选预览磁盘和旧预览绝对路径保持不变，程序更新不触发全量重渲染。

默认定位顺序为：已记住的音色库 → 当前程序目录已有的 `local-data/library.db` → 平台用户目录。旧 Windows v1.1 默认库与新默认库相同，可直接使用。旧便携版在原目录运行 v1.4 后会记住库的位置；若已经更换安装目录，打开「音源与设置 → 试听与存储 → 打开已有音色库」，选择原来的 `local-data` 即可。打开成功后，稳定用户目录中的 `library-location.json` 保存定位，后续版本继续使用。若磁盘断开或原目录被移走，启动会明确提示并允许重新选择，不静默创建空库。

关联旧库不搬动文件；须保留该目录、原音频及外部渲染环境。需要迁移磁盘时，请先保留完整副本并重新关联；本版不自动重写 Windows 与 Mac 间的来源、插件或音频路径。显式 `--data-directory` 优先于上述定位，用于隔离测试时不会记住测试库；需永久关联可加 `--remember-data-directory`。相对参数仍以可执行文件目录为准。

数据库当前保持 schema 2。schema 0/1 的已有库升级前，使用 SQLite backup API 将包含已提交 WAL 页的完整快照保存至 `Backups`，备份失败则中止升级；迁移事务失败回滚。已有 schema 2 不重复迁移，未知更高 schema 拒绝打开。缓存键、ID、收藏、反馈、任务记录、预览路径与新旧音区映射不因应用版本更新而失效。回归测试使用旧库夹具及 WAL 数据，禁止修改真实用户库。

## 构建与运行

构建需要 .NET 8 SDK（仓库固定 8.0.425），首次恢复界面依赖需要访问 NuGet。发布版包含运行时；普通用户无需安装 .NET SDK / Desktop Runtime。

```bash
dotnet restore SoundPreviewer.sln --configfile NuGet.Config
dotnet build SoundPreviewer.sln -c Release --no-restore
dotnet run --project tests/SoundPreviewer.Tests -c Release --no-build
python3 -m unittest discover -s scripts/renderers -p 'test_*.py'
./scripts/Publish-Desktop.sh osx-arm64
./scripts/Publish-Desktop.sh osx-x64
```

Windows 发布：`pwsh -File scripts/Publish-Desktop.ps1 -Runtime win-x64`。可选 x86 宿主仍先单独构建，再发布 Windows 应用。

macOS 从源码运行 `./Start-SoundPreviewer.command`；发布后双击发布目录的 `Start-SoundPreviewer.command`。Windows 双击 `SoundPreviewer.exe`。可选宿主使用匹配插件架构的本地 CPython 3.11 / 3.12，基础依赖固定在 `scripts/renderers/requirements.txt`；Serum 2 额外依赖位于 `requirements-serum2.txt`。可运行 `scripts/Prepare-DawDreamer.sh --include-serum2`（macOS，须指定 Python）或 `scripts/Prepare-DawDreamer.ps1 -IncludeSerum2`（Windows）。完整命令和边界见[宿主配置](rendering.md#可选-dawdreamer-宿主)；没有自动安装商业插件或许可证。

`.github/workflows/desktop.yml` 在 Windows/macOS 运行相同的构建与测试，发布 Windows x64、macOS arm64 / x64。CI 无音频设备检查不替代实机播放和插件验收。

## 验收边界

本次本机为 Apple Silicon。基础设施和界面测试使用明确标注的合成测试音频和文件，这些测试不作为商业音源兼容性的证据。另行执行的真实插件检查与具体样本记录在下表。

当前 Mac 已发现 Vital 1.6.4 和 Serum 2 2.0.23，并执行单样本渲染。Vital 样本是明确标记的 `TEST_ONLY` 测试 patch，由真实 Vital CLI 演奏；Serum 2 样本是本机安装的工厂预制。尚未在两端使用同版本插件、相同预制和外部资源逐样本比较。旧 Windows x86 Sylenth1 二进制不能在 macOS 执行；须安装 macOS 对应版本。相同算法与模板并不保证不同厂商版本、架构或随机音源的波形逐字节一致。

v1.4 已在 Windows 本机完成共用 UI、原生播放、旧库兼容及只读插件扫描；本机未安装 Windows Serum 2，没有完成其真实渲染验收。Intel Mac 的真人交互与商业插件运行仍需实机确认；CI 通过不能代替音色身份核对。尚需使用同版本插件、相同预制与外部资源核对音色、音区、尾音、连续切换和授权状态。旧 FXP 在 Serum 2 中直接加载、FXB 银行、Spire 原生预制及包内条目仍不在已验收范围内。

技术依据：[Avalonia 共用架构](https://docs.avaloniaui.net/docs/fundamentals/cross-platform-architecture)、[DawDreamer 0.8.3 发行说明](https://github.com/DBraun/DawDreamer/releases/tag/v0.8.3)。


## 本次验证结果

v1.4.0 Windows 同步与升级兼容验证（2026-10-18）：

| 检查 | 结果与边界 |
| --- | --- |
| Windows Release / C# | 0 警告、0 错误；37/37，含 WinMM 静音播放、VST3 多架构、停止恢复和旧缓存回归 |
| Python | 共 61 项，60 项通过；仅 Windows 无符号链接权限的检查跳过，路径穿越与 Serum 2 状态转换检查已执行 |
| 共用 UI | 1380×900 / 1040×700、三个主页、五个设置页、试听、关闭回调、10000 项未配置反馈通过，证据 `artifacts/ui-windows-v140-fixture/ui-smoke.json` |
| 旧 Windows 库副本 | 8859 条路径、1019 个重复副本、122 个发现结果加载并通过上述 UI 自检；逐条比对 ID、来源、收藏、反馈、缓存键、预览路径、生成时间、音区与模板均未改变。只操作副本，未扫描或渲染商业插件。证据 `artifacts/ui-windows-v140-legacy/ui-smoke.json` |
| 只读实际插件扫描 | 识别 Serum 1.334 x64、Sylenth1 2.2.1.2 x86、Vital 1.6.4 x64；未加载插件，未发现 Windows Serum 2。证据 `artifacts/windows-parity/plugin-scan.json` |
| 数据安全 | 当前进程库定位固定；旧便携库跨安装定位、无效定位明确失败、规则首次迁移、schema 0/1 一致备份含 WAL、备份失败不迁移、迁移失败回滚、未来 schema 拒绝均通过隔离回归 |

三平台安装包、GitHub Actions 和后续标签发布流程见[版本发布](releases.md)。真实安装覆盖升级及卸载保留数据需在测试朋友的干净环境继续确认。

v1.3.0 新增验证（2026-10-17）：

| 检查 | 结果与边界 |
| --- | --- |
| C# / Python 回归 | C# 35/35，含 macOS 设备播放；Python 57/57，使用 Python 3.12.13 和可选 Serum 2 依赖，状态转换测试实际执行；测试中的合成音频与插件元数据不替代真实样本 |
| 扫描入口与默认目录 | 主工具栏、目录页、发现页扫描按钮在 1380×900 / 1040×700 完整显示；空列表恢复默认、自定义不可访问目录保留、Vital 包外层目录推断通过纯路径检查，UI smoke 不扫描用户安装插件 |
| 共用界面 | 五类音源的渲染设置、两种窗口尺寸、播放、停止、组合筛选和 10000 项未配置反馈通过；原测试 `artifacts/ui-scan-serum2-v130/ui-smoke.json`。最终发布版以两个真实 Serum 2 预览再测，通过加载中关闭及关闭后延迟回调检查（`ClosingCallbacksChecked=true`，退出码 0），证据 `artifacts/publish-osx-arm64/local-data/compatibility-test/ui-smoke.json` |
| Vital 1.6.4 / Apple Silicon | 真实原生 CLI 演奏 `TEST_ONLY_Mac_Vital.vital`；三音区快速预览 1.5 秒，非静音、无削波和非有限值；因 CLI 不报告加载成功，保持待确认 |
| Serum 2 2.0.23 / Apple Silicon | VST3 加载工厂 `WIND - Pan Flute.SerumPreset` 与 `LD - Das EDM.SerumPreset`，逐音区加载后状态回读核对、三参考启动基准及完整 .NET 队列通过；生成 2 项、失败 0 项，每项三音区各 0.5 秒，非静音、无削波和非有限值；第四份 fresh instance 启动 holdout 命中 3/3 并拒绝发布；普通结果保持待确认，尚无本轮人工音色身份确认 |
| 便携发布包 | Windows x64、macOS arm64 / x64 发布成功，共用 Core DLL 一致；arm64 包移除 DOTNET_ROOT 后启动通过，程序目录内宿主安装、自检及真实 Pan Flute 再渲染通过 |
| Spire | 仅索引、设置及未适配保护回归；没有真实插件或音频验收 |

真实插件单样本证据位于 `artifacts/vital-compat/` 和 `artifacts/serum2-compat/`，不提交插件、私人预制或生成音频。Serum 2 队列与启动基准的后续验证结果见[渲染记录](rendering.md#默认启动音色基准)。

以下为 v1.2.0 移植时的历史验证，保留用于区分本次增量：

| 检查 | 结果与边界 |
| --- | --- |
| 共用解决方案 Release 构建 | 0 警告 / 0 错误 |
| C# 回归 | 32/32，含真实 macOS 设备的静音播放、暂停、定位、恢复、结束检测；原有 Windows PE / 选择 / x86 迁移测试使用固定 Windows 上下文保留 |
| Python 桥接纯测试 | 25/25，无商业插件加载 |
| 原生额外检查 | SQLite 中文路径、进程双管道限额、超时、取消、子进程回收、20 次快速播放生命周期均通过 |
| 共用界面 | 1380×900、1040×700；三个主页面、五个设置页、组合筛选、列菜单、去重视图、播放、停止、10000 项未配置反馈、四个后端选项及跨页面 / 窗口尺寸保留修改均通过 |
| 实际插件只读扫描 | 当时本机 89 个 macOS 候选，0 警告；未加载插件，未发现本项目支持的三类音源；此为修复前历史结果 |
| Apple Silicon 发布包 | 自包含 arm64 App / Worker；取消 DOTNET_ROOT 后仍可启动，默认数据实测在发布目录 local-data；空库界面验收退出码 0 |
| Intel Mac / Windows 发布 | 自包含 osx-x64 / win-x64 构建成功；本机无法代替对应硬件的交互和音源验收 |

v1.2.0 证据目录：`artifacts/ui-smoke-macos-final/`（带明确标识的合成音频），`artifacts/default-storage-smoke-osx-arm64/`（发布包默认路径验证）。当前发布目标目录仍为 `artifacts/publish-osx-arm64`、`artifacts/publish-osx-x64`、`artifacts/publish-win-x64`；以窗口右下角版本和该次构建日志确认是否已更新为 v1.3.0。
