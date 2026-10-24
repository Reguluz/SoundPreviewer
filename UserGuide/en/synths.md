# Synths and rendering setup

[简体中文（默认）](../synths.md) · **English**

On this page:

- [Support](#support)
- [Planned support](#_1)
- [Scan and link a synth](#scan)
- [Check the rendering path](#backend)
- [Prepare DawDreamer](#environment)

<a id="support"></a>

## Support

An available rendering path still requires checks with your plugin version, license, and external resources. Windows and Mac share the interface, but successful app startup and successful rendering with a specific plugin are separate checks.

| Synth | Presets that can generate | Required setup | Real sample checks so far |
| --- | --- | --- | --- |
| **Vital** | Individual `.vital`; eligible entries inside `.vitalbank` after embedded-resource checks | Standalone Vital on Windows / Mac, using Vital native mode | Historical Windows samples and two bank entries with Vital 1.6.4; individual test preset on Apple Silicon, with bank generation still awaiting Mac testing |
| **Serum 1** | Individual `.fxp` | VST2 plugin and the prepared DawDreamer environment | Windows Serum 1.334 x64; [five-preset concurrency results](https://github.com/Reguluz/SoundPreviewer/blob/main/docs/parallel-rendering.md#060-windows-五样本实测2026-10-20); real Mac plugin testing pending |
| **Serum 2** | Native `.SerumPreset` | VST3 plugin and DawDreamer with Serum 2 dependencies | Two factory presets with Serum 2 2.0.23 on Apple Silicon; Windows and Intel Mac testing pending |
| **Sylenth1** | Individual `.fxp` | Bundled host for Windows x86; DawDreamer for other matching VST2 architectures | Five presets with Windows Sylenth1 2.2.1.2 x86 at concurrency 1 / 2 / 4; other combinations pending |
| **Spire** | No preview generation yet | `.spf`, `.spf2`, `.sbf`, and `.swzip` can be indexed and appear as Unavailable | No real plugin generation verification yet |

For Vital banks, **Scan presets** again to run resource checks. Entries with complete embedded wavetables / samples and supported resource types become Pending and can use Vital native rendering. Only the selected entry is loaded in a private task directory; Vital's global resources are not imported or modified. Unsupported or incomplete entries remain Unavailable with a reason. Import the bank into Vital and export an individual `.vital` if needed. Bank files are limited to 256 MiB and individual embedded presets to 16 MiB.

`.fxb` banks and other unsupported bank contents **cannot generate directly**. Export individual presets from the corresponding synth first. Old Serum 2 `.fxp` files must be imported and saved as `.SerumPreset` in Serum 2; renaming the extension does not convert them. If synth identification is uncertain, the details panel asks you to check and assign the synth.

See [rendering verification records](https://github.com/Reguluz/SoundPreviewer/blob/main/docs/rendering.md#本机验证记录) for versions and samples. These checks do not cover every preset, plugin version, or external resource.

<a id="_1"></a>

## Planned support

**Spire native generation** is planned. Its formats can already be scanned and indexed, but real plugin loading, slots, and resources must be verified before generation is enabled. There is no confirmed schedule for other synths. FXB bank slot traversal is also pending. Use the rendering paths in the table for now.

<a id="scan"></a>

## Scan and link a synth

Click **Scan synths**. Under **Synths and settings → Discovered synths**, select the correct plugin version and click **Use selected version**. Common installation locations are scanned automatically; add a missing installation folder under **Folders** and scan again.

Prefer a plugin matching your app architecture. Scanning identifies plugins; you still need to configure how previews are generated.

<a id="backend"></a>

## Check the rendering path

Under **Synths and settings → Preview generation**:

- **Vital:** choose the standalone program and Vital native mode. A VST plugin alone cannot use this native path.
- **Windows Sylenth1 x86:** use the bundled host with the matching plugin. If prompted, install the **x86** VC++ runtime from [Microsoft](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/).
- **Serum 1 / Serum 2 / DawDreamer-based Sylenth1:** click **Prepare rendering environment**, let the initial download finish, and check the linked plugin file.

Check the actual synth version and **Save settings**. The private environment matches the app architecture. Windows DawDreamer uses x64 and cannot load x86 plugins. On Mac, Python, DawDreamer, and the plugin must have matching arm64 / x64 architecture, or the plugin must be Universal.

The rendering program and the linked plugin are separate fields. For Serum 1 with DawDreamer, choose Python as the program and the 64-bit `Serum_x64.dll` as the plugin. Other synth settings can still be saved when a synth is incomplete; its configuration status explains missing requirements. An empty DawDreamer program field can be filled from the app's prepared `Preview/renderers` environment. Custom paths and temporarily offline paths are retained.

**Sushi (local research):** manual configuration remains available, but the host is not bundled or selected automatically. See the [research notes](https://github.com/Reguluz/SoundPreviewer/blob/main/docs/headless-host-research.md). Public testing uses the private DawDreamer environment described above.

Loading checks normally run automatically. If startup sounds vary with the bundled Windows x86 Sylenth1 host, precise per-preset state readback can be used instead. Mismatched or unverifiable states are still rejected; always audition generated output. Vital native rendering has no startup-sound reference check and is not blocked for that reason.

Sylenth1 must provide a verifiable loaded state. An unchanged state or unsupported readback format means loading cannot be verified with that plugin. Use a matching Sylenth1 version or export a new individual FXP from it. Clearly incompatible loads are not retried repeatedly in the same queue.

See [concurrency and memory](rendering.md#parallel).

<a id="environment"></a>

## Prepare DawDreamer

Skip this section if you only use Vital native rendering or the bundled Windows Sylenth1 x86 host.

Open **Synths and settings → Preview generation → Prepare rendering environment**. You do not need to install Python or uv separately. The initial setup requires internet access; generation can then work offline. Serum 2 dependencies are included. Failed downloads or checks preserve the existing environment and cache for a later retry.

The package includes the uv download tool. Python, DawDreamer, and downloads are kept in **`Preview/renderers`** beside the app, without changing system PATH, Python registration, or your uv / Python configuration. This Windows x64 rendering path uses private C++ runtime files and needs no separate runtime installation.

Setup fills unconfigured rendering paths for discovered compatible plugins and repairs stale paths in its own private environment. Custom rendering choices and working older Python environments are retained. If a plugin is missing, scan and link it first. You may also prepare the environment before scanning; compatible confirmed plugins can be linked later. Preparation checks an empty host, does not install commercial plugins, and does not validate every preset.

After closing the app, you can move the whole program folder together with **Preview and Config**. The new private environment uses a relative manifest and is found again after moving. Check absolute paths to original presets, plugins, and manually configured external environments separately. See [advanced environment options](https://github.com/Reguluz/SoundPreviewer/blob/main/docs/rendering.md#可选-dawdreamer-宿主).

![Select and link a discovered synth version; Chinese interface](../images/discovered.png)

![Check the rendering mode, program, and linked plugin; Chinese interface](../images/renderers.png)

*Screenshots use an example library. The English interface has the same layout.*

After saving, run the [single-preset test](quick-start.md#test). For unavailable configurations or loading failures, see [troubleshooting](troubleshooting.md#rendering).

---

[Home](index.md) · [Quick start](quick-start.md) · [Installation](installation.md) · [Synth setup](synths.md) · [Generation and playback](rendering.md) · [Library](library.md) · [Data and backup](storage.md) · [Language and appearance](appearance.md) · [Troubleshooting](troubleshooting.md)

Applies to SoundPreviewer 1.0.1. Text and screenshots work offline; external download and feedback links require internet access.
