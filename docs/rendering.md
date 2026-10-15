# 渲染后端与实测边界

核对日期：2026-10-15。此仓库的桌面界面、调度器与工作进程使用 C# / .NET 8；插件渲染发生在独立进程。应用不会产生模拟合成器音频，也不会安装、下载或绕过插件授权。

## 当前支持

| 组合 | 已实现 | 真实插件验收状态 |
| --- | --- | --- |
| Vital 独立程序 + 单文件 `.vital` | 三次原生命令行渲染、拼接、WAV 检查 | 本机已生成 138 项真实预览，全部待人工确认加载对应关系与外部资源 |
| Vital `.vitalbank` | 索引可保留来源；原生渲染拒绝银行 / 内部条目 | 需先在 Vital 导入并导出独立预制 |
| Serum 1 VST2 + `.fxp` | 可选 DawDreamer 真实宿主桥接 | Serum 1.334 x64 已完成一个实际 FXP 的生成和播放，用户确认三音区正常；尚未验证批量 |
| Serum 2 + `.SerumPreset` | 外部工作进程协议；未内置专有加载器 | 未验证 |
| Sylenth1 VST2 + `.fxp` | 独立 x86 VST.NET 宿主；DawDreamer 仍只支持 x64 | 本机 2.2.1.2 x86 已生成、播放一个实际 FXP，用户确认三音区正常；尚未验证批量 |
| Sylenth1 银行槽位 | 外部工作进程协议；未内置槽位选择 | 未验证，不能把整个银行当单个预制 |

已检查 `C:\Program Files\Common Files\VST3`、常见 `VstPlugins` / `VST2` 目录、Program Files 顶层产品目录，未发现上述三个产品的二进制。已发现 `C:\Users\Reguluz\Documents\Xfer\Serum Presets`，目录存在不能证明插件版本或离线授权可用。检查范围有限，用户可指定其他位置的插件。

随后依据用户提供的位置，检查 `D:\MusicStation\VSTi(64bit)\Sylenth1`：发现 4,260 个 `.fxp`、519 个 `.fxb`，插件 `Sylenth1.dll` 文件 / 产品版本为 2.2.1.2，PE machine 为 `0x014C`（x86 / 32 位）。目录名称中的 64bit 与二进制架构不符；未来宿主需单独的 x86 进程或真正的 x64 插件。未读取授权文件内容，未判断授权可用性。用户提供的 Serum 预制目录 `C:\Users\Reguluz\Documents\Xfer\Serum Presets\Presets` 中有 613 个 `.fxp`。这些数量表示文件数，不能等同于银行展开后的音色数。

在用户提供的 MusicStation 目录顶层，进一步发现 `D:\MusicStation\VSTi(64bit)\Serum_x64.dll`，产品版本 `1,3,3,4`（对应 Serum 1.334），PE machine `0x8664`（x64）。同目录的 `SerumFX_x64.dll` 为效果器，不用于音色演奏。

## 本机验证记录

### Serum 单音色已通过

2026-10-15 使用本地 Serum 1.334 x64 和 `BA Analog Pluck [SN].fxp`，通过完整的 C# Worker → DawDreamer 桥接生成了 12 秒真实音频。模板为 MIDI 36 / 60 / 84、力度 100、每音区按键 2 秒及尾音 2 秒、BPM 120、44100 Hz / 立体声 float32。三个音区 RMS 分别为 0.071275、0.060060、0.046171，峰值分别为 0.703617、0.713867、0.717470；没有静音、削波或非有限值。原始 DLL 与 FXP 的哈希、大小、修改时间未变。

已通过应用相同的 WinMM 播放路径低音量播放，用户确认“三段预览正常”。这是单个预制的生成与试听验收，不代表全部预制、外部资源、离线授权或批量切换已经通过。索引仍保留 `NeedsReview`，不把能听见自动等同于已经完成独立的预制身份对照。

本地验证报告在被 Git 忽略的 `artifacts/serum-single-20261015`。FXP SHA256 为 `f673b153387b0137189aacaa912d3425161cc9b64eec49cdd837dbfe695ae051`，DLL SHA256 为 `82ead932eb342579cd9d55a811b664e8ab06e762307acbc4f4abef5b4fb24f66`，验收 WAV SHA256 为 `b14614dea887978b8fe100ffe5e9e23153e4d5813306e40f0853024c67b4216a`。私人预制、插件及音频不提交仓库。

### DawDreamer 故障定位与修复

已在项目 `artifacts/renderer-env` 创建独立 x64 Python 3.11.7 环境，下载官方 wheel 并在断网安装模式下安装 DawDreamer 0.8.3 / NumPy 1.26.4。两个包的导入成功，未修改全局 Python。

使用两个不同的 Serum 工厂 FXP，分别配置 MIDI 36 / 60 / 84、力度 100、按键 2 秒、尾音 2 秒、BPM 120、44100 Hz，调用真实 C# Worker / Python 桥接。两次尝试都在创建 `RenderEngine` 阶段退出，退出码为 `0xC0000005`，未产生任何 WAV；目标插件与预制尚未加载。随后仅执行 `RenderEngine(44100,128)`、完全不指定商业插件 / 预制，在沙箱内和沙箱外也出现同样初始化失败。

在另一个独立环境 `artifacts/renderer-env-090` 对官方 DawDreamer 0.9.0 进行了单次有界诊断：导入成功，新的空 `RenderEngine` 构造也未成功返回。发行桥接继续固定 0.8.3，未将未完成的替代版本当作已验证依赖。

上述旧结果只能说明宿主初始化存在问题，不能归因于 Serum 预制或授权。失败记录仍保存在被 Git 忽略的 `artifacts/render-validation`，作为诊断历史；后续成功结果以上节为准。

原生调试定位到实际加载的 Anaconda 私有 `MSVCP140.dll` 14.27.29016.0。系统虽已有较新运行库，进程仍使用了旧 DLL。改用标准 x64 CPython 3.12.14 的独立环境、保持 DawDreamer 0.8.3 / NumPy 1.26.4 不变后，空宿主构造、处理和释放均通过，实际 MSVCP 为 14.51.36247.0。未覆盖系统或 Anaconda DLL。Microsoft [STL 17.10 发行说明](https://github.com/microsoft/STL/releases/tag/vs-2022-17.10) 记录了相关 mutex 运行库兼容边界；本机诊断据此增加实际已加载 C++ 运行库检查。

随后插件初始化的另一处失败发生于受限沙箱读取 Serum 用户配置时。同一请求在正常用户权限下成功生成，未改插件、预制或授权文件。此现象与旧运行库问题分开处理，不通过修改插件来规避权限或授权。

2026-10-15 仅对既有环境做了一次 8 秒上限的空宿主复测：导入成功，`RenderEngine(44100, 128)` 在约 1.8 秒后以 `0xC0000005` 退出，未加载任何商业插件或预制。当前默认音色库中的 Serum 1 渲染器仍未配置，1513 条记录的尝试次数均为 0；这表示尚未尝试加载，不是所有 FXP 都验证失败。

同日只读核对默认音色库，发现 138 个 Vital WAV，三音区映射完整，约 181 MiB，全部为 `NeedsReview`，其中 86 项有削波风险提示。最近一轮生成 138 项、失败 0 项，其余 6417 个可处理项因 Serum 1 / Sylenth1 未配置渲染器而跳过。界面现在将 `Ready` 和已有音频的 `NeedsReview` 一并纳入已生成视图，但不会自动把待确认结果改为已验证。

「当前渲染状态」依据当前已保存配置和插件扫描元数据检查位数、格式、程序与模板；“可尝试”不是实际加载验证。32 位 Sylenth1 需要兼容的独立 x86 宿主或桥接，不要求用户必须更换 64 位音源包；不能仅通过移除位数检查声称支持。

下载缓存包括官方 0.8.3 / 0.9.0 Windows cp311 x64 wheel，分别核对了官方 PyPI SHA-256 `f603f96e110b1d581f108a3d5f62a09a56cb963bf7f84a0dbbf717e108a5b2a6` / `4899f18eb6c3e99daff1ac3ff01d55cb739a30d8b669c2ba467b186ec01b2437`。0.8.3 依赖安装后未发生运行期下载。

### Sylenth1 x86 单音色

使用本地 `Sylenth1.dll` 2.2.1.2（PE x86）与 `Sylenth1 - Bass Saw.fxp`，通过完整 C# Worker → 独立 x86 VST.NET 宿主生成并低音量播放 12 秒预览。采用与 Serum 相同的三音区模板；RMS 为 0.089551、0.121580、0.049655，峰值为 0.653464、0.670866、0.593902，没有静音、削波或非有限数值。用户确认“三段都正常”，插件报告音色名称与请求一致；索引仍标记待确认，尚不独立证明音色身份或全部资源正确。

本地报告在 `artifacts/sylenth-single-20261015`。FXP SHA256 为 `3aaea00d671661da02d9ca605fdbe107033ed5598222aa32ca711fd566e45841`，DLL SHA256 为 `78efa99c808ba2a0b314898d2ee38585103f8cff7c97b967ace72fe71b9075cb`，首轮验收 WAV SHA256 为 `4c0a603331fc986331064b2d6024d945208ae150a7de4df0f92435ba5acde3e6`；源文件与插件的哈希、大小、时间均未改变。此样本来自用户已有音色包，不宣称为已确认的工厂预制。

最终发布包对同一个 FXP 的复测也已通过，使用带 `fact` 块的 float32 WAV；报告为 `sylenth-bass-saw-published-summary.json`。该次未重复播放，人工试听证据来自前一轮相同插件与预制的三音区预览；两轮均非静音、无削波，未据两次输出的指纹差异推断音色身份。

## Vital 原生参数

依据 [Vital main.cpp](https://github.com/mtytel/vital/blob/main/src/standalone/main.cpp) 和 [synth_base.cpp](https://github.com/mtytel/vital/blob/main/src/common/synth_base.cpp)，本适配器使用当前公开源码的如下行为：

- `--length` 读取整数，并直接作为 `renderAudioToFile` 的秒数；不是拍数。版本变化须重新验收。
- `--midi` 单音高、`--bpm` 整数 BPM；三个音高分别启动新进程，避免上一音符残留。
- 原生输出固定 44100 Hz、立体声、16-bit PCM，力度固定 normalized 0.7，约等于 MIDI 89。
- 尾音固定为按键时长的 30%，最后有短淡出；不能任意选择原生尾音或力度。
- 处理块为 64 帧，实际 Note Off 和末尾长度有不足一块的舍入；索引保存各音符实际时长、力度及音区位置。
- 48000 Hz 预览由本应用对真实 44100 Hz 输出进行窗函数 sinc 重采样；不是插件原生 48000 Hz 渲染。

不匹配的整数时长 / 力度 / 尾音会返回明确错误。界面可采用原生参数模板。三段原生 WAV 不应用响度归一化，源文件不会被修改。输入复制到本次任务的私有暂存目录，以相对路径传入，兼容源码的 Windows 路径处理，并避免 Vital 在用户预制目录写入 WAV。预制读取后核对扫描哈希，以同一份字节渲染三个音高。

**加载确认限制**：当前 Vital CLI 忽略 `loadFromFile` 的返回值。即使退出码为 0 且音频非静音，也无法证明加载了目标预制。应用先检查 JSON `settings` 和可解析的版本信息，再将所有原生渲染标记为待确认。用户须比对预制名称 / 实际演奏，检查外部采样、波表与资源。暂存复制保留 JSON 中已有资源内容及绝对路径，但不复制不明的相对资源；这类预制必须人工确认。

## 工作进程协议

主程序调用：

```text
SoundPreviewer.Worker.exe --request <绝对 request.json 路径> --result <绝对 result.json 路径>
```

JSON 属性见 `SoundPreviewer.Core.Models.RenderRequest` / `RenderResult`，枚举按默认 System.Text.Json 数字序列化：Vital=0、Serum1=1、Serum2=2、Sylenth1=3；Unconfigured=0、VitalCli=1、ExternalWorker=2、DawDreamer=3。请求含源预制身份、插件配置、试听模板、缓存键和本次暂存 WAV 输出路径。渲染器只能将成功结果提交到请求的输出路径。

外部后端使用相同 `--request` / `--result` 参数，无命令行 shell。用户指定实际外部渲染器可执行文件及插件路径。外部程序须：

1. 实例化用户指定的真实音源，并明确验证专有预制或银行槽位加载。
2. 以请求的三个 MIDI 音高、力度、BPM、按键和尾音时长生成音频。
3. 返回 `Success`、`OutputPath`、`AdapterVersion`、`NeedsReview`、`Message` 和三个 `Notes` 映射。每项含 `MidiNote`、`StartSeconds`、`DurationSeconds`、`ActualHoldSeconds`、`ActualTailSeconds`、`ActualVelocity`。可附 normalized 力度及音区指标。
4. 输出完整 WAV 后写入结果 JSON；失败返回 `Success=false` 与原因，并使用非零退出码。无法确认目标加载时必须设置 `NeedsReview=true`。

主工作进程和桌面端都独立检查 WAV，不信任外部提供的指标。每个音区须按顺序且不重叠，时间必须覆盖合理范围且位于文件内。外部执行文件不能指向 SoundPreviewer.Worker 本身。外部宿主应不留后台子进程，并在授权弹窗等无法继续状态返回错误。

[DawDreamer 官方 API](https://github.com/DBraun/DawDreamer/blob/main/docs/user_guide/plugin_processor.rst) 提供 VST2 `load_preset(.fxp)`、VST3 `load_vst3_preset(.vstpreset)` 与 MIDI 演奏。这不能直接证明 Serum 的 `.SerumPreset`、Sylenth1 `.fxb` 或特定版本已兼容。本项目未把第三方工具的支持声明视为真实验收。

## 可选 DawDreamer 宿主

`scripts/renderers/dawdreamer_worker.py` 随 C# Worker 发布到 `Renderers` 子目录。主界面的 DawDreamer 后端配置使用 `Plugin.ExecutablePath` 指定 Python 可执行文件，`Plugin.PluginPath` 指定 VST2 乐器 DLL。桌面应用与调度仍为 C# / WPF；Python 仅作为隔离的可选本地插件宿主。

固定依赖为 **DawDreamer 0.8.3、NumPy 1.26.4、x64 Python 3.11 或 3.12**。选择此组合是为了固定已核对的 VST2 FXP API 和现有 Windows wheel；不会随运行自动升级。依据 [v0.8.3 PluginProcessor 源码](https://github.com/DBraun/DawDreamer/blob/v0.8.3/Source/PluginProcessor.cpp)，`load_preset` 通过 JUCE 的 FXP 加载接口返回成功标志，桥接在返回 false 时拒绝生成。依据 [v0.8.3 RenderEngine 源码](https://github.com/DBraun/DawDreamer/blob/v0.8.3/Source/RenderEngine.cpp)，秒制 MIDI 与渲染时长按采样帧保存，脚本记录由真实输出帧数决定的时间。

桥接仅接收 Serum 1 / Sylenth1 的独立 `.fxp`，检查文件头、扫描哈希及 DLL 的 x64 PE 架构。每个音高创建新插件实例，加载同一份不变的 FXP 快照，以指定 BPM 和 MIDI 力度演奏，保留请求的释放时间与插件自身效果。输出为真实插件的 IEEE float32 WAV 数字音频，保留原始输出，包括超出 ±1 的峰值，不进行削峰 / 响度归一化，也不使用系统回录。无需打开插件界面；授权弹窗、崩溃与挂起由工作进程的时间限制处理。当前新宿主组合一律标记待确认，须人工抽查预制与资源；静音 / 削波另外记录。

不支持 x86 DLL、SerumFX、VST3 下的 `.fxp`、`.SerumPreset`、`.fxb` 或银行槽位。Sylenth1 本机 x86 插件会在加载宿主前返回可理解的架构错误。

建议使用标准 CPython，而不是携带旧私有运行库的 Conda。下面的脚本仅在工作区建立独立环境，固定官方依赖并运行有 8 秒时限的空宿主自检；不加载商业插件，不修改全局 Python。示例中的 Python 路径应替换为本机标准 x64 CPython 3.11 / 3.12 的实际安装路径：

```powershell
pwsh -File scripts/Prepare-DawDreamer.ps1 -PythonPath 'C:\Python312\python.exe'
# 已有环境也可单独自检，不加载插件或生成 WAV：
& '.\artifacts\dawdreamer-cpython-x64\Scripts\python.exe' scripts/renderers/dawdreamer_worker.py --probe --result artifacts/host-probe.json
```

脚本分开下载官方 wheel 和离线安装，缓存仅在 `artifacts` 中；已满足固定依赖的环境跳过安装。返回的 `EnvironmentPythonPath` 用于设置中的 Python 程序路径。应用运行时只导入本地依赖，不访问网络。桥接同时检查实际加载的 C++ 运行库，过旧时直接提示；自检通过仅表示空宿主可运行。DawDreamer 的 GPLv3 与所带 JUCE 等依赖许可证在发行宿主二进制时须保留并核对。应用不随发行包包含商业插件、授权文件或私人预制。

## 单音色验收工具

`scripts/Validate-SinglePreset.ps1 -RequestPath <request.json> [-Play] [-Volume 0.15]` 经完整工作进程链只处理一个独立预制；拒绝银行槽位、覆盖已有验收输出或写入工作区 `artifacts` 以外。逐段检查三音区、记录 WAV 指纹并验证源 FXP 与 DLL 未改变。`-Play` 使用应用相同的播放器，不是系统录音。

`scripts/Open-ValidatedSamples.ps1 -SummaryPath <summary.json>` 仅将通过校验的真实结果导入新建的隔离试听库并打开 WPF 应用；不覆盖原库，不添加扫描根目录，不自动生成批量预览。可加 `-NoLaunch` 只建立样本库，使用 `-DataDirectory` 指定新的 `artifacts` 子目录。

## 可选 32 位 Sylenth1 宿主

`src/SoundPreviewer.Vst2Host` 使用独立 x86 .NET 8 进程及固定的 [VST.NET2-Host 2.1.0](https://www.nuget.org/packages/VST.NET2-Host/2.1.0)，不需要 Python，也不把 32 位 DLL 加载进 WPF 主进程。目标电脑需安装 x86 .NET 8 Desktop Runtime。只处理 Sylenth1 的独立 `.fxp`，拒绝 `.fxb`、包内条目、其他音源和 x64 DLL。校验源 SHA256、PE 架构、FXP / 实际插件 ID；chunk 加载检查返回值与 program 状态变化，无法取得加载证据时拒绝生成。每个音高使用新插件实例，输出保留 float32 原始音量；结果始终先待确认。

此可选项目不加入主解决方案，基础 WPF 构建仍无需第三方 NuGet 包。单独构建需要从官方 NuGet 恢复依赖：

```powershell
pwsh -File src/SoundPreviewer.Vst2Host/build.ps1
dotnet publish src/SoundPreviewer.App/SoundPreviewer.App.csproj -c Release -o artifacts/publish
```

主应用构建 / 发布时，将已构建的 `artifacts/vst2host-x86` 完整复制到 `Vst2Host-x86` 子目录。不能只复制宿主 EXE，须保留 Jacobi / Ijwhost DLL、配置、依赖与许可证。包含该宿主的版本在插件扫描后可自动关联 Sylenth1 x86 VST2；已有自定义外部渲染器不会被覆盖。无可选宿主时仍不自动启用 x86。工作进程只对这个内置宿主子进程清除可能误指向 x64 的通用 `DOTNET_ROOT`，不改系统或父进程环境。

VST.NET 为 LGPL-2.1，独立动态 DLL 保持可替换，许可证与源码 / 重建信息随宿主保留，见宿主目录 `THIRD-PARTY-NOTICES.md`。不分发 Steinberg SDK、商业插件、授权文件或私人音色。

## 独立试听输出

本机的 WPF `MediaPlayer` 解码测试返回 `InvalidWmpVersionException` / `0x88980507`，缺少它要求的 Windows Media Player 组件。应用改用 Windows 自带 WinMM `waveOut` 播放，不要求安装或修改操作系统功能。

`WavePlayer` 读取已验证的 WAV，在内存中转换为 16-bit PCM 供默认音频设备试听；原始预览文件、float32 峰值与分析结果保持原样。支持播放 / 暂停 / 继续、按秒定位、音区切换、停止、音量和播放完成检测。[Microsoft 音量接口说明](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutsetvolume) 指定使用打开的设备句柄只影响该实例，本应用不使用全局设备编号调节音量。

播放器在释放音频指针前调用 [waveOutReset](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutreset)，等待缓冲区解除准备并关闭设备。位置按 [waveOutGetPosition](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutgetposition) 实际返回的字节数、采样数或毫秒换算。设备不存在、格式拒绝或驱动错误会显示具体原因。

用于此播放路径的 IEEE float32 测试 WAV 明确标记为 `TEST AUDIO`；它用于解码和控制验证，不属于插件渲染结果或音色兼容性证据。

## 音频校验与故障隔离

支持 RIFF WAV PCM 8/16/24/32 位和 IEEE float 32/64 位（包括对应 WAVE_FORMAT_EXTENSIBLE），单声道或立体声。检查格式头、块边界、帧对齐、音频时长与非有限数值。单文件上限 256 MB / 300 秒。

RMS 小于 0.00001 记为疑似静音；峰值达到 0.999 记为接近削波。这些线索进入待确认状态，不自动等同于预制失败。非有限数值、截断文件、路径不符、无效音符映射或异常退出不会提交。

每次任务在目标预览目录的同卷私有目录暂存请求、结果和音频。验证完成后移动 WAV 到最终缓存路径；失败 / 取消清理本次暂存文件。进程加入设置了退出清理的 Windows Job Object，主程序超时或取消会结束工作进程及其子进程，成功返回后也清理残留子进程。工作进程捕获插件失败并返回可展示原因。插件不能拖住主界面。

当前 Serum 与 Sylenth1 x86 单音色均已完成真实生成及用户试听，后续需验证连续切换、外部资源、银行槽位和离线授权。用户要求先验收单音色，再推进批量；本轮不启动批量生成。源码及第三方工具的支持不能替代真实兼容性验收。
