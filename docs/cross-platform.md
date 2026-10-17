# Windows / macOS 共用版本

更新：2026-10-17。此页记录本次移植，旧 Windows 实测记录不自动视作新版双平台验收。

## 共用范围

`SoundPreviewer.App` 改为 Avalonia / .NET 8，共用同一套 XAML、事件处理、视图模型和波形控件。三个主页面、五个设置页、组合筛选、可选列、收藏与反馈、三音区播放、任务停止和继续均沿用原有控制器。Core、Worker 和回归测试统一使用 `net8.0`。

两端共用预制识别、MD5 / SHA256、SQLite schema / 迁移、分类规则、JSON 工作进程协议、队列、原子音频提交、半秒裁剪、音区映射、启动基准与结果校验。Windows 继续通过 WinMM 播放；macOS 通过 AudioQueue 播放相同 PCM16 数据。SQLite 分别加载系统的 `winsqlite3.dll` 与 `libsqlite3.dylib`。

macOS 扫描只读解析 XML / binary Info.plist 和 Mach-O，识别 `.app`、`.vst`、`.vst3` 的实际可执行文件及 x64 / arm64 / Universal 架构，不通过加载插件猜测身份。Windows 保留 PE / 版本 / 导出检查；旧发现结果缺少 Platform 时仍解释为 Windows。自动关联只选本平台和当前进程兼容的已识别产品。手选 VST 插件与配置外部宿主仍是不同操作。

Windows 的独立 x86 VST.NET 宿主保留，不加入 macOS 发布。macOS 的 Serum 1 / Sylenth1 FXP 路径使用相同 DawDreamer 协议，接受匹配架构的 VST2 `.vst` bundle；Python、DawDreamer 与插件必须使用相同架构。原来的 MSVC 运行库检查仅在 Windows 执行。Vital 使用各平台独立程序；从 macOS 选择 `.app` 时解析到其真实可执行文件。

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

macOS 从源码运行 `./Start-SoundPreviewer.command`；发布后双击发布目录的 `Start-SoundPreviewer.command`。Windows 双击 `SoundPreviewer.exe`。macOS 可选宿主使用本地 Python 3.11 / 3.12，依赖版本固定在 `scripts/renderers/requirements.txt`；没有自动安装商业插件、Python 宿主或许可证。

`.github/workflows/desktop.yml` 在 Windows/macOS 运行相同的构建与测试，发布 Windows x64、macOS arm64 / x64。CI 无音频设备检查不替代实机播放和插件验收。

## 验收边界

本次本机为 Apple Silicon。测试使用明确标注的合成测试音频和文件，只能证明基础设施与交互；不证明商业音源的兼容性。

当前 Mac 没有安装本项目支持的 Vital / Serum / Sylenth1，无法对相同商业插件版本和真实预制做跨平台听感或逐样本比较。旧 Windows x86 Sylenth1 二进制不能在 macOS 执行；须安装 macOS 对应版本。相同算法与模板并不保证不同厂商版本、架构或随机音源的波形逐字节一致。

尚需 Windows 实机及 Intel Mac 运行验收，并针对同版本合法插件、相同 FXP 与外部资源核对音色、音区、尾音、连续切换和授权状态。Serum 2 专有预制、FXB 银行及包内条目的原有未适配限制继续存在。

技术依据：[Avalonia 共用架构](https://docs.avaloniaui.net/docs/fundamentals/cross-platform-architecture)、[DawDreamer 0.8.3 发行说明](https://github.com/DBraun/DawDreamer/releases/tag/v0.8.3)。


## 本次验证结果

| 检查 | 结果与边界 |
| --- | --- |
| 共用解决方案 Release 构建 | 0 警告 / 0 错误 |
| C# 回归 | 32/32，含真实 macOS 设备的静音播放、暂停、定位、恢复、结束检测；原有 Windows PE / 选择 / x86 迁移测试使用固定 Windows 上下文保留 |
| Python 桥接纯测试 | 25/25，无商业插件加载 |
| 原生额外检查 | SQLite 中文路径、进程双管道限额、超时、取消、子进程回收、20 次快速播放生命周期均通过 |
| 共用界面 | 1380×900、1040×700；三个主页面、五个设置页、组合筛选、列菜单、去重视图、播放、停止、10000 项未配置反馈、四个后端选项及跨页面 / 窗口尺寸保留修改均通过 |
| 实际插件只读扫描 | 本机 89 个 macOS 候选，0 警告；未加载插件，未发现本项目支持的三类音源 |
| Apple Silicon 发布包 | 自包含 arm64 App / Worker；取消 DOTNET_ROOT 后仍可启动，默认数据实测在发布目录 local-data；空库界面验收退出码 0 |
| Intel Mac / Windows 发布 | 自包含 osx-x64 / win-x64 构建成功；本机无法代替对应硬件的交互和音源验收 |

证据目录：`artifacts/ui-smoke-macos-final/`（带明确标识的合成音频），`artifacts/default-storage-smoke-osx-arm64/`（发布包默认路径验证）。发布包位于 `artifacts/publish-osx-arm64`、`artifacts/publish-osx-x64`、`artifacts/publish-win-x64`。版本为 1.2.0。
