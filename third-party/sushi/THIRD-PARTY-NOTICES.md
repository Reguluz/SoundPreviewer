# Sushi research build: sources and license inventory

This directory records a **local compatibility experiment**, not a cleared public distribution. Sushi is not included by default in SoundPreviewer's installation packages or public CI. No VST 2 SDK source, commercial plugin, preset or authorization file is included here.

## Direct dependencies

The source revisions below are pinned by Sushi commit `c6ddde6b6b6c06229118432c8c7f7a7c61a61408`. The local patch is in `patches/Sushi1.4/soundpreviewer-state-files.patch`; `scripts/Build-Sushi.ps1` reproduces the offline Windows x64 build from an externally supplied SDK and libsndfile development package.

| Component | Source revision | Upstream license record |
| --- | --- | --- |
| [Sushi](https://github.com/elk-audio/sushi) | `c6ddde6b6b6c06229118432c8c7f7a7c61a61408` | `licenses/Sushi-LICENSE.md`, `licenses/AGPL-3.0.txt` |
| [Twine](https://github.com/elk-audio/twine) | `4a36c54419f05e74670a1afba53654c2dd0d81bc` | GPL v3 or later in source headers; `licenses/Twine-COPYING.txt` |
| [Elk warning suppressor](https://github.com/elk-audio/elk-warning-suppressor) | `daaf1b6b0ae74906188e89487ebbd065b1c55dcf` | Header says AGPL v3 or later, bundled COPYING is GPL v3; both facts retained, not resolved here |
| [Elklog](https://github.com/elk-audio/elklog) | `744f866ae9c442aaccb383335a067d824516da57` | `licenses/Elklog-LICENSE.txt` |
| [spdlog](https://github.com/gabime/spdlog) | `79524ddd08a4ec981b7fea76afd08ee05f83755d` | MIT; `licenses/Spdlog-LICENSE.txt`; bundled fmt notice in `licenses/Fmt-LICENSE.rst` |
| [RapidJSON](https://github.com/Tencent/rapidjson) | `ab1842a2dae061284c0a62dca1cc6d5e7e37e346` | `licenses/RapidJSON-LICENSE.txt`, including its third-party notices |
| [oscpack](https://github.com/ENTTEC/oscpack) | `eb4abfc0692769acddc5afca1b8aa1be45f0a50e` | MIT; `licenses/Oscpack-LICENSE.txt` |
| [Brickworks](https://github.com/Orastron/brickworks) | `5269206236f0cb9d95703d26c007573dca119af7` | GPL v3; `licenses/Brickworks-LICENSE.txt`, `licenses/Brickworks-README.md` |
| [Freeverb](https://github.com/elk-audio/freeverb) | `e2b84a69f9e0cb5d7449a6ab03d2bea3daa57b07` | Upstream public-domain declaration in `licenses/Freeverb-README.md` |
| Sushi's bundled FIFO | Sushi source revision above | Upstream public-domain header in `licenses/FIFO-NOTICE.txt` |
| [Elk plugin extensions](https://github.com/elk-audio/elk-plugin-extensions) | `23edab4c38813bb6966304d613fa4f32fb866030` | Copyright and README retained; no separate license file found in this revision |
| [VST 3 SDK](https://github.com/steinbergmedia/vst3sdk) | `1d64ea0a33971ca5149b647bf46103150f2d961b` | MIT notices in `licenses/VST3*-LICENSE.txt`; no VSTGUI or example plugins are shipped by the research script |
| [libsndfile](https://github.com/libsndfile/libsndfile/releases/tag/1.2.2) | Official `libsndfile-1.2.2-win64.zip` | LGPL 2.1 text from the matching source tag in `licenses/libsndfile-COPYING.txt` |
| Steinberg VST 2.4 SDK | External build input only | Not fetched by the build script, vendored, licensed here, or copied into output |

VST 3 submodules used by this build are `base` (`3d2e82f8e6bff59c1d8b7a27491a29c2286b5206`), `cmake` (`de6e54eeaaab35b7145f5c32c279b5e892146e04`), `pluginterfaces` (`31d6eeba6daaa3e2a8bfbe3e7a90ca0b7fbfbc1c`) and `public.sdk` (`a3911a4615dabbfdfd9d181ee26b05c70c289a95`). Sushi's own `third-party/vst3sdk.windows.patch` applies its MSVC C++20 compatibility correction during configuration.

## Redistribution remains unresolved

Sushi's additional license text explicitly addresses plugins loaded into the host and applications communicating with it. Its special exception requires specified source availability. The official [Elk license explanation](https://elk-audio.github.io/elk-docs/html/intro/license.html) and [pinned Sushi LICENSE.md](https://github.com/elk-audio/sushi/blob/c6ddde6b6b6c06229118432c8c7f7a7c61a61408/LICENSE.md) must be considered before a public build using proprietary plugins. Technical loading success does not establish that permission.

The official libsndfile Windows archive contains no COPYING file. The retained license text was obtained separately from the official `1.2.2` source. Its CMake package reports external codec and MPEG support enabled; their embedded dependency versions and notices have not been fully inventoried. This direct-dependency inventory is consequently **not a complete release clearance or final redistribution notice**.

Recorded external binary hashes:

- Official libsndfile ZIP SHA-256: `2173935c0c1ed13cf627951d34483f9d405ead2eb473190461c42ba220643a3f`.
- Research `sndfile.dll` SHA-256: `4e3bd2de8e1485110eaebef8e1239471f73d608773831c323bf528e05645655e`.
- Retained libsndfile COPYING SHA-256: `ad01ea5cd2755f6048383c8d54c88459cd6fcb17757c5c8892f8c5ea060f6140`.

Sushi and the VST SDK names are used for identification. Their owners do not endorse SoundPreviewer. Retaining these notices does not replace the applicable source-availability, license or other obligations.
