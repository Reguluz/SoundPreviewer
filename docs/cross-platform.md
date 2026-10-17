# Windows / macOS 共用版本

更新：2026-10-17，当前版本 v1.3.0。此页记录移植与双平台后续改进，旧 Windows 实测记录不自动视作新版双平台验收。

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

## 便携目录

发布目录内：

```text
SoundPreviewer[.exe]              桌面入口
SoundPreviewer.Worker[.exe]       同平台独立工作进程
Start-SoundPreviewer.command      macOS 双击入口
Config/
  preset-categories.default.json  随包默认规则
  preset-categories.json          首次启动建立的可编辑规则
Renderers/                       可选宿主桥接
local-data/
  library.db                     数据库及同目录 WAL
  application.log                运行日志
  Previews/                      WAV / 启动基准 / 同卷临时目录
```

默认路径以 `AppContext.BaseDirectory` 为准，不受终端工作目录影响，不写入 AppData / Application Support。设置中的生成与导出路径须位于程序目录内。旧的外部预览目录在启动时调整到便携目录，旧预览记录本身仍可读取，源预制保持只读。

`--data-directory` 是明确的目录覆盖，可用于既有库与隔离验收；显式指定时也允许在该目录下生成文件。相对参数以程序目录为准。未传此参数时不会自动打开或搬移旧 Windows `%LOCALAPPDATA%/SoundPreviewer`。使用旧库前备份数据库，并核对程序右下角版本提示中的库路径；Windows 路径不能在 Mac 上自动映射，Mac 需重新配置本地来源与插件位置。

程序文件夹须可写。复制或升级时保留 `local-data` 与自定义分类规则。macOS 当前采用可写的便携文件夹及 `.command` 启动入口，不要求把数据写入签名的 `.app` bundle。

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

Windows 实机及 Intel Mac 运行验收仍未完成；发布构建通过不能代替目标平台运行。尚需针对同版本插件、相同预制与外部资源核对音色、音区、尾音、连续切换和授权状态。Serum 2 其他版本、旧 FXP 直接加载、FXB 银行、Spire 原生预制及包内条目仍不在本轮已验收范围内。

技术依据：[Avalonia 共用架构](https://docs.avaloniaui.net/docs/fundamentals/cross-platform-architecture)、[DawDreamer 0.8.3 发行说明](https://github.com/DBraun/DawDreamer/releases/tag/v0.8.3)。


## 本次验证结果

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
