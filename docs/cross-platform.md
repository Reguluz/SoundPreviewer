# Windows / macOS 共用版本

更新：2026-10-20，当前为 v0.7.2 开发版。此页记录共用功能与数据约定，历史实测范围按原版本保留。直接在 `main` 开发，不自动创建分支或 PR；major 变更须用户明确确认，所有 `0.x` 标签发布均为 prerelease。

## 共用范围

0.7.2 修复来源下拉框缺少计数、未配好的音源阻止其他设置保存的问题；执行前分别检查宿主与插件，保留离线路径与已知指纹。Windows 内置 Sylenth1 x86 宿主增加逐音色精确状态回读，启动声音不能稳定校准时明确采用该检查，不降低声音比对阈值；未通过回读的输出仍不保存。

0.7.1 修正数据位置：正式运行固定使用程序旁的 Preview，旧库先复制校验再使用；原数据库与原音频保留作回退备份。已有目标库不覆盖，程序目录不可写时明确报错。旧版定位和设置不再使新音频继续生成到外部旧路径。

0.7.0 增加同音源优先调度：保持音源首次出现和组内原顺序，允许并行时连续填充当前组，禁止同源多开时可用异源补位，继续遵守 2 GiB 目标。每项仍独立加载插件。界面增加清晰的「定位音色文件 / 定位音色库文件」按钮，在 Windows 资源管理器或 Mac 访达中选中对应来源；包内条目定位到容器文件。另提供原独立音色的系统复制拖放，源文件与缓存不变。Windows FL Studio 的空白处自动创建音源是优先后续目标，接收方式和验证边界见 [DAW 音色传递](daw-transfer.md)。

`SoundPreviewer.App` 改为 Avalonia / .NET 8，共用同一套 XAML、事件处理、视图模型和波形控件。三个主页面、五个设置页、组合筛选、可选列、收藏与反馈、三音区播放、任务停止和继续均沿用原有控制器。Core、Worker 和回归测试统一使用 `net8.0`。

两端共用预制识别、MD5 / SHA256、SQLite schema / 迁移、分类规则、手工标签、JSON 工作进程协议、队列、原子音频提交、按设定时长裁剪、音区映射及加载防错检查。快速预览默认每音 0.5 秒，可设 0.1–4 秒；音高界面显示 C2 / C4 / C6，对应内部 MIDI 36 / 60 / 84，新预览按中 → 高 → 低排列。Windows 使用 WinMM，macOS 使用 AudioQueue 播放相同 PCM16 数据，SQLite 分别加载系统库。

0.6.0 的并行调度同样共用：新安装默认自动，或手动选 1 / 2 / 4 / 8 / 16 条；自动按物理核数选择目标，再受父进程及全部生成后代合计 2 GiB 的 RSS 目标限制。同一种音源须另行允许多实例，旧设置保留。Windows 以 Toolhelp 进程表和 WorkingSet 计数，Mac 以系统 ps 的父子 PID 与 RSS 采样；物理核探测分别使用 Windows CPU 拓扑与 Mac sysctl，失败时按逻辑线程的一半保守选择。采样式预留、限流与超限停止不等于操作系统硬配额。各平台真实插件性能需分别测量，调度设置不使旧缓存失效。

设置中的「预览生成」提供「生成方式（高级）」与「加载防错检查」：前者选择如何让本机插件演奏并保存预览，后者记录插件未加载预制时的启动声音，避免加载失败后误存默认音色。两端共用同一协议和判断；此检查不会替代人工试听。

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

0.7.1 正式运行将记录、规则和生成缓存固定保存在程序旁的 `Preview`，安装版与 Portable 相同，更新程序时须保留该目录：

```text
Windows: 应用目录\Preview
macOS:   SoundPreviewer.app 旁的 Preview

Preview/
  library.db                     数据库及同目录 WAL
  application.log                运行日志
  <缓存键>.wav                    已提交预览音频
  InitializationBaselines/       加载防错声音参照
  Config/preset-categories.json   用户分类规则
  Backups/                       升级 / 目录迁移前的一致快照
  renderers/                     可选 Python 宿主环境及下载缓存
  diagnostics/                   可选宿主自检诊断
```

Windows 每用户安装目录可写；macOS 应将 `.app` 放到可写的独立目录，例如 `~/Applications/SoundPreviewer`，数据位于其旁，不写入签名 bundle。程序目录不可写时明确报错，不回退到用户目录。Worker、Renderers 和内置默认规则仍跟随程序资源。Windows 安装器升级及卸载保留生成的 `Preview`，macOS 只替换 `.app` 时也保留旁边的数据。发布脚本发现输出中已有 `Preview` 时拒绝清理或打包，即使该目录尚为空。

旧版 `library-location.json`、`local-data` 和旧平台用户库用于查找首次兼容迁移来源，不再覆盖正式运行的数据根。旧库须先复制并校验到当前程序旁的 `Preview`；已有目标库不被另一套旧库覆盖，只归并目标库自身引用的外部音频和加载防错参照。迁移失败或来源不可访问时明确提示，保留原库，不静默续写旧路径。旧定位文件可保留供查找原库，但不能将新音频重定向到旧目录。

Portable 支持免安装及在原目录替换程序升级。移动程序前先退出并备份，连同 `Preview` 一起保留；音色来源、插件与可选 Python 环境的绝对路径仍须人工核对。用户界面不再提供将数据或新音频输出改到任意外部目录的入口。

旧库迁移复制数据库、设置、`Config`、手工标签 / 分类、收藏与引用预览。先建立并校验 SQLite 一致快照和音频副本，再更新目标库的音频与加载防错参照路径。原数据库、规则与音频全部保留作回退备份；不会为了迁入固定 `Preview` 而清除原预览。「清空预览」是独立操作，只处理当前记录或基准明确引用、可确认独占的缓存；共享 / 外部 / 未知音频、未知基准与源文件保留并报告，不凭文件名删除孤立缓存，也不表示整个目录被清空。

设置中的三个维护入口为「打开 Preview 文件夹」「清空预览」「导入旧版音色库」。手动导入须在界面确认；程序先在独立暂存目录复制所选源与全部引用音频，校验副本并验证记录、设置可读，然后才将整个当前 Preview 原子改名为程序旁 `Preview-before-import-*` 并发布暂存库，不合并两套记录。失败且目标空缺时尽量原子恢复原名，明确提示备份位置。备份内保留原 Preview 绝对路径，不能直接作为异名源导入；恢复需退出并备份当前 Preview，再将恢复目录还原名 Preview。

可编辑规则只在首次缺失时从旧规则或内置默认值建立，之后不覆盖。手工标签和分类保存到现有预制 JSON，明确清空也会记住；扫描、内容变更和规则重算保留手工覆盖，「恢复自动」采用最近的自动结果。数据库保持 schema 2，记录及应用设置的未知 JSON 字段保留。缺失的已生成音频保留原映射并提示，不自动清空。

跨 Windows / Mac 搬移时，来源文件与插件安装路径仍需人工关联，不能据文件复制宣称商业插件跨平台可用。普通历史 `--data-directory` 仅指定导入来源；`--smoke-test --data-directory` 保留给构建、测试与隔离诊断，不作为正式运行时的任意外部数据目录入口。内部 `--test-installed-storage` 先运行正式启动的同一迁移流程再执行 UI 自检。Mac `.app` 的固定 Preview 位于其父目录。

数据库当前保持 schema 2。schema 0/1 的已有库升级前，使用 SQLite backup API 将包含已提交 WAL 页的完整快照保存至 `Backups`，备份失败则中止升级；迁移事务失败回滚。首次导入只升级暂存副本，源数据库及其 schema、WAL 中的已提交记录和原文件保留。已有 schema 2 不重复迁移，未知更高 schema 拒绝打开。缓存键、ID、收藏、反馈、任务记录与新旧音区映射不因应用版本更新而失效，迁移只重写需要归入 Preview 的音频路径。回归测试使用旧库夹具及 WAL 数据，禁止修改真实用户库。

## 构建与运行

构建需要 .NET 8 SDK（仓库固定 8.0.425），首次恢复界面依赖需要访问 NuGet。发布版包含运行时；普通用户无需安装 .NET SDK / Desktop Runtime。

```bash
dotnet restore SoundPreviewer.sln --configfile NuGet.Config
dotnet build SoundPreviewer.sln -c Release --no-restore
dotnet run --project tests/SoundPreviewer.Tests -c Release --no-build
python3 -m unittest discover -s scripts/renderers -p 'test_*.py'
bash scripts/Publish-Desktop.sh osx-arm64
bash scripts/Publish-Desktop.sh osx-x64
```

Windows 发布：`pwsh -File scripts/Publish-Desktop.ps1 -Runtime win-x64`。脚本会先发布自包含 x86 宿主，再发布应用和 Worker，普通用户不需要单独安装 x86 .NET Runtime。

macOS 从源码运行 `./Start-SoundPreviewer.command`；发布后双击发布目录的 `Start-SoundPreviewer.command`。Windows 双击 `SoundPreviewer.exe`。可选宿主使用匹配插件架构的本地 CPython 3.11 / 3.12，基础依赖固定在 `scripts/renderers/requirements.txt`；Serum 2 额外依赖位于 `requirements-serum2.txt`。可运行 `scripts/Prepare-DawDreamer.sh --include-serum2`（macOS，须指定 Python）或 `scripts/Prepare-DawDreamer.ps1 -IncludeSerum2`（Windows）。完整命令和边界见[宿主配置](rendering.md#可选-dawdreamer-宿主)；没有自动安装商业插件或许可证。

`.github/workflows/desktop.yml` 在分支推送（包括 `main`）时校验批准 major 并执行 Windows x64、macOS arm64 / x64 构建、测试与安装包流程；Release 入口另核对 tag 与版本。手动运行只生成测试包，推送匹配版本 tag 才公开 Release，`0.x` 强制 prerelease。CI 无音频设备检查不替代实机播放和插件验收。

## 验收边界

以下 Mac 历史实测使用 Apple Silicon。基础设施和界面测试使用明确标注的合成测试音频和文件，这些测试不作为商业音源兼容性的证据。真实插件检查与具体样本按其原版本记录在下表。

当前 Mac 已发现 Vital 1.6.4 和 Serum 2 2.0.23，并执行单样本渲染。Vital 样本是明确标记的 `TEST_ONLY` 测试 patch，由真实 Vital CLI 演奏；Serum 2 样本是本机安装的工厂预制。尚未在两端使用同版本插件、相同预制和外部资源逐样本比较。旧 Windows x86 Sylenth1 二进制不能在 macOS 执行；须安装 macOS 对应版本。相同算法与模板并不保证不同厂商版本、架构或随机音源的波形逐字节一致。

历史 v1.4 Windows 同步验证覆盖共用 UI、原生播放、旧库兼容和只读插件扫描；当时未安装 Windows Serum 2，没有完成其真实渲染验收。Intel Mac 的真人交互与商业插件运行仍需实机确认；CI 通过不能代替音色身份核对。尚需使用同版本插件、相同预制与外部资源核对音色、音区、尾音、连续切换和授权状态。旧 FXP 在 Serum 2 中直接加载、FXB 银行、Spire 原生预制及包内条目仍不在已验收范围内。

技术依据：[Avalonia 共用架构](https://docs.avaloniaui.net/docs/fundamentals/cross-platform-architecture)、[DawDreamer 0.8.3 发行说明](https://github.com/DBraun/DawDreamer/releases/tag/v0.8.3)。


## 本次验证结果

0.7.2 来源数量与渲染设置修复（2026-10-20，程序提交 `21bf0ff`）：

| 检查 | 结果与边界 |
| --- | --- |
| Windows 本机 | Release 0 警告、0 错误；C# 54/54，含原生静音播放。来源计数及多个未配好音源的实际保存、运行配置隔离、页面切换和宿主选择隔离均通过 |
| GitHub 三平台 | Windows x64、macOS arm64 / x64 均通过[最终构建](https://github.com/Reguluz/SoundPreviewer/actions/runs/39443006871)，各平台迁移专项 1/1、C# 53/53、Python 63 项通过；新来源数量与设置保存检查在发布程序及 Mac 封装 app 中均通过 |
| 防错与旧缓存 | 不改变声音门限；替代模式的触发、重开、未知或损坏记录、缺失 / 错误源哈希 / 错误音高 / 错误方案证明、快速预览多轮传播及提交失败保留旧 WAV 均通过；schema 2 与旧预览优先复用保持不变 |
| 真实 Windows 小样本 | 按力度 89、每音 0.5 秒验证 Serum 1.334 x64 一个 FXP、Sylenth1 2.2.1.2 x86 两个 FXP，生成失败 0 项、再次运行全部复用，原输入哈希不变；原生 v2 快速预览的实际状态证明核验通过。此次实际三参考均可校准，替代模式触发由确定性回归覆盖，未声称整库或所有随机启动均已实测 |
| 本地发布程序 | 两种尺寸的空库 UI、实际旧库迁入及无参数重开通过，SourceCountsChecked / RendererSettingsChecked 均为 true；x86 空宿主 ProgramSlotBounds 自检通过。安装包与 Portable 校验通过，706 个 ZIP 条目中无个人数据 |
| CI 安装与封装 | 三平台旧库迁入与重开均保持原文件；Windows 安装、覆盖升级、安装后 UI 及卸载保留数据，ProgramSlotBounds 自检通过；两 Mac 封装 app 的来源数量、设置保存与两种尺寸检查通过，六份安装包及三组诊断已上传 |

0.7.1 安装目录存储修正（2026-10-20，程序提交 `5c46df4`）：

| 检查 | 结果与边界 |
| --- | --- |
| Windows 本机 | Release 0 警告、0 错误；C# 53/53，含原生静音播放。新增迁移检查均使用隔离夹具，不操作真实用户库 |
| GitHub 三平台 | Windows x64、macOS arm64 / x64 最终构建均成功，见[构建记录](https://github.com/Reguluz/SoundPreviewer/actions/runs/39308672278)。各平台旧库迁移专项 1/1、完整 C# 52/52、Python 63 项通过，六份安装包及三组界面诊断已上传 |
| 旧库与缓存 | schema 1 的 live WAL 一致副本迁入、仅副本升级、原 DB 字节与记录保留；只读 closed-WAL 源不生成辅助文件，备份仅复制主文件即可核对原 schema、全部 JSON 和完整性；预览及基准逐文件复制校验、缓存身份和个人元数据不变；同根外部音频归并、名称冲突、缺失引用保留、未知字段保存与恢复中断均通过 |
| 切换与失败恢复 | 普通导入拒绝覆盖；明确替换先验证完整副本，再保留整个旧 Preview；源预览位于当前 Preview 的情况可复制，错误字段类型在切换前拒绝，恢复备份须还原原名；目标不可用不回退至外部目录 |
| 实际启动 | 独立应用副本通过首次迁移与无参数再次启动，记录及有效音频均在该副本程序旁 Preview，原始数据库和音频哈希不变；两种窗口尺寸、设置及个人元数据保存通过 |
| CI 安装与封装 | 三平台发布程序均通过空库与旧库迁入 / 重开自检；受限 Windows 桌面重新布局后仍通过可见区域检查。Windows 安装、同包覆盖升级、安装后 UI 与卸载保持测试数据哈希；两 Mac 封装 app 再次通过 UI 并生成 DMG / ZIP |
| 本地测试包 | Windows 安装包与 Portable 均为 `0.7.1+5c46df4` 对应程序，SHA-256 校验通过；Portable 706 个条目中无个人数据库、预览、日志或定位文件 |

0.7.0 同音源优先与音色文件传递（2026-10-20，程序提交 `7a7a268`）：

| 检查 | 结果与边界 |
| --- | --- |
| Windows 本机 | Release 0 警告、0 错误；C# 52/52，含原生静音播放；新分组回归使用合成音频和真实子进程，不替代商业音源验收 |
| GitHub 三平台 | Windows x64、macOS arm64 / x64 正式构建均成功；C# 各 51/51、Python 各 63 项通过，安装包与界面诊断均已上传，见[构建记录](https://github.com/Reguluz/SoundPreviewer/actions/runs/39293317480)。Windows 首次获取安装器编译器遇到远端服务错误，仅重试该任务后通过，程序提交与检查不变 |
| 同音源与内存 | 验证交错输入稳定分组、4 路同源填槽、边界校准及异源补位；注入低内存时，较小的已测任务可继续、较大的未知音源不启动，无安全任务时停止，解除压力后复用已完成结果并继续 |
| 界面与文件数据 | 1380×900 / 1040×700 下定位和拖出入口可同时滚动至可视区域；原文件 payload、中文 / 空格 / `#` 路径、来源及预览不被改写、拖出时拒绝银行 / 包内 / 未识别 / 缺失 / 音频；Windows 与 Mac 定位命令构造通过。发布版相同检查及 x86 空宿主自检通过 |
| CI 安装与封装 | 三平台发布 UI 均通过两种窗口配置、设置保存、原文件拖放载荷及来源定位命令检查；两 Mac 封装 app 再次通过检查并生成 DMG / ZIP。Windows 安装、同包覆盖升级、安装后 UI 及卸载检查通过，测试数据库和标记文件保持原哈希 |
| 系统与 DAW 边界 | Windows 跨应用拖放实测因 Computer Use 应用访问授权超时而未执行；当前没有实际 FL 加载或自动创建音源的验收。先提供用户认可的文件管理器定位方案，后续目标及官方来源见 [DAW 音色传递](daw-transfer.md) |

0.6.0 自动并行与内存保护（2026-10-20，程序提交 `1ab920b`）：

| 检查 | 结果与边界 |
| --- | --- |
| Windows 本机 | Release 0 警告、0 错误；C# 51/51，含原生静音播放；Python 共 63 项，仅符号链接权限项跳过 |
| GitHub 三平台 | Windows x64、macOS arm64 / x64 正式构建均成功；C# 各 50/50、Python 各 63 项通过，六份安装包及界面诊断产物已上传，见[构建记录](https://github.com/Reguluz/SoundPreviewer/actions/runs/39286934326) |
| 自动档与资源保护 | 实际平台进程树探测及物理核识别、0 / 1 / 2 / 4 / 8 / 16 调度、逐音源校准、独立任务预留通过；注入超过 2 GiB 或连续无法完整监测时停止并恢复未完成项，手动建立参照也受保护 |
| 界面与本地包 | 1380×900 / 1040×700 下六档设置保存、内存显示、10000 项反馈和停止恢复通过；发布版 UI 与随包 x86 空宿主自检通过，安装包和 Portable ZIP 已生成 |
| CI 安装与封装 | 三平台发布 UI 均通过两种窗口配置与设置保存检查；两 Mac 封装 app 再次通过自检并生成 DMG / ZIP。Windows 实际安装、同包覆盖升级、安装后 UI 及卸载检查通过，测试数据库和标记文件保持原哈希 |
| 数据兼容 | 新设置默认为自动，旧缺字段保持串行，已有并行档位及音源许可保留；缓存身份和 schema 2 不变。共享缓存、个人元数据、SQLite 提交失败回滚及停止恢复回归通过 |
| 真实插件小样本 | Serum 1.334 x64 与 Sylenth1 2.2.1.2 x86 各五个 FXP，实际活动峰值达到 1 / 2 / 4。4 条较串行分别约 2.24 / 2.05 倍，进程树 RSS 观测峰值约 886 / 465 MiB；每档仅一次 warm，定时采样可能漏掉短峰。详见[实测与边界](parallel-rendering.md#060-windows-五样本实测2026-10-20)；未运行真实 8 / 16 条或 Mac 商业插件并发 |

0.5.0 并行版本（2026-10-19，程序提交 `5ab46ee`）：

| 检查 | 结果与边界 |
| --- | --- |
| Windows 本机 | Release 0 警告、0 错误；C# 48/48，含原生静音播放；Python 共 63 项，仅符号链接权限项跳过 |
| GitHub 三平台 | Windows x64、macOS arm64 / x64 的 C# 各 47/47、Python 各 63 项通过；发布 UI 自检及安装包生成、上传完成，见[构建记录](https://github.com/Reguluz/SoundPreviewer/actions/runs/39278071968) |
| 安装与界面 | 两种窗口尺寸、并行设置保存及更换音源版本后重置许可通过；Windows CI 的安装、同包覆盖升级及卸载均保留测试数据，Mac 发布目录与封装 app 均通过自检 |
| 并发与数据 | 真实测试子进程验证全局及同音源上限、全部取消、单项崩溃、预算预留和基准单次建立；同键普通 / 强制覆盖、批次外引用、个人元数据、来源变更及 SQLite 提交失败回滚通过 |
| 真实插件小样本 | 本机 Windows 的 Serum 1.334 x64 和 Sylenth1 2.2.1.2 x86，各两个不同内容的 FXP 完成 1 / 2 槽测速；Serum 正反顺序约 1.64–1.65 倍，Sylenth1 约 1.75 倍。详见[实测及限制](parallel-rendering.md#windows-小样本实测2026-10-19)；没有扩大 Mac 商业插件并发的实机验收范围 |

历史 v1.4.0 Windows 同步与升级兼容验证（2026-10-18，保留原结果）：

| 检查 | 结果与边界 |
| --- | --- |
| Windows Release / C# | 0 警告、0 错误；37/37，含 WinMM 静音播放、VST3 多架构、停止恢复和旧缓存回归 |
| Python | 共 62 项，61 项通过；仅 Windows 无符号链接权限的检查跳过，路径穿越与 Serum 2 状态转换检查已执行 |
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

v1.2.0 证据目录：`artifacts/ui-smoke-macos-final/`（带明确标识的合成音频），`artifacts/default-storage-smoke-osx-arm64/`（当时发布包默认路径验证）。构建输出目录仍为 `artifacts/publish-osx-arm64`、`artifacts/publish-osx-x64`、`artifacts/publish-win-x64`；当前构建以窗口右下角版本和当次日志核对，历史证据目录不随新版本改写。
