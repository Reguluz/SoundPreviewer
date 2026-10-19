# 音色文件拖出与 DAW 加载

更新：2026-10-20。适用版本：**0.7.0 开发版**，Windows / macOS。

## 目标与推进顺序

目标已明确为：**将选中的音色拖到 DAW 空白位置，自动创建对应音源并加载此音色**。优先适配 **Windows FL Studio**；后续覆盖 Windows / Mac 的 **FL Studio、Bitwig Studio 和 Ableton Live**。本项目当前尚未完成这一端到端能力。

第一阶段先提供可直接使用的文件管理器定位：选中音色后，点击详情区的“定位音色文件”，在 Windows 资源管理器或 Mac 访达中选中原文件。音色包／银行内的条目定位到所属的音色库文件，不能定位到尚未导出的独立音色。此功能不依赖 DAW 接口，也不会打开音源或修改工程。

文件拖出入口保留为传递基础；它与上述自动建轨目标分别记录，不能把文件复制成功当作自动加载完成。

## 当前操作

在音色库中选中一个独立音色，按住详情区的拖动入口，将原音色文件拖到接收软件。接收软件决定是否接受文件、在哪里加载以及如何创建音源。建议先在 DAW 中打开对应音源，再拖到该音源支持的接收位置。

此入口使用系统文件拖放，提供原文件路径并且只允许复制。它不会发送预览 WAV，不移动或改写原音色，也不会自动搬运、打包或补齐音色引用的外部采样、波表等资源。接收软件若将文件复制到其他位置，外部资源仍由该软件和音源处理。

当前可拖出的独立文件包括 Vital `.vital`、Serum 1 / Sylenth1 `.fxp`、Serum 2 原生 `.SerumPreset`、Spire `.spf` / `.spf2`。这里的文件范围只表示应用可以提供原文件，不能据此判断某个插件或 DAW 已支持加载。来源缺失、音源待识别、包内条目、银行槽位和银行文件不能作为单个音色拖出；请先在对应音源中导出独立预制。Serum 2 的旧 `.fxp` 不通过此入口冒充原生 `.SerumPreset`。

## 两种不同的目标

1. **替换已打开音源的音色**：DAW 中已经有对应插件，插件或 DAW 的插件窗口接收音色文件，改变该实例的状态。当前文件拖出入口为此提供基础，能否成功仍须按具体组合验收。
2. **拖到空白处自动创建音源**：DAW 接收文件后，新建轨道、识别并实例化正确插件、加载目标音色。这是已选定的最终目标；此流程通常需要 DAW 自己的设备／轨道预制或专属桥接。优先针对 Windows FL Studio 验证，原生音色文件拖出本身不包含这些能力。

系统返回“接受了复制”只能说明接收方接受文件传递，不能证明插件已加载、音色一致或外部资源齐全。当前界面因此仅显示文件拖出结果。

## 官方资料与兼容边界

以下是厂商文档或厂商官方源码提供的依据，**不是本项目已经完成的 DAW 端到端验收矩阵**。

| 音源／宿主 | 官方资料确认的行为 | 本项目的判断与待验证项 |
| --- | --- | --- |
| Serum 1 | Steve Xfer 在官方论坛说明，可将 `.fxp` 拖到已打开 Serum 的 A/B 振荡器波形区域，加载完整音色。[厂商回复](https://xferrecords.com/forums/general/drag-and-drop-preset-onto-serum-to-load-it) | 可作为已有插件实例接收文件的候选方案；具体 DAW、插件版本及 Windows / Mac 组合仍需验证。该回复不证明拖到 DAW 空白区域可自动创建 Serum。 |
| Serum 2 | 官方手册介绍先在 DAW 添加 Serum，再从音色浏览器或 Load Preset 加载。[音色加载](https://xferrecords.com/web-manual/serum-2/loading-a-serum-preset)；手册中的“Dragging Audio to Your DAW”指 WAV 导出，Shift 拖动则将保存的预制复制到 Finder / Explorer。[拖出说明](https://xferrecords.com/web-manual/serum-2/dragging-audio-to-your-daw) | 本次未从上述官方资料确认外部 `.SerumPreset` 在各 DAW 的接收位置，或自动建轨能力。音频拖出说明不能当作预制自动加载的依据。 |
| Sylenth1 | 官方版本史注明 **3.029** 加入 FXP / FXB 文件拖放，**3.072** 加入 `.vstpreset` / `.fst` 拖放支持。[版本史](https://www.lennardigital.com/sylenth1/whatsnew.php) | 本项目历史真实渲染样本使用 **2.2.1.2 x86**，不能据新版资料承诺旧版插件能接收拖放；新版插件也需在目标 DAW 中验收。银行拖放能力不代表本项目能将其中某个条目作为独立音色交付。 |
| Vital | 官方公开仓库提供插件与界面源码，同时说明源码更新滞后于二进制版本。[官方仓库](https://github.com/mtytel/vital) | 本次检查公开的 FullInterface、PresetBrowser、SynthGuiInterface 未找到预制拖放处理。该结果不能推断当前发行二进制一定不支持；`.vital` 拖入插件加载保持未确认。 |
| Spire | 本项目可以提供独立 `.spf` / `.spf2` 原文件。 | 本次尚未取得可确认目标接收位置与自动实例化行为的厂商依据，也未完成真实拖放验收；不因可拖出文件而扩大预览渲染或 DAW 兼容声明。 |
| FL Studio | Wrapper 有“Accept dropped files”，可让 VST 插件优先处理拖入文件；FXP / FXB 导入描述针对已托管插件实例。FL 的“Save preset as”及通道状态还可拖到兼容位置。[官方 Wrapper 手册](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/wrapper.htm) | 原生音色、FL 设备预制和通道状态不能混同。需验证具体文件是交给插件还是由 FL 处理；不以改扩展名方式伪造 `.fst`。 |
| Ableton Live | 官方说明可从 Explorer / Finder 将 `.adg`、`.adv`、`.amxd` 等 Live 专用设备文件拖入 Live Set；VST 预制另有导入入口。[设备预制](https://www.ableton.com/en/manual/working-with-instruments-and-effects/)、[插件与预制](https://www.ableton.com/en/live-manual/12/using-plug-ins/) | Live 设备预制的行为不能外推到 `.fxp`、`.vital`、`.SerumPreset`。自动建轨需要进一步验证合适的 Live 容器或桥接方式。 |

## Windows FL Studio 深入调研

本机仅通过安装元数据与可执行文件版本读取，确认存在 FL Studio **25.1.0.4868**，另有 **24.2.1.4526** 和 **21.0.1.3348**。这不是当前 FL 最新版本声明，也不表示已在这些版本启动插件或验收拖放。本次未操作 DAW、读取工程或修改 FL 设置。

### 原生音色不能直接当作 FL 状态文件

Image-Line 将 `.fst` 定义为用于保存生成器与效果器预制的 FL 状态文件，官方创建方式来自 FL 的 Save Channel State As / Save Preset As。[状态文件说明](https://www.image-line.com/fl-studio-learning-content/fl-studio-online-manual/html/fformats_other_fst.htm)

Wrapper 手册区分插件预制与整个通道状态：后者还包括 wrapper、采样和杂项设置。官方 Browser / Plugin Picker 文档说明，FL 的设备预制可加载为插件；Playlist 文档中的乐器轨道工作流要求把乐器拖到**轨道标题区**，这不同于音频／Pattern 的编排画布。[Wrapper](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/wrapper.htm)、[Browser 与 Plugin Picker](https://www.image-line.com/fl-studio-learning-content/fl-studio-online-manual/html/browser.htm)、[Playlist](https://www.image-line.com/fl-studio-learning-content/fl-studio-online-manual/html/playlist.htm)

据这些资料，可靠的候选路线是**先由 FL 保存真实插件／通道 `.fst`，再由 SoundPreviewer 关联并提供这个文件**。它需要每个音色对应的实际状态，不是每种音源仅准备一个空模板。本项目尚未实现这种关联，也尚未验证外部文件拖到 Channel Rack 空白区、Playlist 轨道标题区及编排空白区时的具体差异。

本次没有找到厂商公开的 `.fxp`、`.vital`、`.SerumPreset` 到 wrapper `.fst` 的自动转换接口或可据以实现通用写入器的格式规范。本项目不会以改扩展名、替换模板字节或逆向私有格式来声称完成转换。现有渲染宿主取得的音源状态也不能直接等同于 FL 通道状态。

### 官方扩展接口的覆盖范围

| 官方入口 | 本次确认的用途 | 对本目标的限制 |
| --- | --- | --- |
| FL 保存的预制／通道状态 | 由 FL 保存实际插件及通道设置，通过兼容位置再加载。 | 是后续优先验证的候选，但不能自动把所有现有原生音色批量转换为 `.fst`。 |
| Python MIDI scripting | 已有通道／插件参数控制、预制前后切换、Browser 导航和 UI 操作等。[官方 API 表](https://www.image-line.com/fl-studio-learning-content/fl-studio-online-manual/html/midi_scripting.htm) | 公开表中未列出按任意外部文件路径新建音源通道、加载这些原生预制并导出 `.fst` 的完整接口。Browser 导航／菜单操作依赖当时 UI 状态，不能据此承诺可靠的外部文件桥接。 |
| FL 命令行 | 官方列出既有 `.flp` 的音频、MIDI、ZIP 导出及部分诊断功能。[命令行说明](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/fformats_save_export.htm#commandline_export) | 未列出向当前工程插入指定音源、加载原生音色或生成 wrapper `.fst` 的命令。工程导出能力不能代替创建通道的接口。 |
| FL 原生 Plugin SDK | [开发者页](https://www.image-line.com/developers)仍保留下载入口，但入口指向的[官方管理员帖子](https://forum.image-line.com/viewtopic.php?f=700&t=12092)目前说明该 SDK 已不再提供，并建议开发 VST / VST3 / AU / CLAP 插件。 | 该 SDK 原本面向 FL 插件开发，不是已经确认可用的外部宿主控制服务；改做标准插件也不自动获得创建其他音源通道的权限或接口。 |

以上是当前官方公开入口的核对结果，不能推断所有未来版本都无法支持。现阶段没有足够依据将自动建轨作为已完成能力，因此先使用文件定位；真实 `.fst` 关联或厂商支持的专属桥接完成后，再增加相应入口。FL、Bitwig 与 Live 的容器和控制接口分别验收，不共用未经确认的格式假设。

## 实现和当前验证范围

Windows 与 macOS 共用 Avalonia 文件拖放入口，使用 `DataTransferItem.CreateFile` 和 `DragDrop.DoDragDropAsync`，限制为 `DragDropEffects.Copy`。[Avalonia 官方 API](https://api-docs.avaloniaui.net/docs/T_Avalonia_Input_DragDrop)

应用内自动检查覆盖原始文件 payload、来源与预览不被改写、包内条目／银行槽位／未知音源／缺失来源／预览音频被拒绝，以及两个窗口尺寸下的入口布局。这些检查验证本应用提供的文件及界面，**尚未验证真实 DAW 接收后正确加载音色**；Windows 与 Mac 的系统交互也需纳入实际 DAW 验收。

## 下一阶段验收

先以本机 Windows FL Studio 25.1.0.4868 为候选基线，验证由 FL 保存的真实预制／通道状态能否满足**空白处创建音源并加载音色**。分别核对 Channel Rack 空白区、Playlist 轨道标题区与编排画布，不把落点混为一谈。每个目标组合记录：

- 操作系统、DAW、音源版本及 VST2 / VST3 / AU 格式，实际拖入位置。
- 原文件传递成功，目标实例或新建实例正确，音色名称、关键状态与人工试听一致。
- 外部采样和波表正常加载；保存并重开 DAW 工程后可还原。
- 拖动取消、接收方拒绝、来源缺失等情况下，原音色、预览、标签和分类保持原样。

通过 Windows FL 的真实检查后，再推进 Windows / Mac 的 FL、Bitwig 和 Live 对应方式；每个组合通过后再列为可用操作，说明具体落点与是否自动创建。已有插件窗口中的音色替换只作为独立兼容记录，不能代替最终目标验收。
