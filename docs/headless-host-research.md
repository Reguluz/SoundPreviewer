# 无界面宿主与 Portable 研究

记录日期：2026-10-21。本轮先完成并交付 0.7.7 空格重播功能，再研究替代 DawDreamer 的原生宿主。0.8.0 已本地接通手选自备 Sushi 的 Windows x64 / Serum 1 VST2 实验路径，包含应用配置、独立 Worker、InitBaseline 与 RenderQueue。公开分发许可尚未确认；普通安装包和公开 CI 不捆绑 Sushi 原生二进制。

## VST 2.4 SDK 的取得与边界

找到第三方公开镜像 [R-Tur/VST_SDK_2.4](https://github.com/R-Tur/VST_SDK_2.4)，研究副本固定为 `4610873fa433d263a5c4a68b5b8b59ffcf6ca6db`。其中的 `pluginterfaces/vst2.x/aeffect.h`、`aeffectx.h` 满足 Sushi 的编译输入。

镜像的可取得性与技术可编译性不证明 Steinberg 授权、文件原厂签名或使用者持有历史 SDK 许可。SDK 头文件本身包含再分发约束；仓库、研究产物和安装包均不复制 SDK 原件，复现入口必须通过显式参数接收外部 SDK。来源指纹审计保存在本地 `artifacts/research/vst-sdk-2.4-source-audit.json`。

官方背景：[VST 2 SDK 停止提供](https://forums.steinberg.net/t/vst-2-sdk-discontinued/201774)、[宿主 VST2 支持的官方说明](https://forums.steinberg.net/t/can-i-create-vst-2-support-for-host-application-vst-3-license/202012/2)。

## Sushi 的本地结果

Sushi 来源固定为官方 [elk-audio/sushi](https://github.com/elk-audio/sushi) 提交 `c6ddde6b6b6c06229118432c8c7f7a7c61a61408`，源码版本 1.4.0。官方 Windows 1.4 包的能力检查仅显示 VST3，本轮另用外部 VST 2.4 SDK 编译同时启用 VST2 / VST3 的 Windows x64 宿主。

精简构建关闭实时设备、RPC、LV2、Link、Sentry 和上游测试，静态链接 Twine 与 MSVC CRT；设置 `CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded` 与 `SMTG_USE_STATIC_CRT=ON`。加入当前兼容补丁的研究产物为 `sushi.exe` 2,138,624 字节、独立 `sndfile.dll` 2,603,520 字节。宿主本身不依赖 Python 或 uv，libsndfile 仍是动态库；精确文件指纹及以后重放的大小以各次构建清单为准。

本地新增 [补丁](../patches/Sushi1.4/README.md) 支持读取单 FXP 的程序 chunk 或原始状态、加载后状态回读，以及定时 MIDI CC。它还修正 VST2 flags 检查时误改 flags 的问题，并补充可关闭 LV2 的构建保护和 Windows 系统库链接。

Unicode 兼容补丁为实验宿主加入进程内 UTF-8 manifest、启动代码页检查，并用 `u8path` 和 `LoadLibraryW` 加载 VST2 插件路径。实验宿主要求 Windows 10 1903 或更新版本；代码页不符合时明确退出，不修改系统编码设置。该要求只属于实验宿主，SoundPreviewer 主程序原有 Windows 10 1809 最低要求不变。

两份真实 Serum 1 FXP 已在本机的研究副本中成功加载，插件报告的程序名与目标相符，并生成正常音频输出。但插件回读的二进制状态与原 FXP chunk 有变化，不能将“成功回读”“名称相符”称为精确的目标状态证明，也不扩大到其他 Serum 版本、Serum 2、全部音色或跨平台验收。源预置与已有 Preview / Config 不参与构建输出。

## 应用链路的本地验收

本地已从应用的实验生成方式配置，经 Worker 实际调用研究宿主，再由 InitBaseline 校准默认启动声音，最终通过 RenderQueue 生成、写入和复用预览。首批范围为 Windows x64 的 Serum 1 VST2 / `XfsX` 单程序 `FPCh` FXP；其他音源、格式和平台继续使用已有路径。

| 检查 | 本次结果 |
| --- | --- |
| 真实 Worker 正常用例 | 10/10 通过：两份 FXP 在 44.1 / 48 kHz 下分别运行固定与快速模式共 8 项，另有两个采样率的默认启动生成共 2 项 |
| Worker 拒绝用例 | 7/7 按预期拒绝：错误 FXP 身份、银行冒充单音色、截断 chunk、扫描后源文件变化、基准请求携带预置、错误插件扩展名、输出额度不足；已有预览保留，临时数据清理通过 |
| 44.1 kHz 完整队列 | 新生成 2、失败 0；再次执行复用 2，复用音频 SHA-256 未变 |
| 48 kHz 完整队列 | 新生成 2、失败 0；再次执行复用 2，复用音频 SHA-256 未变 |
| 默认音色防错 | 两个采样率均完成三份独立参考；测试用默认状态 FXP 的三个音区整体匹配 3/3 份默认参考，按预期拒绝保存 |
| 源文件保留 | 正常用例、拒绝用例和两次完整队列检查均确认原 FXP 的 SHA-256 未变 |
| 中文、emoji 与超长输出 | 独立 Worker 重跑正常模式矩阵 10/10 通过；宿主、DLL、音源及两份 FXP 均位于中文 / emoji 路径，输出根长 326 字符，音频非静音且帧数正确；五个输入文件 SHA-256 均未变 |
| 中文、emoji 与超长完整队列 | Core → Worker → 原生宿主在长 330 字符的预览根目录及更深的基准目录下完成 44.1 kHz 全链路；新生成 2、失败 0、复用 2，默认状态匹配 3/3 份参考并拒绝保存；源文件及复用音频 SHA-256 未变 |

原始本地报告分别在 `artifacts/research/sushi-worker-validation-v2/report.json`、`sushi-worker-rejections-v1/report.json`、`sushi-queue-44100-5adb54d626e542d391c20f903599eb3f/report.json` 和 `sushi-queue-48000-a69333dab0054d12b664b46e8e74db89/report.json`。这些隔离研究目录不进入普通包或公开 CI 诊断上传。

Unicode 独立 Worker 报告与输入指纹在 `artifacts/research/中文路径🎹-83f4394f/report.json`、`source-audit.json`；完整队列汇总在 `artifacts/research/sushi-queue-longpath-v3.log`，其中记录对应研究目录。Windows 的 WorkerProcessClient 在 staging 路径达到 240 字符时，改用较短的 Worker 所在目录或盘符根目录启动，绝对 request / result 路径保持不变；超过 310 字符的协议回归及上述真实完整队列均已通过。本次验证覆盖这些隔离路径用例，不扩大为任意路径或所有平台保证。

当前每个音区新建插件实例，避免前一音区的延迟或尾音串入；尚未实现宿主驻留后的连续换预置，不承诺加载耗时或渲染性能优于 DawDreamer。当前预览保留待试听确认标记，不把默认启动防错检查当作精确目标状态证明。可复现脚本、固定输入与选项见 [本地构建说明](sushi-build.md)。

## 公开分发许可尚待确认

官方 [Elk 许可说明](https://elk-audio.github.io/elk-docs/html/intro/license.html) 明确讨论 hosted plugins。固定来源中的 [LICENSE.md](https://github.com/elk-audio/sushi/blob/c6ddde6b6b6c06229118432c8c7f7a7c61a61408/LICENSE.md) 对载入的插件、应用及接口组合提出源码条件，不能仅依据包可下载或 AGPL 标题推定可无条件与商业音源一起分发。

因此，本轮保留来源、补丁、应用内手选自备宿主的实验入口及本地构建脚本，**不公开捆绑 Sushi 二进制，也不把它设为默认渲染器**。直接依赖和已发现的未完许可项见 [来源与许可清单](../third-party/sushi/THIRD-PARTY-NOTICES.md)。公开捆绑方式仍须确认适用分发条件；当前开发版的 DawDreamer、其他音源和既有有效缓存继续保留。
