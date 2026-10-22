# 渲染后端与实测边界

当前说明更新于 2026-10-23，对应 0.9.0 开发版；下方真实插件的历史版本、日期、样本与指标保持原样。桌面界面、调度器与工作进程使用 C# / .NET 8，插件渲染发生在独立进程。应用不会产生模拟合成器音频，也不会安装、下载或绕过插件授权。

0.8.1 新预览默认存为 AAC-LC 128 kbps / M4A，保留模板的 44100 / 48000 Hz 和立体声；可在「试听与存储」选择 WAV。插件仍先产生无损 PCM，完成目标预制、三音区和启动声音参照检查后才编码；声音参照始终保留 WAV。M4A 内保存原始帧数并绑定内容校验，解码仅去除末尾编码补齐，保持音区定位。旧 WAV 继续复用，只有显式「压缩已有 WAV」才更新已有文件；缓存键与数据库 schema 2 不变。Windows 原生编解码与三音区对齐已通过本地合成音频回归；macOS 实测由对应平台 CI 验证，该检查不扩大真实插件兼容范围。

0.8.0 私有环境本地验收：Windows 系统 PowerShell 5.1 从零准备 uv 0.13.4 / CPython 3.12.15，包含全部 Serum 2 依赖和私有 x64 C++ 运行库。整目录移动后离线准备成功，关键文件清单与已有个人记录哈希不变；Core 实际识别并修复失效旧受管路径，自选环境及其他后端保留。此环境通过完整 .NET 队列生成 Serum 1.334 的 `BA Analog Pluck [SN].fxp` 与 `BA Ampology [CFA].fxp`：44100 Hz，三音区各 0.5 秒，Generated=2 / Failed=0；随后禁用后端并移除可用 Worker 参数，仍 Reused=2，音频哈希不变。三份启动参照可比对，独立测试默认状态命中 3/3 并拒绝保存；原 DLL / FXP 哈希不变。报告位于本地隔离 `artifacts/uv-research/dawdreamer-queue-44100-*/report.json`，不附带商业音源或音色。Mac 私有环境由三平台 CI 验证空宿主及迁移，不据此扩大真实插件支持范围。

界面入口为「预览生成 → 生成方式（高级）」；「加载防错检查」记录音源刚启动、未加载预制时的声音作参照，避免把加载失败后的默认声音误存成目标音色。内部协议和以下历史记录仍使用“默认启动音色基准”这一技术名称。参照通常自动建立，不能替代人工确认目标音色与外部资源。

## 当前支持

| 组合 | 已实现 | 真实插件验收状态 |
| --- | --- | --- |
| Vital 独立程序 + 单文件 `.vital` | 三次原生命令行渲染、拼接、WAV 检查 | Windows 历史库已生成 138 项真实预览；本轮 Apple Silicon / Vital 1.6.4 使用明确标记的测试 patch 完成真实 CLI 渲染，均待人工确认加载对应关系与外部资源 |
| Vital `.vitalbank` | 完整内嵌采样及已覆盖波表源通过检查后，按条目私有加载；复核原包与条目哈希，不导入全局资源 | Windows Vital 1.6.4：实际包 78 条扫描通过；两条分别含音频波表和启用采样的音色经完整队列生成成功、随后复用，取消重做保留旧预览；源包与全局配置不变。仍需试听确认，Mac 包内生成尚待实机验证 |
| Serum 1 VST2 + `.fxp` | 可选 DawDreamer 真实宿主桥接 | Serum 1.334 x64 已完成一个实际 FXP 的生成和播放，用户确认三音区正常；尚未验证批量 |
| Serum 1 x64 VST2 + FPCh `.fxp` / Sushi（实验） | 手动配置自备补丁宿主；每音区新实例；启动声音参照；快速预览 | 本地两音色在 44.1 / 48 kHz 生成及完整队列通过，仍待人工试听；公开包不捆绑宿主，许可及样本边界见[研究记录](headless-host-research.md) |
| Serum 2 VST3 + `.SerumPreset` | 可选 DawDreamer 宿主、当前插件状态转换、加载后状态回读；共用队列和启动基准 | Apple Silicon / 2.0.23：两个工厂预制完成完整队列、三参考基准与 1.5 秒预览；待人工音色及外部资源确认，未验证其他版本或 Windows / Intel 实际执行 |
| Serum 2 旧 `.fxp` | 可识别 / 保留索引；当前 VST3 后端明确拒绝直接加载 | 先在 Serum 2 中导入并另存为 `.SerumPreset` |
| Sylenth1 VST2 + `.fxp` | 独立 x86 VST.NET 宿主；DawDreamer 仍只支持 x64 | 本机 2.2.1.2 x86 已生成、播放一个实际 FXP，用户确认三音区正常；尚未验证批量 |
| Sylenth1 银行槽位 | 外部工作进程协议；未内置槽位选择 | 未验证，不能把整个银行当单个预制 |
| Spire `.spf` / `.spf2` / `.sbf` / `.swzip` | 产品发现、配置入口与索引；统一显示「无法预览」并在详情解释原因 | 没有真实插件或渲染测试；不展开未知银行格式，不能生成原生预览 |

以下 Windows 路径发现与数量为 2026-10-15 的历史记录：已检查 `C:\Program Files\Common Files\VST3`、常见 `VstPlugins` / `VST2` 目录、Program Files 顶层产品目录，未发现当时目标产品的二进制。已发现 `C:\Users\Reguluz\Documents\Xfer\Serum Presets`，目录存在不能证明插件版本或离线授权可用。检查范围有限，用户可指定其他位置的插件。

随后依据用户提供的位置，检查 `D:\MusicStation\VSTi(64bit)\Sylenth1`：发现 4,260 个 `.fxp`、519 个 `.fxb`，插件 `Sylenth1.dll` 文件 / 产品版本为 2.2.1.2，PE machine 为 `0x014C`（x86 / 32 位）。目录名称中的 64bit 与二进制架构不符；未来宿主需单独的 x86 进程或真正的 x64 插件。未读取授权文件内容，未判断授权可用性。用户提供的 Serum 预制目录 `C:\Users\Reguluz\Documents\Xfer\Serum Presets\Presets` 中有 613 个 `.fxp`。这些数量表示文件数，不能等同于银行展开后的音色数。

在用户提供的 MusicStation 目录顶层，进一步发现 `D:\MusicStation\VSTi(64bit)\Serum_x64.dll`，产品版本 `1,3,3,4`（对应 Serum 1.334），PE machine `0x8664`（x64）。同目录的 `SerumFX_x64.dll` 为效果器，不用于音色演奏。

## 本机验证记录

### macOS Vital 与 Serum 2（v1.3.0）

2026-10-17 在 Apple Silicon 上确认已安装 Vital 1.6.4 与 Serum 2 2.0.23。音源扫描默认覆盖系统和用户的 VST / VST3 / Applications 目录；修复了没有显式目录时不能扫描，以及只从 Vital `.app` 二进制推断到 `Contents/MacOS` 的问题。发现仍只解析元数据，不启动插件。

Vital 使用真实 `/Applications/Vital.app/Contents/MacOS/Vital` CLI 演奏仓库验收目录中明确标记的 `TEST_ONLY_Mac_Vital.vital`。经完整 C# Worker 快速策略生成中 / 高 / 低各 0.5 秒的双声道 44100 Hz 预览，总长 1.5 秒；RMS 0.011462，峰值 0.028961，无静音、削波或非有限值。此 patch 是测试输入，声音由真实 Vital 生成；不能把它当作用户音色包或商业资源兼容性验收。CLI 仍不报告目标加载成功，结果保持 `NeedsReview`。证据位于 `artifacts/vital-compat/request.json` 和 `result.json`。

Serum 2 使用本机 VST3 2.0.23、Python 3.12.13、DawDreamer 0.8.3，通过完整 .NET 队列生成两个工厂预制 `WIND - Pan Flute.SerumPreset` 与 `LD - Das EDM.SerumPreset`。每个音高从新插件实例取得状态格式与实际版本，将同一原生预制快照转换为该状态后加载，并回读核对内容；没有以能发声代替目标状态确认。两个结果均为 44100 Hz、双声道 float32、MIDI 60 / 84 / 36 各 0.5 秒，总长 1.5 秒；队列 `Generated=2`、`Failed=0`，无静音、削波或非有限值，保持待确认。证据为 `artifacts/serum2-compat/full-queue/queue-result.json`。三参考启动基准结果见下节。

这些样本未覆盖其他 Serum 2 版本、全部工厂 / 用户预制、外部采样与波表、长序列连续切换或双平台波形一致性；本轮尚无用户对生成音色身份的独立试听确认。Windows 与 Intel Mac 只有发布构建验证，Spire 尚未实际加载。

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

快速模式另外用上述两个真实 FXP 验证：Serum / Sylenth1 各生成 66150 帧、44100 Hz 双声道 float32，三个音区均为 22050 帧 / 0.5 秒，总长 1.5 秒。各文件 529256 字节，相比原 12 秒预览体积减少约 87.5%；单轮采集即可满足响度，两份均已低音量播放，无静音、削波或非有限值，源 FXP / DLL 前后未变。报告位于 `artifacts/quick-preview-20261016`。这是实际单样本快速渲染验收，不宣称慢起音样本、Vital 快速模式或批量已完成真实插件验收；慢起音及静音上限由明确标记的测试子进程覆盖。

## 默认启动音色基准

当前界面将此功能称为「加载防错检查」。若支持的宿主未能加载目标预制却继续输出启动音色，三音区参照可在提交前拒绝该结果并保留旧预览。更换插件、模板（包括快速时长）或策略后须重建参照；已有完整缓存仍优先复用，不被追溯宣称通过新检查。

新增代码支持在新插件实例中不加载任何预制，演奏同一套三音区模板，保存当前安装状态的默认启动声音。它不是由用户指定的 `Init.fxp`，也不从已生成音色中挑选一个充当基准；插件自己保存的启动设置仍可能影响此声音，不能将它称为厂商统一的出厂音色。

| 后端组合 | 基准协议实现范围 | 本轮真实插件验收 |
| --- | --- | --- |
| Serum 1 / DawDreamer / x64 VST2 | fresh instance，跳过 `load_preset` | 本机 1.334：v2 三参考及独立启动输出自动比对通过，正常 Pluck 区分及低音量设备播放完成；尚无本轮用户身份确认 |
| Serum 2 / DawDreamer / 原生 VST3 | fresh instance，跳过原生预制状态转换与加载 | Apple Silicon / 2.0.23：三份独立启动参考校准成功，第四份 fresh instance holdout 命中 3/3 并拒绝提交；两个工厂预制经完整队列生成，尚无本轮用户身份确认 |
| Sylenth1 / DawDreamer / x64 VST2 | fresh instance，跳过 `load_preset` | 尚待单独执行；本机已确认的 Sylenth1 DLL 是 x86 |
| Sylenth1 / 内置 VST.NET / x86 VST2 | fresh instance，跳过 FXP chunk / 参数载入 | 本机 2.2.1.2：v2 三参考及独立启动输出自动比对通过，正常 Bass Saw 区分及低音量设备播放完成；尚无本轮用户身份确认 |
| Vital 原生 CLI、Spire、其他外部宿主 | 尚未适配此基准协议，明确提示未启用保护 | 不宣称已支持 |

对已支持后端，首次需要生成新音色且缺少有效基准时，队列先自动建立基准。设置中的「预览生成 → 加载防错检查」可查看状态、重新生成或独立试听，无需选文件和人工批准基准。基准包含实际 WAV、三音区映射、模板、插件 / 宿主指纹、实际版本及比对策略版本；对应身份改变、文件缺失、静音或校验失败时不能继续当作有效基准。无法建立有效检查时会明确失败。基准文件在预览目录的 `InitializationBaselines` 子目录，与普通音色映射分开。

0.7.2 修复内置 Sylenth1 x86 宿主的启动声音波动阻塞：仅在三份参照有效、已知 v2 宿主因声音随机变化无法稳定校准时，显式改用「逐音色状态核验」。它不表示声音比对已通过，也不降低原阈值；每个音区都须回读与目标完全一致的 chunk，或核验全部目标参数，并绑定源文件 SHA256。快速预览任一采集轮核验不全都不能提交。未知格式无法精确核验、旧宿主缺少证明或源身份不符时仍失败，已有预览保留。Vital CLI 不支持声音基准检查，但不会因此被禁止生成。

2026-10-20 的 Windows 小样本按当前默认力度 89、每音 0.5 秒验证：Serum 1.334 x64 一个 FXP、Sylenth1 2.2.1.2 x86 两个 FXP 均通过真实完整队列，失败 0 项，再次运行全部复用；原 FXP、插件和宿主文件哈希不变。原生快速预览另核对了实际回读证明的源 SHA256 与 C4 / C6 / C2 顺序。该轮实际三参考均可校准，替代模式的触发、重开和错误证明拒绝由确定性回归验证，不能将其写成整库或所有随机启动情况均已实测。私有证据位于 `artifacts/real-v072-*/summary.json`，不提交私人音色或生成音频。

采用声音比对时，新渲染在最终音频提交前按三个音区分别比较波形相关、包络、频谱和响度，所有严格门限同时满足才拒绝；不靠文件名、能否发声或单一频谱判断。拒绝时保存最近失败原因，已有完整预览不被覆盖。由于旧预览可能仍为 `Ready` / `NeedsReview`，可用「最近生成失败」查出本轮失败，不能只看旧预览状态。

当前策略 `startup-audio-comparison-v2` 生成三份独立的真实启动参考，元数据 `References` 分别保存 WAV 路径、SHA256 和完整结果，主 WAV / Result 继续保留供试听。只有参考两两都落在不可突破的硬边界以内，才根据观测漂移作有界校准；否则失败，不盲目放宽。少于四分之一秒的片段只允许原严格比较。候选必须匹配至少两份参考，并且对每份命中的参考都须三个音区同时通过完整比对；受校准的分支还检查起音等硬门限。不允许把不同参考的单音命中拼票，也不允许单凭频谱放行。

2026-10-16 的最终新序实测使用中 → 高 → 低（MIDI 60 / 84 / 36），每音 0.5 秒，普通输出均为 66150 帧、44100 Hz / 双声道 float32、总长 1.5 秒。两组基准各含三份独立参考：第一组建立校准 profile，第二组主音频作独立 holdout。Serum 1.334 x64 的启动 holdout 命中 3/3，`BA Analog Pluck [SN].fxp` 命中 0/3；Sylenth1 2.2.1.2 x86 的启动 holdout 命中 3/3，`Sylenth1 - Bass Saw.fxp` 命中 0/3。两份报告 `Success=true`、`InputsUnchanged=true`，FXP、DLL 及所检查宿主组件的 SHA256、大小、修改时间前后均不变，未进行批量。

报告分别在 `artifacts/init-v2-middle-first-serum-20261016/summary.json` 与 `artifacts/init-v2-middle-first-sylenth-20261016/summary.json`。端到端验收工具耗时分别为 40.356 秒和 3.832 秒，包含两组三参考生成及一次普通 FXP，不能视为单个预制的常规渲染耗时。早期 Sylenth1 四次独立启动的固定严格比较受调制漂移影响而不稳定，历史保留为采用三参考受限校准的原因，不把旧失败改记为通过。

两份本轮 `normal.wav` 已经由应用相同的 `WavePlayer` 以音量 0.12 顺序完整播放，均到达末尾且无超时。这证明实际播放路径已执行，不等于用户人工确认本轮音色身份；已有用户试听证据仍只适用于前述较早音频。此比较只表达“结果与当前默认启动声音严格近似”，不能独立证明预制加载失败或成功。非匹配结果仍须人工核对目标预制、外部资源与授权。已有有效预览仍按保存模板默认复用，不追溯宣称已通过新基准；显式重新生成才走新提交检查。两个已知样本的自动验证不覆盖其他插件版本、Sylenth1 x64、更多预制或批量，纯合成回归也不能替代真实验收。

2026-10-17 新增 Serum 2 2.0.23 / Apple Silicon / DawDreamer 验证：三份 fresh instance 参考完成有界校准，`artifacts/serum2-compat/full-queue/baseline-result.json` 记录 `CanCompare=true`。第四份独立启动实例作为 holdout，完整三音区匹配 3/3 参考，工作进程返回初始化匹配失败且 `OutputPublished=false`，证据为同目录 `holdout-result.json`。相同配置的完整队列成功生成 Pan Flute 和 Das EDM 两个工厂预制，未被误判为默认启动音色。此次没有重复 2026-10-16 Windows 脚本的两组三参考流程，不能据当前样本扩大到全部 Serum 2 版本。

## 快速预览策略

`PreviewTemplate.QuickPreview` 默认开启，`QuickPreviewSeconds` 为每音最终试听上限，默认 0.5 秒，可设 0.1–4 秒。缺少新字段的旧模板按 0.5 秒解释；默认值维持原缓存身份，非默认时长进入新缓存和基准模板身份。只有关闭快速模式才直接采用用户自定义按键与尾音。已有长预览仍按自身保存的模板和映射验证并默认复用，不因修改当前时长而自动删除或批量重渲染。

新模板 `NoteOrder=MiddleHighLow`，默认按中 → 高 → 低（MIDI 60 / 84 / 36）采集和拼接。界面以 C2 / C4 / C6 显示低 / 中 / 高音，`LowNote` / `MiddleNote` / `HighNote` 在协议中仍是数值 MIDI 音高。明确的 `LowMiddleHigh=0` 以及缺少 `NoteOrder` 的旧 JSON 保留低 → 中 → 高；旧预览按保存模板 / Notes 播放，不自动重排或失效。外部协议未提供此字段时仍按旧序，调用者须明确 `MiddleHighLow=1` 才取得新序。验收脚本只修改本次内存请求的顺序，不写回原请求或旧报告。

三种后端由 C# Worker 统一处理：从设定的目标时长采集，DawDreamer / 外部宿主保留 0.08 秒采集尾音；按 20 ms 窗口计算双声道能量（不混合相位），要求 RMS 至少 0.001、峰值至少 0.003。没有足够声音或起音过晚时有界延长，已足够的音区保留原片段。默认 0.5 秒仍采用 0.5、1、2、4 秒采集；其他目标以 `ceil(max(4, 2×目标时长))` 为最大按键时长，最高 8 秒，最多四轮。Vital 因整数秒限制将首轮向上取整，力度固定 89、尾音固定 30%。每轮仍受工作进程超时 / 停止约束。

找到有效声音后保留 10 ms 前导余量，片段不超过所设每音时长；尾部低于 RMS 0.0001 且峰值 0.0003 的明显静音可压缩，保留 10 ms 余量与至少 0.1 秒片段。只裁边界，不删除音内节奏停顿。裁头时加 5 ms 淡入，结尾加 5 ms 淡出，不做响度归一化。达到捕获上限仍无足够响度时使用低门限波形保真回退，无波形则保存短静音并标记待确认。三个片段直接拼接，总长最多为每音时长的三倍，默认最多 1.5 秒。

`NotePreview.ActualHoldSeconds` / `ActualTailSeconds` 保留真实 MIDI / 原生采集时长，`TrimmedLeadingSeconds` / `TrimmedTrailingSeconds` 记录被裁去的部分。必须满足 `DurationSeconds + TrimmedLeadingSeconds + TrimmedTrailingSeconds = ActualHoldSeconds + ActualTailSeconds`（帧级容差），`StartSeconds` 按裁后帧数累计，音区间无额外空白。旧数据缺少裁剪字段时默认为零。裁剪映射必须带 `quick-preview-v1` 标记，并通过当前目标时长和动态捕获上限校验；Vital 原生块舍入另有容差。

策略标志及版本进入缓存键；存储预算按最终短文件估算，临时原始捕获有独立有界预算。每轮受渲染器超时限制，外层按最多三 / 四轮预留总时限，停止始终可立即请求。勾选「全量重渲染（覆盖）」时跳过已生成复用与重试上限，成功才提交新结果，停止或失败保留当前旧预览，不提前清库。换路径后，只回收当前预览目录中符合旧 64 位十六进制缓存名、无其他索引引用、不是源文件且不经链接的旧文件；先提交新映射，再清理旧文件。可确定回收的空间计入最终预算，提交前暂存可短暂并存两份。

DawDreamer 每次 `render()` 都从头处理，不能连续调用当成无损续渲染；低响度重试可能比一次较长渲染更慢。Vital 也仍需整数秒原生采集。快速模式减少正常起音的 DSP 与最终试听 / 存储长度，不承诺每种插件初始化或每个慢起音都加速。

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

JSON 属性见 `SoundPreviewer.Core.Models.RenderRequest` / `RenderResult`，枚举按默认 System.Text.Json 数字序列化：Vital=0、Serum1=1、Serum2=2、Sylenth1=3、Unknown=4、Spire=5；Unconfigured=0、VitalCli=1、ExternalWorker=2、DawDreamer=3。旧枚举值保持不变。`PreviewNoteOrder` 为 LowMiddleHigh=0、MiddleHighLow=1，缺字段兼容旧低中高。请求含源预制身份、插件配置、试听模板、缓存键和本次暂存 WAV 输出路径。渲染器须按模板 `Pitches` 的实际顺序返回三个 Notes，只能将成功结果提交到请求的输出路径。

外部后端使用相同 `--request` / `--result` 参数，无命令行 shell。用户指定实际外部渲染器可执行文件及插件路径。外部程序须：

1. 实例化用户指定的真实音源，并明确验证专有预制或银行槽位加载。
2. 以请求的三个 MIDI 音高、力度、BPM、按键和尾音时长生成音频。
3. 返回 `Success`、`OutputPath`、`AdapterVersion`、`NeedsReview`、`Message` 和三个 `Notes` 映射。每项含 `MidiNote`、`StartSeconds`、`DurationSeconds`、`ActualHoldSeconds`、`ActualTailSeconds`、`ActualVelocity`。可附 normalized 力度及音区指标。
4. 输出完整 WAV 后写入结果 JSON；失败返回 `Success=false` 与原因，并使用非零退出码。无法确认目标加载时必须设置 `NeedsReview=true`。

主工作进程和桌面端都独立检查 WAV，不信任外部提供的指标。每个音区须按顺序且不重叠，时间必须覆盖合理范围且位于文件内。外部执行文件不能指向 SoundPreviewer.Worker 本身。外部宿主应不留后台子进程，并在授权弹窗等无法继续状态返回错误。

[DawDreamer 官方 API](https://github.com/DBraun/DawDreamer/blob/main/docs/user_guide/plugin_processor.rst) 提供 VST2 `load_preset(.fxp)`、VST3 `load_vst3_preset(.vstpreset)` 与 MIDI 演奏。`.SerumPreset` 通过本项目额外的原生状态转换与 `load_state` / `save_state` 回读适配，不直接交给 `.vstpreset` 接口。API 的存在不证明特定版本或 Sylenth1 `.fxb` 已兼容，本项目仍以明确的真实样本记录为准。

## 可选 DawDreamer 宿主

`scripts/renderers/dawdreamer_worker.py` 与 `serum2_state.py` 随 C# Worker 发布到 `Renderers` 子目录。主界面的 DawDreamer 后端配置使用 `Plugin.ExecutablePath` 指定 Python 可执行文件，`Plugin.PluginPath` 指定对应乐器：Serum 1 / Sylenth1 使用 Windows VST2 DLL 或 macOS `.vst`，Serum 2 使用 `.vst3`。桌面应用与调度为共用的 C# / .NET 8 / Avalonia；Python 仅作为隔离的可选本地插件宿主。

固定基础依赖为 **DawDreamer 0.8.3、NumPy 1.26.4、CPython 3.11 或 3.12**，Windows 为 x64，macOS 须与实际插件使用相同的 arm64 或 x64 架构。Serum 2 另需 `requirements-serum2.txt` 中的 **serum2-preset-loader 0.1.3、cbor2 5.6.5、zstandard 0.23.0**，该文件同时包含基础依赖。不会随运行自动升级。依据 [v0.8.3 PluginProcessor 源码](https://github.com/DBraun/DawDreamer/blob/v0.8.3/Source/PluginProcessor.cpp)，`load_preset` 通过 JUCE 的 FXP 加载接口返回成功标志，桥接在返回 false 时拒绝生成。依据 [v0.8.3 RenderEngine 源码](https://github.com/DBraun/DawDreamer/blob/v0.8.3/Source/RenderEngine.cpp)，秒制 MIDI 与渲染时长按采样帧保存，脚本记录由真实输出帧数决定的时间。

Serum 2 的结构转换使用固定版本、MIT 许可的 [serum2-preset-loader](https://github.com/wiillownet/serum2-preset-loader)。本项目根据实际插件保存的状态格式重新封装，并执行独立的内容回读核对，不直接采用转换器内置的固定插件版本标记。

Serum 1 / Sylenth1 路径接收独立 `.fxp`，检查文件头、扫描哈希与匹配的二进制架构。Serum 2 路径接收原生 `.SerumPreset`，检查 XferJson 产品 / 文件类型 / 版本和有界解压后的内容，使用实际 fresh state 的状态 schema，拒绝超出支持范围或加载后回读不符的结果。每个音高创建新插件实例，加载同一份不变的预制快照，以指定 BPM 和 MIDI 力度演奏，保留请求的释放时间与插件自身效果。输出为真实插件的 IEEE float32 WAV 数字音频，保留原始输出，包括超出 ±1 的峰值，不进行削峰 / 响度归一化，也不使用系统回录。无需打开插件界面；授权弹窗、崩溃与挂起由工作进程的时间限制处理。当前新宿主组合一律标记待确认，须人工抽查预制与资源；静音 / 削波另外记录。

不支持 x86 DLL、SerumFX、VST3 直接加载旧 `.fxp`、`.fxb`、银行槽位或 Spire 原生预制。Serum 2 旧 FXP 须由用户先在实际 Serum 2 中导入并另存，不能自动改扩展名。Sylenth1 本机 x86 插件会在加载宿主前返回可理解的架构错误。

0.8.0 默认使用私有 uv 0.13.4 下载 CPython 3.12.15，不要求系统 Python。GUI「一键准备生成环境」默认包含 Serum 2 组件；扫描到兼容插件时自动填入未配置项，已有外部环境和后端保留。等效命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Prepare-DawDreamer.ps1 -IncludeSerum2
# -ApplicationPath 指定发布目录；-DataDirectory 仅供隔离诊断。
```

```bash
bash scripts/Prepare-DawDreamer.sh --app-path ./artifacts/publish-osx-arm64 --include-serum2
```

`--app-path` 接受源码、发布目录、`.app` 或其 `Contents/MacOS`。Windows / 普通发布目录使用程序旁 Preview，Mac bundle 使用 app 旁 Preview。Python 位于 `Preview/renderers/dawdreamer-portable-平台`；uv 缓存和临时文件也在 renderers，诊断在 `Preview/diagnostics`。不写 PATH、Python 注册信息、用户 uv 配置或全局包目录。使用 uv 的固定 catalog、官方归档校验、`--no-config`、显式缓存与安装目录，并清理继承的 UV / PYTHON / PIP 定制项；Windows 从应用资源复制私有 C++ 运行库，不覆盖系统 DLL。相关原理见 [uv 管理的 Python](https://docs.astral.sh/uv/concepts/python-versions/)。

相对 `runtime.json` 在空宿主自检通过后写入，含 Python / 依赖版本与关键二进制、转换器文件 SHA-256。使用独立解释器而非 venv，整体目录移动后可离线运行；`-Offline` / `--offline` 可验证无需下载复用。Core 只自动修复明确属于本程序受管环境且已失效的旧路径，保持自选配置。新运行时指纹覆盖实际关键文件；旧环境指纹及旧预览优先复用规则保持兼容，不触发全库重渲染。调用 Python 使用 `-I -B`，只显式加入随包桥接目录，不使用当前目录或 PYTHONPATH 查找模块。

高级兼容入口仍接受 `-PythonPath` / `--python` 指定标准 CPython 3.11 / 3.12，沿用旧独立 venv；这种旧环境仍依赖其基础 Python 位置，不能承诺整体移动。`-UvPath` / `--uv-path` 是开发验证入口；普通发布包已自带匹配 uv，无需用户安装。

设置脚本只做空宿主自检，不加载商业插件或输出 WAV。应用日常生成只使用本地依赖。Windows 桥接检查实际加载的 C++ 运行库，过旧直接提示；自检成功不扩大商业插件兼容范围。下载的 DawDreamer / Python 保留各自官方许可证与元数据，不作为公共安装包或 CI 附件上传；随包 uv 与 Microsoft 运行库声明见 `third-party/uv`、`third-party/vc-runtime`。

## 单音色验收工具

`scripts/Validate-SinglePreset.ps1 -RequestPath <request.json> [-Play] [-Volume 0.15]` 经完整工作进程链只处理一个独立预制；拒绝银行槽位、覆盖已有验收输出或写入工作区 `artifacts` 以外。逐段检查三音区、记录 WAV 指纹并验证源 FXP 与 DLL 未改变。`-Play` 使用应用相同的播放器，不是系统录音。

`scripts/Open-ValidatedSamples.ps1 -SummaryPath <summary.json>` 仅将通过校验的真实结果导入新建的隔离试听库并打开桌面应用；不覆盖原库，不添加扫描根目录，不自动生成批量预览。可加 `-NoLaunch` 只建立样本库，使用 `-DataDirectory` 指定新的 `artifacts` 子目录。

`scripts/Validate-InitializationBaseline.ps1 -RequestPath <已核对的普通FXP请求.json> -ArtifactDirectory <新的artifacts子目录>` 单独验证新实例无预制加载的基准合同。可用 `-WorkerPath` / `-CorePath` 指定待验收版本，`-RendererPath` 只覆盖本次请求的宿主路径。输入必须是匹配音源身份的独立普通 FXP 请求，不接受银行或把基准请求再次作为普通预制。

工具读取第一组元数据的三份原始参考，逐项核验 WAV / 指纹 / 音区映射后建立校准 profile，再用第二组独立生成的主 WAV 作 holdout，要求至少命中两份完整三音区参考；普通 FXP 也只与第一组同一 profile 比较。两组生成各三份参考，再加一次普通 FXP，共按最多七次三音区请求预留取消时限，每次快速请求仍有自身有界采集轮数。保存 `summary.json`、逐参考音频报告、普通请求与结果、两组基准元数据、WAV、分段指标、时序及源预制 / DLL / 宿主 / 桥接脚本等输入的前后指纹。目标目录必须尚不存在，且位于工作区 `artifacts` 下、不经过目录链接，不覆盖旧样本或写入原音色库数据库。

此入口会实际执行用户配置的商业插件宿主，不是 `--probe` 空宿主测试，也不运行目录扫描或批量任务。指纹不变、基准可重复或普通音色非匹配，均不能独立证明预制身份及所有资源正确；仍须独立试听。当前新增基准的受限校准与真实验收结论保持单独跟踪，不能据工具存在或纯测试通过就标为真实验收成功。

## 可选 32 位 Sylenth1 宿主

`src/SoundPreviewer.Vst2Host` 使用独立 x86 .NET 8 进程及固定的 [VST.NET2-Host 2.1.0](https://www.nuget.org/packages/VST.NET2-Host/2.1.0)，不需要 Python，也不把 32 位 DLL 加载进桌面主进程。Windows 发行包已自带该宿主及其 x86 .NET 运行库；仅使用独立开发构建时需另装 x86 .NET 8 Desktop Runtime。两种方式仍需 Microsoft VC++ x86 运行库。只处理 Sylenth1 的独立 `.fxp`，拒绝 `.fxb`、包内条目、其他音源和 x64 DLL。校验源 SHA256、PE 架构、FXP / 实际插件 ID；chunk 加载检查返回值与 program 状态变化，无法取得加载证据时拒绝生成。每个音高使用新插件实例，输出保留 float32 原始音量；结果始终先待确认。

此可选项目不加入主解决方案，主界面使用 Avalonia NuGet 包，x86 宿主依赖单独恢复。单独构建需要从官方 NuGet 恢复依赖：

```powershell
pwsh -File src/SoundPreviewer.Vst2Host/build.ps1
dotnet publish src/SoundPreviewer.App/SoundPreviewer.App.csproj -c Release -o artifacts/publish
```

主应用构建 / 发布时，将已构建的 `artifacts/vst2host-x86` 完整复制到 `Vst2Host-x86` 子目录。不能只复制宿主 EXE，须保留 Jacobi / Ijwhost DLL、配置、依赖与许可证。包含该宿主的版本在插件扫描后可自动关联 Sylenth1 x86 VST2；已有自定义外部渲染器不会被覆盖。无可选宿主时仍不自动启用 x86。工作进程只对这个内置宿主子进程清除可能误指向 x64 的通用 `DOTNET_ROOT`，不改系统或父进程环境。

最终无插件自检包含 `UnchangedChunkRejected`：未能改变插件状态的测试 chunk 必须被拒绝。此自检不加载商业插件，不能替代实际 FXP 的加载和音色身份核对。

VST.NET 为 LGPL-2.1，独立动态 DLL 保持可替换，许可证与源码 / 重建信息随宿主保留，见宿主目录 `THIRD-PARTY-NOTICES.md`。不分发 Steinberg SDK、商业插件、授权文件或私人音色。

## 独立试听输出

本机的 WPF `MediaPlayer` 解码测试返回 `InvalidWmpVersionException` / `0x88980507`，缺少它要求的 Windows Media Player 组件。应用改用 Windows 自带 WinMM `waveOut` 播放，不要求安装或修改操作系统功能。

`WavePlayer` 读取已验证的 WAV，在内存中转换为 16-bit PCM 供默认音频设备试听；原始预览文件、float32 峰值与分析结果保持原样。支持播放 / 暂停 / 继续、按秒定位、音区切换、停止、音量和播放完成检测。[Microsoft 音量接口说明](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutsetvolume) 指定使用打开的设备句柄只影响该实例，本应用不使用全局设备编号调节音量。

播放器在释放音频指针前调用 [waveOutReset](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutreset)，等待缓冲区解除准备并关闭设备。位置按 [waveOutGetPosition](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutgetposition) 实际返回的字节数、采样数或毫秒换算。设备不存在、格式拒绝或驱动错误会显示具体原因。

用于此播放路径的 IEEE float32 测试 WAV 明确标记为 `TEST AUDIO`；它用于解码和控制验证，不属于插件渲染结果或音色兼容性证据。

## 音频校验与故障隔离

0.7.4 Windows Sylenth1 2.2.1.2 x86 加载已知 1028 字节 `1lys` FXP 时，会把部分源状态头标识 2202/2211 改写为 2200，而名称、槽位和全部参数保持相同。宿主 v3 仅对白名单格式及这两个实证改写放行，任何其他字节差异仍拒绝；proof scheme 升为 v2，旧 v2 宿主/v1 proof 保留识别。15 个真实音色中：6 个原误判生成成功、3 个原成功保持成功、3 个状态未变和3个约3040字节不兼容格式拒绝且不提交 WAV。完整 quick Worker 路径另验证两个恢复输入和一个不兼容输入；16 个源文件/插件哈希前后不变。这是状态加载及音频结构验证，仍需人工试听确认音色和资源，不扩大其他插件版本/平台的兼容声明。明确加载不兼容的错误携带格式差异及匹配版本/重新导出建议，本批只尝试一次。

支持 RIFF WAV PCM 8/16/24/32 位和 IEEE float 32/64 位（包括对应 WAVE_FORMAT_EXTENSIBLE），单声道或立体声。检查格式头、块边界、帧对齐、音频时长与非有限数值。单文件上限 256 MB / 300 秒。

RMS 小于 0.00001 记为疑似静音；峰值达到 0.999 记为接近削波。这些线索进入待确认状态，不自动等同于预制失败。非有限数值、截断文件、路径不符、无效音符映射或异常退出不会提交。

每次任务在目标预览目录的同卷私有目录暂存请求、结果和原始 WAV。验证完成后按设置编码为 AAC 或保留 WAV，再移动到最终缓存路径；失败 / 取消清理本次暂存文件并保留原有效预览。进程加入设置了退出清理的 Windows Job Object，主程序超时或取消会结束工作进程及其子进程，成功返回后也清理残留子进程。工作进程捕获插件失败并返回可展示原因。插件不能拖住主界面。

早期 Windows 的 Serum 与 Sylenth1 x86 单音色均已完成真实生成及用户试听；当时按要求先验收单音色，未启动批量生成。0.5.0 随后新增各两个真实 FXP 的串行 / 双任务小样本测速，见[并行实测](parallel-rendering.md#windows-小样本实测2026-10-19)；新增样本尚未独立人工试听确认身份。连续切换、外部资源、银行槽位和离线授权仍需逐项核对，源码及第三方工具的支持不能替代真实兼容性验收。


## macOS 共用版本（2026-10-17）

桌面和 Worker 统一为 .NET 8，渲染协议、三音区、快速裁剪与结果校验共用。macOS 的可选 DawDreamer 桥接支持与 Python 架构匹配的 VST2 `.vst` bundle，原先的 `.dll` / MSVC 检查仅适用于 Windows；macOS bundle 读取 Info.plist 与 Mach-O 校验架构。插件基准指纹也支持 bundle 实际二进制与元数据。Vital 使用本平台独立程序，原生采集命令采用两平台都接受的相对输入路径。

v1.3.0 已在本机 Mac 完成 Vital 1.6.4 的测试 patch 原生渲染，以及 Serum 2 2.0.23 的两个工厂预制完整队列和三参考启动基准。此前 Windows Serum 1 / Sylenth1 的历史结果不自动延伸到 Mac；Windows / Intel Mac 的新版构建也不等于对应硬件实测。发布、输出路径和完整验收边界见 [双平台说明](cross-platform.md)。
