# 渲染后端与实测边界

核对日期：2026-10-15。此仓库的桌面界面、调度器与工作进程使用 C# / .NET 8；插件渲染发生在独立进程。应用不会产生模拟合成器音频，也不会安装、下载或绕过插件授权。

## 当前支持

| 组合 | 已实现 | 真实插件验收状态 |
| --- | --- | --- |
| Vital 独立程序 + 单文件 `.vital` | 三次原生命令行渲染、拼接、WAV 检查 | 本机已生成 138 项真实预览，全部待人工确认加载对应关系与外部资源 |
| Vital `.vitalbank` | 索引可保留来源；原生渲染拒绝银行 / 内部条目 | 需先在 Vital 导入并导出独立预制 |
| Serum 1 VST2 + `.fxp` | 可选 DawDreamer 真实宿主桥接 | 已发现 Serum 1.334 x64；本机宿主初始化失败，未生成预览 |
| Serum 2 + `.SerumPreset` | 外部工作进程协议；未内置专有加载器 | 未验证 |
| Sylenth1 VST2 + `.fxp` | 当前 DawDreamer 桥接仅支持 x64；外部宿主协议不禁止 x86 | 本机 2.2.1.2 为 x86，需要兼容 32 位的独立宿主或桥接；本应用尚未提供或验证该宿主 |
| Sylenth1 银行槽位 | 外部工作进程协议；未内置槽位选择 | 未验证，不能把整个银行当单个预制 |

已检查 `C:\Program Files\Common Files\VST3`、常见 `VstPlugins` / `VST2` 目录、Program Files 顶层产品目录，未发现上述三个产品的二进制。已发现 `C:\Users\Reguluz\Documents\Xfer\Serum Presets`，目录存在不能证明插件版本或离线授权可用。检查范围有限，用户可指定其他位置的插件。

随后依据用户提供的位置，检查 `D:\MusicStation\VSTi(64bit)\Sylenth1`：发现 4,260 个 `.fxp`、519 个 `.fxb`，插件 `Sylenth1.dll` 文件 / 产品版本为 2.2.1.2，PE machine 为 `0x014C`（x86 / 32 位）。目录名称中的 64bit 与二进制架构不符；未来宿主需单独的 x86 进程或真正的 x64 插件。未读取授权文件内容，未判断授权可用性。用户提供的 Serum 预制目录 `C:\Users\Reguluz\Documents\Xfer\Serum Presets\Presets` 中有 613 个 `.fxp`。这些数量表示文件数，不能等同于银行展开后的音色数。

在用户提供的 MusicStation 目录顶层，进一步发现 `D:\MusicStation\VSTi(64bit)\Serum_x64.dll`，产品版本 `1,3,3,4`（对应 Serum 1.334），PE machine `0x8664`（x64）。同目录的 `SerumFX_x64.dll` 为效果器，不用于音色演奏。

## 本机验证记录

已在项目 `artifacts/renderer-env` 创建独立 x64 Python 3.11.7 环境，下载官方 wheel 并在断网安装模式下安装 DawDreamer 0.8.3 / NumPy 1.26.4。两个包的导入成功，未修改全局 Python。

使用两个不同的 Serum 工厂 FXP，分别配置 MIDI 36 / 60 / 84、力度 100、按键 2 秒、尾音 2 秒、BPM 120、44100 Hz，调用真实 C# Worker / Python 桥接。两次尝试都在创建 `RenderEngine` 阶段退出，退出码为 `0xC0000005`，未产生任何 WAV；目标插件与预制尚未加载。随后仅执行 `RenderEngine(44100,128)`、完全不指定商业插件 / 预制，在沙箱内和沙箱外也出现同样初始化失败。

在另一个独立环境 `artifacts/renderer-env-090` 对官方 DawDreamer 0.9.0 进行了单次有界诊断：导入成功，新的空 `RenderEngine` 构造也未成功返回。发行桥接继续固定 0.8.3，未将未完成的替代版本当作已验证依赖。

因此当前证据只证明 **本机宿主初始化存在问题**，不能归因于 Serum 预制、Serum 授权或 DLL 加载，也不能宣称 Serum / Sylenth1 渲染已通过。主界面保留未配置后端状态，供后续兼容性调查。完整请求、源文件 SHA-256 身份映射和失败 JSON 保存在被 Git 忽略的 `artifacts/render-validation`；私人预制内容没有进入仓库。工厂样本为 `BA Ampology [CFA]` 与 `BA Analog Pluck [SN]`，源哈希不同。没有音频产出，故未执行 RMS、音区差异或预览指纹比较，也没有人工演奏一致性验收。

2026-10-15 仅对既有环境做了一次 8 秒上限的空宿主复测：导入成功，`RenderEngine(44100, 128)` 在约 1.8 秒后以 `0xC0000005` 退出，未加载任何商业插件或预制。当前默认音色库中的 Serum 1 渲染器仍未配置，1513 条记录的尝试次数均为 0；这表示尚未尝试加载，不是所有 FXP 都验证失败。

同日只读核对默认音色库，发现 138 个 Vital WAV，三音区映射完整，约 181 MiB，全部为 `NeedsReview`，其中 86 项有削波风险提示。最近一轮生成 138 项、失败 0 项，其余 6417 个可处理项因 Serum 1 / Sylenth1 未配置渲染器而跳过。界面现在将 `Ready` 和已有音频的 `NeedsReview` 一并纳入已生成视图，但不会自动把待确认结果改为已验证。

「当前渲染状态」依据当前已保存配置和插件扫描元数据检查位数、格式、程序与模板；“可尝试”不是实际加载验证。32 位 Sylenth1 可以由兼容的独立 x86 宿主或桥接接入，不要求用户必须更换 64 位音源包；当前应用仍缺少经实际验证的该宿主，不能仅通过移除位数检查来声称支持。

下载缓存包括官方 0.8.3 / 0.9.0 Windows cp311 x64 wheel，分别核对了官方 PyPI SHA-256 `f603f96e110b1d581f108a3d5f62a09a56cb963bf7f84a0dbbf717e108a5b2a6` / `4899f18eb6c3e99daff1ac3ff01d55cb739a30d8b669c2ba467b186ec01b2437`。0.8.3 依赖安装后未发生运行期下载。

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

建议在单独的本地环境安装，避免修改系统 Python。示例在仓库目录执行：

```powershell
& 'D:\Professional\anaconda3\python.exe' -m venv artifacts\renderer-env
& '.\artifacts\renderer-env\Scripts\python.exe' -m pip download --only-binary=:all: --dest artifacts\renderer-wheels -r scripts\renderers\requirements.txt
& '.\artifacts\renderer-env\Scripts\python.exe' -m pip install --no-index --find-links artifacts\renderer-wheels -r scripts\renderers\requirements.txt
```

后两行分开下载和安装，使 wheel 缓存可用于断网安装。应使用对应 Python 版本 / 架构的 wheel。应用运行时只导入本地已安装依赖，不访问网络。DawDreamer 0.8.3 Windows Python 3.11 x64 wheel 约 30.9 MB；宿主 GPLv3 与所带 JUCE 等依赖许可证在发行宿主二进制时须保留并核对。应用不随发行包包含商业插件、授权文件或私人预制。

## 独立试听输出

本机的 WPF `MediaPlayer` 解码测试返回 `InvalidWmpVersionException` / `0x88980507`，缺少它要求的 Windows Media Player 组件。应用改用 Windows 自带 WinMM `waveOut` 播放，不要求安装或修改操作系统功能。

`WavePlayer` 读取已验证的 WAV，在内存中转换为 16-bit PCM 供默认音频设备试听；原始预览文件、float32 峰值与分析结果保持原样。支持播放 / 暂停 / 继续、按秒定位、音区切换、停止、音量和播放完成检测。[Microsoft 音量接口说明](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutsetvolume) 指定使用打开的设备句柄只影响该实例，本应用不使用全局设备编号调节音量。

播放器在释放音频指针前调用 [waveOutReset](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutreset)，等待缓冲区解除准备并关闭设备。位置按 [waveOutGetPosition](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutgetposition) 实际返回的字节数、采样数或毫秒换算。设备不存在、格式拒绝或驱动错误会显示具体原因。

用于此播放路径的 IEEE float32 测试 WAV 明确标记为 `TEST AUDIO`；它用于解码和控制验证，不属于插件渲染结果或音色兼容性证据。

## 音频校验与故障隔离

支持 RIFF WAV PCM 8/16/24/32 位和 IEEE float 32/64 位（包括对应 WAVE_FORMAT_EXTENSIBLE），单声道或立体声。检查格式头、块边界、帧对齐、音频时长与非有限数值。单文件上限 256 MB / 300 秒。

RMS 小于 0.00001 记为疑似静音；峰值达到 0.999 记为接近削波。这些线索进入待确认状态，不自动等同于预制失败。非有限数值、截断文件、路径不符、无效音符映射或异常退出不会提交。

每次任务在目标预览目录的同卷私有目录暂存请求、结果和音频。验证完成后移动 WAV 到最终缓存路径；失败 / 取消清理本次暂存文件。进程加入设置了退出清理的 Windows Job Object，主程序超时或取消会结束工作进程及其子进程，成功返回后也清理残留子进程。工作进程捕获插件失败并返回可展示原因。插件不能拖住主界面。

当前首先需解决本机 DawDreamer 构造失败，再完成实际 Serum 预览与人工演奏对照，并补齐 Vital 安装、兼容的 Sylenth1 架构、银行槽位适配和离线授权确认；源码及第三方工具的支持不能替代真实兼容性验收。
