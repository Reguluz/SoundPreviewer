# SoundPreviewer

Windows 本地合成器预制音色库，使用 C#、.NET 8 和 WPF 开发。SQLite 保存来源、预览映射、任务状态与个人反馈，浏览试听不需要启动 DAW。基础应用无需第三方 NuGet 包，也不调用云端服务。

当前交付为可运行的首个开发版本。目录扫描、检索、收藏、反馈、播放、任务恢复和独立渲染进程已实现；真实音源的支持范围见 [渲染验证记录](docs/rendering.md)。AI 向量搜索、个人推荐、FXB 银行槽位遍历与 macOS 尚未实现。

## 启动

需要 Windows 10/11、.NET 8 Desktop Runtime；从源码构建还需要 .NET 8 SDK。仓库固定 SDK 8.0.425，允许更新的 .NET 8 功能版本。

双击仓库中的 `Start-SoundPreviewer.cmd`，或使用下面的启动命令。

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-SoundPreviewer.ps1
```

启动脚本将本地数据保存在仓库的 `local-data`，不会提交到 Git。直接打开 `SoundPreviewer.exe` 时，默认数据目录为 `%LOCALAPPDATA%\SoundPreviewer`。可通过 `--data-directory` 指定另一音色库。

## 配置与使用

1. 在「音源与设置」中按音源添加预制目录。FXP 的所属音源由配置决定，不能仅靠扩展名推断。
2. 为 Vital 选择已安装的独立程序，并选择「Vital 原生」方式。点击「采用 Vital 原生参数」匹配其固定力度与尾音比例。
3. 可选「DawDreamer」方式使用具备宿主依赖的 Python 和对应乐器 DLL。位数必须匹配；桥接为 x64。本机 DawDreamer 在加载插件前初始化失败，暂保留未配置状态；真实兼容结果和离线依赖说明见渲染记录。
4. 保存实际版本、三音区 MIDI 音高、BPM、时长和存储预算，然后扫描目录并生成预览。
5. 试听低、中、高三个音区。待确认预览须由用户核对对应关系后确认；静音与削波提示保留在详情中。

支持 `.vital`、`.vitalbank`、`.fxp`、`.SerumPreset` 和 `.fxb` 的对应扫描规则。Vital 包仅索引内部条目；未验证的银行会显示「待适配」，不会伪造音色槽位或预览。Serum 2 目前可索引，需另行实现真实适配器。

「外部工作进程」接受自定义本地渲染器，采用 JSON 请求、结果映射和适配器版本标记，详见 [进程协议](docs/rendering.md)。程序不会执行 shell 命令模板。

## 构建与验证

```powershell
dotnet restore SoundPreviewer.sln --configfile NuGet.Config
dotnet build SoundPreviewer.sln -c Release --no-restore
.\tests\SoundPreviewer.Tests\bin\Release\net8.0-windows\SoundPreviewer.Tests.exe
dotnet publish src/SoundPreviewer.App/SoundPreviewer.App.csproj -c Release --no-restore -o artifacts/publish
```

测试覆盖实际 WAV 格式和异常值、SQLite 事务与恢复、扫描增量与反馈、音色包资源变化、进程超时、取消、原子提交和有限重试。测试中的声音和子进程仅用于基础设施验证，不能作为插件兼容性的证据。

默认运行 12 组无需音频设备的测试。可添加 `--test-playback`，在具备默认音频设备的 Windows 上进行第 13 组静音播放测试，检查暂停、跳转、继续和释放。

UI 可用隔离音色库生成两种窗口尺寸、三个页面的截图并自动退出：

```powershell
.\src\SoundPreviewer.App\bin\Release\net8.0-windows\SoundPreviewer.exe --data-directory .\artifacts\ui-smoke --smoke-test
```

## 数据与维护

所有预制文件均只读。索引使用 Windows 自带的 `winsqlite3.dll`；Vital 预览使用原生 PCM16，DawDreamer 桥接保留 float32 原始输出，先校验临时文件，再原子提交。试听通过 WinMM 输出内存中的 PCM16 音频，独立音量不修改预览文件，也不依赖 Windows Media Player。缓存包含音色内容、插件/程序指纹、实际版本、试听模板及适配器版本。单文件外部资源的变更跟踪仍需专用适配器补充。

不会自动下载模型或商业音源，不包含用户预制。可选 DawDreamer 及其他组件的发行许可需在正式打包前逐项核对。当前仅调用用户安装的音源；本仓库未替用户选定项目发行许可证。

规划依据：[项目计划](docs/项目计划.md)。实现与后续阶段：[架构](docs/architecture.md)、[开发状态](docs/roadmap.md)。
