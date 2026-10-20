# 本地 Sushi 研究构建

此入口只复现 Windows x64 离线宿主研究，不将 Sushi 纳入默认 CI 或安装包。公开分发许可尚未确认，参见 [研究记录](headless-host-research.md) 与 [直接依赖及许可清单](../third-party/sushi/THIRD-PARTY-NOTICES.md)。

## 前置内容

- Windows x64、PowerShell 7、Git、Visual Studio 2022 的 C++ x64 工具链及 CMake 3.28 以上。
- 外部 VST 2.4 SDK 目录，至少包含 `pluginterfaces/vst2.x/aeffect.h` 和 `aeffectx.h`。脚本不自动下载 SDK，不把 SDK 文件复制进仓库或输出。
- 官方 [libsndfile 1.2.2 win64 开发包](https://github.com/libsndfile/libsndfile/releases/tag/1.2.2) 的解压目录，内含 `cmake/SndFileConfig.cmake`、`lib/sndfile.lib`、`bin/sndfile.dll`。

实验宿主运行要求 Windows 10 1903 或更新版本，以支持进程内 UTF-8 manifest。入口检查实际代码页，不是 UTF-8 时在读取路径前明确退出；不修改系统区域或编码设置。SoundPreviewer 主程序原有 Windows 10 1809 最低要求不变，其他既有后端不受这一实验要求影响。

## 构建

```powershell
./scripts/Build-Sushi.ps1 `
  -Vst2SdkDirectory 'D:/ExternalSDKs/vstsdk2.4' `
  -SndFileDirectory 'D:/ExternalSDKs/libsndfile-1.2.2-win64' `
  -CMakePath 'C:/Program Files/CMake/bin/cmake.exe'
```

`CMakePath` 可指向 Visual Studio 自带的 CMake。编译工具只在开发机使用，研究产物运行不依赖 Python 或 uv。

脚本将官方 Sushi 固定在 `c6ddde6b6b6c06229118432c8c7f7a7c61a61408`，仅获取该提交记录的必需上游子模块，应用 [保留补丁](../patches/Sushi1.4/README.md)。它跳过未使用的测试、文档、VSTGUI、设备后端、RPC 与 vcpkg 下载；不使用外部 SDK 镜像。现有来源若提交不符、修改与补丁不符或目录含用户数据，会保留并报错，不自动重置或清空。

默认来源、构建与产物分别位于 `artifacts/sushi-source-1.4.0`、`artifacts/sushi-build-win-x64`、`artifacts/sushi-research-win-x64`，可通过三个对应的目录参数指定其他 `artifacts` 子目录。不要将其指向正在使用的程序目录或音色库。

编译同时启用 VST2 / VST3、静态 Twine 与静态 MSVC CRT，设置 `CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded`、`SMTG_USE_STATIC_CRT=ON`，关闭 JACK、PortAudio、CoreAudio、ALSA / RtMidi、LV2、Link、RPC、Sentry 和上游测试。仅构建 `sushi` 目标；`sndfile.dll` 仍为独立动态库，未静态合并到宿主。

输出包含 `sushi.exe`、`sndfile.dll`、研究许可说明、许可文本及 `build-manifest.json`。清单记录来源、补丁、外部 SDK 输入头文件指纹和两个产物指纹；不包含 SDK 内容、音源、预置或授权文件。该入口复现来源与编译配置，不承诺不同工具链的二进制逐字节一致。

## 仅准备来源与校验幂等性

```powershell
./scripts/Build-Sushi.ps1 `
  -Vst2SdkDirectory 'D:/ExternalSDKs/vstsdk2.4' `
  -SndFileDirectory 'D:/ExternalSDKs/libsndfile-1.2.2-win64' `
  -SourceDirectory './artifacts/sushi-source-1.4.0' `
  -PrepareOnly
```

此模式检查外部输入、获取必需子模块并应用或确认已有补丁，不执行 CMake 配置和编译。反复执行不会删除目录或再次叠加相同补丁。实际音源兼容检查须另用隔离数据运行；构建成功与无插件自检不代表 Serum 全版本或全部预置兼容。
