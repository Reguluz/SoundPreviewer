# 操作说明首页

**简体中文（默认）** · [English](en/index.md)

<a id="support"></a>

## 支持创作

如果这个工具帮你节省了试音时间，可以扫描下图支持后续开发。

<img src="images/afdian-Reguluz.jpg" alt="Reguluz 爱发电支持二维码" width="375" height="513">

## 软件简介

SoundPreviewer 把合成器音色生成短试听片段，让你在一个本地音色库里搜索、筛选和播放。已有预览无需打开 DAW 或再次加载音源。

这份 Markdown 操作说明随 **1.0.1** 程序提供，Windows 安装包和免安装包另附编译好的 CHM 手册。正文、截图和章节链接均可离线阅读；GitHub、运行库下载及技术记录链接需要联网。本文以简体中文界面为例，英文界面使用同样的页面布局。

<a id="interface"></a>

## 主界面一览

[![SoundPreviewer 中文主界面：搜索筛选、音色列表、详情、播放器与生成进度](images/library.png)](images/library.png)

*点击截图查看原图。截图由用户提供，拍摄自 1.0.0；图中的音色、统计与路径来自截图所示的音色库。*

| 界面区域 | 可以做什么 |
| --- | --- |
| 顶部工具栏与页签 | 扫描音源、扫描音色、生成 / 继续、停止；切换音色库、运行记录和音源与设置 |
| 搜索框与筛选区 | 搜索名称、标签、路径，按音源、状态、来源、音色包、分类、标签和收藏组合筛选 |
| 中间音色列表与右侧详情 | 选择音色、查看状态与结果、收藏、编辑分类标签、重新生成或定位原文件 |
| 下方播放器 | 按空格重播、分别试听中 / 高 / 低音区、调整进度与音量 |
| 底部任务区与右下角书本图标 | 查看生成统计和日志、复制结果；打开本操作说明 |

新用户可直接进入[最小上手流程](quick-start.md)，英文说明见[English user guide](en/index.md)。

<a id="compatibility"></a>

## 当前支持

### 平台与安装包

| 平台 | 系统与架构 | 当前发行包 |
| --- | --- | --- |
| Windows | Windows 10 1809 及以上 / Windows 11，x64 | 安装版 `.exe`、免安装版 `.zip` |
| macOS · Apple Silicon | macOS 12 及以上，M 系列芯片（arm64） | `.dmg`、`.zip` |
| macOS · Intel | macOS 12 及以上，Intel（x64） | `.dmg`、`.zip` |
| Linux / Windows ARM64 | 暂未提供专用发行包 | 暂无 |

系统下限来自安装包配置；下载与启动步骤见[安装与启动](installation.md)。音源的插件架构还须与生成环境匹配。

### 音源、插件与音色格式

| 音源 | 生成时使用的程序 / 插件格式 | 音色文件格式 | 当前支持情况 |
| --- | --- | --- | --- |
| Vital | Vital 独立程序 | `.vital`；符合资源检查的 `.vitalbank` 包内音色 | 可生成；Windows 和 Apple Silicon 已有真实样本验证，Mac 包内生成尚待验证 |
| Serum 1 | VST2 | 独立 `.fxp` | 可生成；Windows x64 已有真实样本验证，Mac 尚待验证 |
| Serum 2 | VST3 | 原生 `.SerumPreset` | 可生成；Apple Silicon 已有真实样本验证，Windows / Intel Mac 尚待验证 |
| Sylenth1 | VST2；Windows x86 可用随包宿主 | 独立 `.fxp` | 可生成；Windows x86 已有真实样本验证，其他组合尚待验证 |
| Spire | 原生生成尚未开放 | `.spf`、`.spf2`、`.sbf`、`.swzip` | 可扫描与索引，暂不能生成预览 |
| FXB 银行 | 取决于所属音源 | `.fxb` | 可保留索引，暂不能直接生成；需先在音源中导出受支持的独立音色 |

音源、音色文件与授权需自行准备。“可生成”表示已有生成方式，具体版本和外部资源仍需单音色测试。详细版本、架构及银行限制见[音源与生成环境](synths.md#support)。

### 界面语言

| 内置语言 | 语言编号 | 支持范围 |
| --- | --- | --- |
| 简体中文 | `zh-CN` | 软件界面、默认分类与标签文案 |
| English（英语） | `en-US` | 软件界面、默认分类与标签文案 |

「界面语言」和「分类与标签语言」可以独立选择；音色原名、文件路径及用户自定义名称不会自动翻译。也可添加社区语言 JSON，操作见[语言与外观](appearance.md#language)。

<a id="start"></a>

## 从哪里开始

- **第一次使用**：[快速开始](quick-start.md)按“最小配置 → 单音色测试 → 批量使用”完成一条完整路径。
- **想确认是否有某项功能**：[常规问题 Q&A](quick-start.md#faq)介绍预览音高、自定义标签、筛选、续做等功能。
- **尚未安装**：[安装与启动](installation.md)选择平台、解压或安装，确认程序目录可写。
- **换了音源或插件版本**：[音源与生成环境](synths.md)核对格式、插件架构与生成方式。
- **准备批量生成**：[生成与试听](rendering.md)了解模板、队列范围、内存、状态与播放器。
- **查找和标记音色**：[音色库整理](library.md)介绍目录、搜索、多选筛选、分类、标签和 DAW 文件地址。
- **更新、搬移或清理**：[数据、备份与升级](storage.md)说明 Preview、Config、导入旧库与压缩。
- **切换语言或配色**：[语言与外观](appearance.md)介绍界面设置与可编辑 JSON。
- **出现错误**：[常见问题](troubleshooting.md)按提示定位问题并复制反馈信息。

<a id="open"></a>

## 打开方式

在软件窗口右下角点击书本形状的 **「操作说明」** 图标。Windows 使用系统帮助查看器打开 `UserGuide/SoundPreviewer.chm`，带章节目录；macOS 打开 `UserGuide/index.md`，可使用支持 Markdown 的阅读器。也可以直接打开这些文件。Windows 的 `UserGuide` 位于 EXE 旁；macOS 位于 `.app/Contents/MacOS/UserGuide`，可通过访达的「显示包内容」找到。

Markdown 和 CHM 均随安装包更新。单独复制 EXE 会缺少所需资源，请保留完整程序目录。

CHM 默认打开中文首页，章节目录按「简体中文（默认） / English」分组；每页顶部也可切换到对应语言。英文 Markdown 首页位于 `UserGuide/en/index.md`。

---

[操作说明首页](index.md) · [快速开始](quick-start.md) · [安装与启动](installation.md) · [音源配置](synths.md) · [生成与试听](rendering.md) · [音色库整理](library.md) · [数据维护](storage.md) · [语言与外观](appearance.md) · [常见问题](troubleshooting.md)

适用于 SoundPreviewer 1.0.1。正文和截图可离线阅读，外部下载与反馈链接需要联网。
