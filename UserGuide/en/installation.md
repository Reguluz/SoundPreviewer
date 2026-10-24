# Installation

[简体中文（默认）](../installation.md) · **English**

Download the matching package from [GitHub Releases](https://github.com/Reguluz/SoundPreviewer/releases). If the version has no release package yet, open a successful run on the [build page](https://github.com/Reguluz/SoundPreviewer/actions/workflows/desktop.yml) and download `packages-platform` from Artifacts. Build artifacts expire and may require a GitHub login.

| Your computer | Package | How to open |
| --- | --- | --- |
| Windows 10 1809 or later / Windows 11, x64 | `win-x64-setup.exe`; portable `win-x64-portable.zip` | Open from Start after installation; fully extract the portable package before opening `SoundPreviewer.exe` |
| macOS 12 or later, Apple Silicon (M series) | `osx-arm64.dmg` or `osx-arm64.zip` | Copy `SoundPreviewer.app` into a separate writable folder, such as `~/Applications/SoundPreviewer` |
| macOS 12 or later, Intel | `osx-x64.dmg` or `osx-x64.zip` | Same as Apple Silicon; choose the x64 package |

Minimum OS versions come from package configuration and do not imply testing on every older system. Do not run the Mac app directly from a read-only DMG. Packages currently have no Windows code signature or Apple signing / notarization, so the OS may show an unknown-source prompt. Check the download source and the accompanying `SHA256SUMS-platform.txt`. Dedicated Linux and Windows ARM64 packages are not available.

The app includes the .NET runtime; normal use needs no development tools. **Supply your own synth plugins, presets, and licenses.** Commercial plugins and private preset libraries are not included. Generating new previews needs the corresponding synth installed; playing existing previews does not load the synth again.

**A fresh, fully extracted package can open without an old Preview or Config folder.** Necessary defaults are created automatically. Follow [quick start](quick-start.md) to add folders and generate previews. To retain favorites, tags, manual categories, and existing previews, preserve or restore the old folders; a new empty library cannot recover personal records by itself.

<a id="check"></a>

## Check after opening

Make sure the application folder is writable. If upgrading, check that Preview, Config, and personal JSON files remain available. For recovery, read [legacy library import](storage.md#import) before replacing any unique copy of your data.

Next: [configure one synth and test a preset](quick-start.md#configure).

---

[Home](index.md) · [Quick start](quick-start.md) · [Installation](installation.md) · [Synth setup](synths.md) · [Generation and playback](rendering.md) · [Library](library.md) · [Data and backup](storage.md) · [Language and appearance](appearance.md) · [Troubleshooting](troubleshooting.md)

Applies to SoundPreviewer 1.0.1. Text and screenshots work offline; external download and feedback links require internet access.
