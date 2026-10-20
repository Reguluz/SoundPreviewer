# 并行渲染可行性调研

更新：2026-10-21。当前为 **0.7.4**，沿用同音源优先调度、自动并行与内存保护；下文保留各版本实测与原始调研。宿主固定依赖为 DawDreamer **0.8.3**、标准 CPython **3.11 / 3.12**。

0.7.4 默认允许同音源多实例，保留已明确关闭的选择，音源重新关联或升级不再重置。每项预测只计自己的 Worker 子树峰值（25% + 64 MiB 余量），主程序已经包含在实际 RSS 中；主程序历史峰值高于当前的差额仅额外预留一次，再加既有 128 MiB 全局余量。修正此前共享主进程增长被多个任务重复预留的问题；允许数量只按当前待处理音源估计，已完成重音源不再压低后续轻音源显示。界面分别显示实际占用与预留；仍保留 1.75 GiB 暂停启动、2 GiB 停止及完整采样要求。新增回归启动真实隔离子进程，并注入总 RSS 为 768 MiB 的资源采样模型，验证可填满 8 槽；这不是商业插件八并行的内存实测或测速，不能作为固定提速承诺。

0.7.3 的 `QueueProgress.MaxConcurrency` 为配置/机型目标，`AllowedConcurrency` 为内存估算上限，`EffectiveConcurrency` 为结合音源多实例许可、首项观测与剩余队列后的当前有效上限。界面运行分母采用有效上限，并显示具体限制原因。未许可的单一音源即使目标为 8、内存允许 3，仍显示运行 1/1；权限不自动开启。实时计数来自本次队列提交结果，区分新生成、复用、失败、未配置和达到重试限制；强制重做失败而保留旧预览仍计失败。此改动不提高内存预算或更改音频缓存键。

## 0.7.0 同音源优先

去重后的生成任务按音源首次出现顺序稳定分组，组内保持原来的顺序。例如 A / B / A / B 串行生成变为 A / A / B / B。同音源允许并行时，首项校准后优先持续填满该组；后续未测音源不会抢先校准、打断当前组。不允许同源多实例且存在空槽时，在空闲边界单独校准后续音源，再让异源任务补位。可选后组校准的预留无法容纳时，继续当前仍能安全启动的任务；只有确实无安全任务时才停止。内存预留、2 GiB 目标、同键合并、取消恢复及提交边界保持原规则。

此改动减少混合音源切换，可能改善系统文件缓存局部性，但**不减少当前的插件实例化次数**，尚无独立提速测量。Worker 仍按音色隔离；DawDreamer 的三个音区各建新引擎与插件，Sylenth1 各建新插件上下文，Vital 各启新 CLI。声音参照仍按身份复用，新排序不改变缓存键或数据库 schema。

真正省去宿主启动的后续候选是同源会话常驻、Python 导入一次，同时继续按音区创建全新插件实例。应先增加阶段计时，再测试串行和交错顺序、坏预制后恢复、长批次内存及停止清理。跨音色复用同一插件还需单独证明尾音、随机相位、采样资源和默认状态可重置；当前未启用该方案，也未改变适配器或声音参照身份。

## 0.6.0 自动并行与内存目标

`MaxParallelRenders` 支持 0（自动）及 1 / 2 / 4 / 8 / 16。新设置默认自动；旧 JSON 缺字段仍为 1，已有 1 / 2 及音源多实例许可保持原值。自动按物理核心数向下选择档位，四核选 4、八核选 8、最高 16；物理核探测失败时，按可用逻辑线程数的一半保守选择。档位是同时处理音色的上限，不设置 CPU 亲和性，也不保证每个插件只用一个线程。

生成期间固定以 **2 GiB（2048 MiB）** 为内存目标，采样统计调用进程及其 Worker、Python、原生宿主等全部可见后代的 RSS。Windows 使用 Toolhelp 父子进程表与 WorkingSet，Mac 使用系统 ps；共享页可能重复计入，因此结果较保守。它不同于任务管理器中仅主程序的专用内存。

各音源先单独完成一个任务，观察实际子树峰值后再增加并行；没有捕获子进程时继续单条，未知任务不凭 0 占用放量。每项按自己的 Worker PID 子树扣抵预留，避免某个大任务抵扣其他尚未加载任务的额度。1.75 GiB 附近暂停启动新任务，另保留全局余量；完整观测超过 2 GiB、连续无法完整监测或无安全启动余量时取消本批并等待进程结束，保留已完成预览，恢复未完成项。手动建立声音参照也经过相同保护。

监测间隔目标为 100ms。第三方插件的瞬时分配可能越过采样间隔或预测，因此这是带预留与停止保护的运行目标，**不是操作系统级硬配额**。手动 8 / 16 也不能绕过内存目标，实际活动数可能低于所选档位。界面分别显示档位、允许数量、当前内存与资源停止原因。

### 0.6.0 Windows 五样本实测（2026-10-20）

0.6.0 在 Windows x64、8 物理核 / 16 可见逻辑线程的机器上，分别使用 Serum 1.334 x64 / DawDreamer 0.8.3、Sylenth1 2.2.1.2 x86 / 0.6.0 随包宿主。每种音源使用五个内容 SHA-256 不同的独立 FXP，模板固定为 MIDI 36 / 60 / 84、力度 100、每音区 0.5 秒、44.1 kHz 双声道，各档使用独立新库。

按 1 → 2 → 4 顺序，每档测一次 cold、**仅一次 warm-force**，再单独检查缓存复用。cold 包含新建声音参照，warm 复用参照但强制实际生成；每次队列的首项单槽资源校准仍计入耗时。下表耗时均为五个音色整批秒数，RSS 为该档冷、暖及缓存阶段的总进程树观测最大值：

| 音源 | 请求档位 / 实际活动峰值 | cold（秒） | warm（秒） | 总树 RSS 峰值（MiB） |
| --- | ---: | ---: | ---: | ---: |
| Serum 1 | 1 / 1 | 38.117 | 31.723 | 320.3 |
| Serum 1 | 2 / 2 | 26.385 | 19.776 | 509.8 |
| Serum 1 | 4 / 4 | 19.593 | 14.140 | 886.0 |
| Sylenth1 | 1 / 1 | 4.998 | 3.161 | 161.5 |
| Sylenth1 | 2 / 2 | 3.929 | 2.229 | 255.5 |
| Sylenth1 | 4 / 4 | 3.479 | 1.543 | 465.3 |

本轮 warm 相对 1 档，Serum 的 2 / 4 档约为 **1.60 / 2.24 倍**，Sylenth1 约为 **1.42 / 2.05 倍**。各 cold / warm 均新生成 5 项、复用 0 项、失败 0 项；缓存检查为 0 生成、5 复用、活动峰值 0，约 0.030–0.049 秒，另行统计。

全部档位的 2 GiB 预算保持原值，未触发资源停止；已记录的源音色、插件、宿主及程序文件前后指纹均未变。独立采样的 RSS 读取完整，统计范围为测速程序及其后代，不能直接代表 Avalonia 主界面的基线占用。采样目标 100ms，实际最大间隙约 220ms，短峰仍可能漏采；CPU 报告是已观测本树累计 CPU 秒的下界，解读时须与整机利用率区分。

音区映射与音频结构检查通过，无整段静音或非有限值。Serum 各输入在三个档位 warm 的音频 SHA-256 各自一致；Sylenth1 三个输入各自一致，Bass Saw 与 16 Bit Lolitas 的字节随生成变化，未据此认定加载错误。Ampology 与 AUDENTITY BS 1 的削波提示在各档均保留。以上不能替代人工确认音色身份及外部资源，也不能外推为其他插件、Mac 或长批次的稳定倍率；自动、8 / 16 档本轮未加载真实插件。

原始报告为 `artifacts/parallel-benchmark-92ec2d432c49412abb399876817ef453/report.json`（Serum）及 `artifacts/parallel-benchmark-9b6434e453094974838ab077f8d2590c/report.json`（Sylenth1），包含实际配置、进程树采样和阶段音频指纹。

### 当前测速入口

`scripts/Measure-ParallelRendering.ps1` 仅接受 Windows 独立 VST2 DLL，在新的 artifacts 目录准备测试库；显式加 `-Execute` 才加载真实插件。当前接受 4–16 个不同内容的独立预设；要在首项校准后观测 4 条真实重叠，应至少给 5 个输入，例如：

```powershell
./scripts/Measure-ParallelRendering.ps1 -RequestPath ./artifacts/my-request.json `
  -PresetPath @('C:\Presets\A.fxp', 'C:\Presets\B.fxp', 'C:\Presets\C.fxp', 'C:\Presets\D.fxp', 'C:\Presets\E.fxp') `
  -WarmRuns 1 -SlotOrder @(1,2,4) -Execute
```

先构建 Release 测试程序与 Worker。每档单独新库，依次测包含声音参照建立的 cold、复用参照但真实重做的 warm-force、单独的 cache-reuse。报告保留实际配置、请求 / 解析档位、活动峰值、Core 与独立观察的进程树 RSS 峰值、采样间隙和 CPU 时间下界。短命进程可能漏采，CPU 不是精确整机利用率；根进程是测速程序，不能把其基线占用当成 Avalonia 界面的占用。

资源停止或独立采样超过预算会保留报告、停止该档后续测速，不提高预算或计为成功加速。报告与各阶段音频保存在 `artifacts/parallel-benchmark-*`，其中含本机文件路径，分享前请核对。实际插件结果仍需逐样本试听，原始音色、插件与宿主前后指纹应不变。

## 0.5.0 试验实现（历史）

全局 `MaxParallelRenders` 仅允许 1 / 2，缺字段默认 1；每音源 `AllowParallelRendering` 缺字段默认 false。界面「预览生成」可开启全局两个任务，再按音源单独允许多实例。重新关联不同插件或版本会撤销该音源许可。旧缓存键、声音参照身份和数据库 schema 保持不变。

父进程以队列快照、有界调度、缓存键任务合并、单次基准准备、空间预留和集中数据库提交协调工作。单个 Worker 内仍按原顺序采集三个音区，插件不跨任务复用。日志记录队列总时长及同时运行数量；完成事件额外提供单项总时长。

回归覆盖真实测试子进程的重叠与上限、同音源许可、同键复用、预算、共享缓存、个人元数据和停止恢复。通过这些检查不等于所有商业插件都能并行；真实性能结果另按样本记录，不承诺固定加速倍数。

0.5.0 使用两个不同内容的预设，在隔离测试库中比较 1 / 2 槽。下列数值仅对应当时实现，不代表 0.6.0 校准及资源预留策略的性能。

### Windows 小样本实测（2026-10-19）

Serum 1.334 x64 / DawDreamer 0.8.3，Windows 11 x64；机器为 AMD Ryzen 7 9850X3D（8 核 16 线程），系统可见内存 61.6 GiB。使用内容不同的 `BA Analog Pluck [SN].fxp` 与 `BA Ampology [CFA].fxp`，模板为 MIDI 36 / 60 / 84、力度 100、每音区 0.5 秒、44.1 kHz 双声道。以下为两音色整个队列的秒数，每轮重新加载并实际生成：

| 模式 | 新库及声音参照 | 复用参照后重做 1 | 复用参照后重做 2 | 预览 Worker 峰值 |
| --- | ---: | ---: | ---: | ---: |
| 串行 | 57.532 | 50.625 | 50.759 | 1 |
| 两任务并行 | 36.111 | 31.057 | 30.915 | 2 |

已有参照时，两次平均从 **50.69 秒降到 30.99 秒，约 1.64 倍，耗时减少约 39%**。两种模式每轮均生成 2 项、复用 0 项、失败 0 项。另测缓存复用为 0.006–0.007 秒，均无生成进程；这项不计入渲染提速。

每个输入在四轮重做中的 WAV SHA-256 一致，两个输入之间不同，音区映射及输入 / 插件 / 宿主指纹检查通过。Ampology 高音区存在接近削波提示，串行与并行一致保留；新增样本尚未独立人工试听确认音色身份及资源完整性。

交换成 2 → 1 顺序后，串行两次为 53.071 / 52.997 秒，并行两次为 32.100 / 32.015 秒，均值比为 **1.65 倍**；与正向的 1.64 倍相近。两个样本跨方向共八轮重做的音频 SHA-256 各自一致，输入、插件与宿主指纹也均不变。

本次未记录 CPU / 内存峰值。结果只支持这台机器上的两个样本，不能推定所有音源、音色或 Mac 的加速幅度；默认值继续为串行。正向完整报告为 `artifacts/parallel-benchmark-cb019589b34248c5820cb95078a1263c/report.json`，反向为 `artifacts/parallel-benchmark-1b8c06e4b6b341109453fe6076580428/report.json`，阶段音频另存于各测速目录的 `slots-*/evidence` 子目录。

Sylenth1 2.2.1.2 x86 / 随包 0.5.0 独立宿主，同模板使用 `Sylenth1 - Bass Saw.fxp` 与 `16 Bit Lolitas Bass .fxp`，也按 1 → 2 顺序测量：

| 模式 | 新库及声音参照 | 复用参照后重做 1 | 复用参照后重做 2 | 预览 Worker 峰值 |
| --- | ---: | ---: | ---: | ---: |
| 串行 | 3.091 | 1.244 | 1.221 | 1 |
| 两任务并行 | 2.317 | 0.737 | 0.672 | 2 |

两次平均从 **1.23 秒降到 0.70 秒，约 1.75 倍**，每轮均生成 2 项、无复用或失败，源文件与宿主指纹不变。所有预览的音区映射通过，无整段静音、削波或非有限值；同一输入在串行重复生成时音频哈希也会变化，因此不能要求本例并行结果逐字节一致，也不能仅凭通过自动检查确认音色身份。缓存复用另计为 0.006–0.013 秒，没有启动生成。报告为 `artifacts/parallel-benchmark-52856ecb7b424a2495277ce85f5fb18a/report.json`。

首次 Sylenth1 测速误用了旧开发目录的宿主，它不支持声音参照请求，已安全拒绝且未进入普通预览生成；换成当前随包宿主后才获得上表结果。实际测试时应填写当前完整宿主路径。

## 0.4.0 调研结论（保留）

**可以在现有架构上尝试有界多进程并行，以提高批量生成速度。建议先做 2 个预制同时运行的实验，并保留 1 个任务的兼容模式。** 单个音色的插件加载、演奏和慢起音重试仍有自身耗时；实际加速倍数要通过真实插件测量。

现在已经有独立 Worker 和插件宿主进程，主要改动在父进程的队列、结果提交与恢复逻辑。推荐每个工作项继续使用独立进程和新插件实例，以异步任务管理有限的并发槽。第一阶段保留每项内部的三音区顺序，避免同时引入进程常驻、插件复用和音区级并发。

**当前 0.4.0 仍逐个生成，用户界面尚无并行设置。** 这份文档描述后续可行方案，不表示已验证商业插件可以任意多开。

## 当前生成过程与成本

父进程从候选中选择一个音色，检查已有预览、配置和声音参照，启动独立 Worker，等待生成与校验完成，再保存数据库并处理下一项。异步等待使界面保持响应，队列仍一次只等待一个 Worker。位置见 [RenderQueue.cs](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Services/RenderQueue.cs#L101) 和 [WorkerProcessClient.cs](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Audio/WorkerProcessClient.cs#L19)。

| 层次 | 当前行为 | 对提速的含义 |
| --- | --- | --- |
| 音色队列 | `foreach` 中逐项 `await RenderAsync` | 不同预制尚未重叠运行，适合先增加少量执行槽 |
| DawDreamer 固定采集 | 每轮启动一个 Python 进程；在其中依次创建三组新引擎和插件实例 | 进程启动、导入和重复插件初始化都可能产生开销 |
| DawDreamer 每音区 | 加载并核对预制，显式等待 0.15 秒，再做预热和目标采集 | 每轮三音区安排 0.45 秒显式等待；预热、加载和 DSP 成本另计 |
| Vital 原生 | 每个音区启动一个 Vital CLI，使用各自的私有工作目录 | 独立进程隔离已有基础，仍需验证实际安装版本能否安全同时运行 |
| Sylenth1 x86 | 每轮启动独立 x86 宿主，三个音区分别建立新插件上下文 | 可以从不同预制间的进程并行着手，保留宿主现有 STA 入口 |
| 快速模式延长 | 一轮声音不足时，重新采集完整三音区；已经满意的片段会保留 | 已完成片段免于再次裁切，但相应音区仍会参与再次采集 |
| 首次声音参照 | 对支持的后端生成三份独立参考，每份都有三音区 | 固定采集情况下对应九次插件实例化；这部分应先准备一次再供任务共享 |
| 校验与持久化 | 输入哈希、参考 WAV 核对、音频解码分析、任务前后保存 | 都有真实工作量，目前缺少分阶段计时，不能认定谁是主要瓶颈 |

本地依据：[DawDreamer 桥接](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/scripts/renderers/dawdreamer_worker.py#L327)、[固定等待与预热](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/scripts/renderers/dawdreamer_worker.py#L381)、[快速采集循环](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Worker/RenderDispatcher.cs#L53)、[Vital 调用](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Worker/RenderDispatcher.cs#L197)、[外部宿主调用](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Worker/RenderDispatcher.cs#L249)、[三份声音参照](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Services/InitBaselineService.cs#L49)、[Sylenth1 宿主](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Vst2Host/VstRenderer.cs#L39)。

预热中的“渲染 0.25 秒音频”表示处理相应数量的采样帧，不能直接换算成 0.25 秒墙钟等待。已有历史基准验收耗时还包含多份参考及普通音色检查，不能当成单预制的常规生成时间。本次没有用合成任务的并发倍数替代真实插件测速。

## 为什么优先选择独立进程

### DawDreamer 0.8.3 的版本限制

项目固定版本的 Python 绑定直接暴露 `RenderEngine::render`，未在该调用上设置释放 GIL 的 guard；pybind11 也不会隐式释放 GIL。因此，在项目使用的标准 CPython 中，同一解释器里的多个 Python 线程不能依靠这些绑定让 `engine.render()` 调用重叠执行。独立 Python 进程各有解释器，可以从进程层面并行；插件内部的原生线程是另一个层次。依据：[DawDreamer v0.8.3 绑定](https://github.com/DBraun/DawDreamer/blob/v0.8.3/Source/source.cpp#L503-L511)、[对应 pybind11 的 GIL 说明](https://github.com/pybind/pybind11/blob/dc9b39596d986aeb061bd3debe52d30e2467dc48/docs/advanced/misc.rst#L42-L45)。

DawDreamer 当前主线的线程文档不能直接套用到固定的 0.8.3；后续若升级依赖，需要重新核对绑定、插件加载协调和兼容性，单独验证后再考虑同进程线程方案。

### 插件实例与线程约束

插件创建、配置与音频处理应遵守宿主线程模型。VST3 的接口有规定的线程使用场景，JUCE 的同步实例创建还会协调到 message thread；不能任意把同一个插件实例交给多个线程同时切换预制和渲染。VST3 对离线处理有具体例外，其线程规则也不表示所有独立实例必须全局串行。依据：[VST3 线程模型](https://steinbergmedia.github.io/vst3_dev_portal/pages/Technical%2BDocumentation/API%2BDocumentation/Index.html#vst-3-threading-model)、[离线状态调用说明](https://steinbergmedia.github.io/vst3_dev_portal/pages/FAQ/Processing.html#q-can-icomponent-getstate-setstate-be-called-during-processing)、[固定 JUCE 实例创建约束](https://github.com/juce-framework/JUCE/blob/cf4f12a452611e46d29e4549ce69adac345706b7/modules/juce_audio_processors/format/juce_AudioPluginFormat.h#L156-L161)。

现有独立宿主可以继续保留自己的线程和消息循环；父进程只负责同时管理多个任务。进程隔离还能让一个插件崩溃或挂起时，其他任务有机会继续完成。

### Vital 与 Sylenth1 的边界

Vital 的公开源码允许多个应用实例，命令行 headless 路径建立局部合成器并在渲染后退出，支持继续验证多进程方案。但该公开源码不等同于本机新版本二进制的实测保证。依据：[多实例声明](https://github.com/mtytel/vital/blob/636ca0ef517a4db087a6a08a6a8a5e704e21f836/src/standalone/main.cpp#L257)、[headless 生命周期](https://github.com/mtytel/vital/blob/636ca0ef517a4db087a6a08a6a8a5e704e21f836/src/standalone/main.cpp#L456-L494)。

同一公开版本的启动链还可能在“已安装且配置版本需要升级”时写入用户级共享配置；各任务的私有工作目录不隔离这个文件。Vital 并发验证应先完成单实例首次初始化，并检查并发前后配置指纹，后端默认保持 1 槽直至实测通过。此处有条件写入的源码证据，不表示已观察到当前二进制发生配置竞争。依据：[启动检查](https://github.com/mtytel/vital/blob/636ca0ef517a4db087a6a08a6a8a5e704e21f836/src/common/startup.cpp#L22-L29)、[共享配置写入](https://github.com/mtytel/vital/blob/636ca0ef517a4db087a6a08a6a8a5e704e21f836/src/common/load_save.cpp#L1261-L1294)。

内置 Sylenth1 x86 宿主已按进程及音区隔离插件实例，适合验证跨预制并发。商业插件是否有授权、全局资源或共享配置限制，仍需逐种插件、版本和平台测试；不能从宿主结构推导出无限并行安全。

## 队列改造必须处理的问题

| 问题 | 当前依据 | 建议处理方式 |
| --- | --- | --- |
| 统计与在飞状态 | `summary`、单个 `current`、`occupiedBytes` 依赖串行顺序 | 父进程统一管理计数和在飞集合；每项使用独立设置与记录快照 |
| 同一缓存键的任务 | 最终路径都是 `key.wav`，原子移动会覆盖目标；不同记录可共享同一缓存键 | 按缓存键合并在飞任务或互斥提交，统一复用结果，保证 WAV 与音区、指标对应 |
| 声音参照竞争 | 基准字典的检查再创建、`InitBaselinePath` 更新依赖串行 | 按插件与模板身份只准备一次，完成并保存定位后发布只读参考 |
| 空间预算 | 多项会看到同一份“剩余空间”；旧文件抵扣可能重复计算 | 执行前全局预留，失败 / 停止释放，提交按实际大小结算；旧文件仅抵扣一次 |
| 磁盘峰值 | 当前预算只扫描根目录 WAV，不包括基准及各级临时采集 | 最终缓存限额与磁盘峰值分别控制，包含并发 staging、quick 和原始 WAV |
| 数据库写入 | `LibraryStore` 已用实例级锁串行写；Worker 当前不直接写库 | 保留父进程单写者提交，无需让每个 Worker 争写数据库 |
| 用户停止 | 当前仅跟踪并恢复一个 `current` | 停止发新任务，取消全部在飞进程树，等待清理并恢复所有未完成项 |
| 单项崩溃或超时 | 已有逐进程超时和取消 | 保持单项失败隔离；超时从取得执行槽后计算，不含排队时间 |
| 提交与取消同时发生 | 当前约定音频原子移动成功后继续持久化 | 收束任务时保留这一约定，避免已有文件却没有相应记录 |
| 界面进度 | 当前只展示一个最新任务事件 | 集中发布单调的完成数，补充正在运行数量，避免乱序回报导致进度倒退 |
| 用户元数据 | 工作项快照可能晚于其他状态写回 | 只合并生成相关字段，保留收藏、反馈、手工标签和分类 |

位置：[队列状态与调度](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Services/RenderQueue.cs#L61)、[基准准备](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Services/RenderQueue.cs#L157)、[预算与最终路径](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Services/RenderQueue.cs#L181)、[覆盖式原子提交](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Audio/WorkerProcessClient.cs#L85)、[旧缓存引用管理](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Services/RenderQueue.cs#L338)、[数据库写锁](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Storage/LibraryStore.cs#L134)、[进程超时](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Audio/ProcessRunner.cs#L50)、[Windows / Mac 进程树管理](https://github.com/Reguluz/SoundPreviewer/blob/eb98c099673a515a1ea6d4a92ca725c418039a0c/src/SoundPreviewer.Core/Audio/ProcessJob.cs#L23)。

SQLite WAL 允许读写并发，但同一数据库仍只有一个写者。增加写库进程不会得到并行写入收益，保留短事务和父进程提交更符合当前架构。依据：[SQLite WAL 并发说明](https://sqlite.org/wal.html#concurrency)。

仅给当前循环加 `Task.WhenAll` 或只限制任务数量，不能解决上述提交和恢复问题。即使写库本身有锁，缓存文件、预算、基准和记录对象仍需独立协调。

## 建议实施顺序

1. **先加阶段计时。** 分开记录队列等待、声音参照准备、Worker 启动、Python 导入、插件创建、加载与回读、等待 / 预热 / DSP、WAV 分析、文件和数据库提交。记录冷启动与已有基准两种状态。
2. **建立统一协调器。** 固定批次配置快照，预先或按身份合并基准准备；协调缓存键、空间预留、完成提交和所有在飞项的收尾。复用现有工作进程协议及私有临时目录。
3. **开放受限实验。** 兼容默认值为 1，先测试 2 槽，再按内存和 CPU 结果决定是否测试 4 槽。增加全局上限、按音源 / 后端限制和退回串行的选项；未验证的自定义外部宿主保持 1。
4. **通过真实样本再确定默认值。** 对每个平台及音源单独比较吞吐、失败率、峰值资源和声音一致性，不按 CPU 逻辑核心数直接启动同等数量插件。
5. **再评估其他优化。** 根据计时考虑减少重复哈希 / 解码、只重采集不足音区。长期复用宿主和插件会改变新实例保证，需要额外研究预制状态、尾音、随机数、DSP 历史与外部资源重置；不与第一轮并行改造混在一起。

并行带来的增益受可重叠工作比例、插件内部线程、内存容量、磁盘和共享资源影响。若某音色已占满 CPU，增加实例可能降低速度；若启动、等待或单实例计算尚未充分利用机器，则有提高吞吐的空间。目前没有足够数据给出“2 槽快一倍”之类承诺。

## 如何验证确实更快且不损坏旧缓存

使用同一机器、相同插件版本与授权、同一批有代表性的独立音色，分别测 1 / 2 / 4 槽。建议每组至少 30 个音色，包含普通起音、慢起音、外部资源、静音与失败案例，重复运行并记录中位数及尾部耗时。2 / 4 槽仅在前一档资源和正确性通过后运行。

冷启动测试包含新建声音参照；已有基准测试复用有效参考，另记每预制耗时。复用已有预览的速度单独统计，生成测试使用独立输出目录与库副本，避免把缓存命中当成渲染提速。不改写原音色、原用户数据库或原缓存。

| 观察项 | 要求 |
| --- | --- |
| 速度 | 总墙钟耗时、每分钟生成数、各阶段中位数及 P95；单独统计新生成和缓存复用 |
| 资源 | 整个进程树的 CPU、峰值内存、进程数、临时磁盘峰值；同时关注 UI 响应和试听 |
| 声音与身份 | 加载 / 状态回读成立，三音区顺序与映射完整；检查静音、削波、非有限值并抽样试听 |
| 随机性 | 有调制或随机振荡器的音色不强求字节相同；比较身份、结构、指标与可听结果 |
| 可靠性 | 并行后超时、崩溃、错误音色和不完整结果不增加；分别验证各音源版本 |
| 兼容 | 新旧模板缓存键、旧预览默认复用、手工元数据与收藏保持；调度数量不应改变音色缓存身份 |
| 退回串行 | 1 槽行为与当前版本等价，未配置并发字段的旧设置默认 1 |

新增回归应覆盖：不同键任务确实重叠但不超上限；同键与共享旧缓存；缺基准时仅创建一套参考；预算预留与释放；多项停止；单项崩溃 / 超时；取消恰逢提交；数据库故障；重启恢复；Windows / Mac 无残留宿主且不误杀其他任务；进度与计数一致。

满足正确性、资源上限和可重复的吞吐改善后，才适合将并行选项交给测试用户。安装包 CI 的合成测试可验证调度协议，真实插件性能与音色验收仍需各平台实机完成。

## 相关文档

[用户操作说明](../README.md) · [开发说明](../README_DEV.md) · [渲染协议与实测记录](rendering.md) · [架构](architecture.md)
