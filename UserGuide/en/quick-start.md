# Quick start

[简体中文（默认）](../quick-start.md) · **English**

On this page:

- [1. Open the app](#launch)
- [2. Minimum setup: configure one synth](#configure)
- [3. Add and scan a preset folder](#scan)
- [4. Test one preset](#test)
- [5. Filter, generate, and audition](#use)
- [Feature Q&A](#faq)

The goal is to generate and audition one preset before processing a larger library. Prepare one supported, licensed synth that opens normally, and a preset for it. Commercial plugins and private preset libraries are not included.

<a id="launch"></a>

## 1. Open the app

Download the matching platform package from the project releases. On Windows, install it or **fully extract** the portable ZIP, then open `SoundPreviewer.exe`. On Mac, copy the `.app` to a separate writable folder before opening it. First launch creates necessary defaults; there is no need to copy Preview or Config beforehand. See [installation](installation.md).

<a id="configure"></a>

## 2. Minimum setup: configure one synth

1. Click **Scan synths** in the toolbar.
2. Open **Synths and settings → Discovered synths**, select the correct version, and click **Use selected version**. If it is missing, add its installation folder under **Folders** and scan again.
3. Open **Preview generation**, configure one rendering path below, check the linked plugin, and click **Save settings**. Other synths can remain unconfigured.

| Your synth | Minimum preparation |
| --- | --- |
| Vital | Select the **standalone program** and Vital native mode; test an individual `.vital` first |
| Serum 1 / Serum 2 | Link the corresponding VST2 / VST3 plugin, click **Prepare rendering environment**, finish the first online setup, then save |
| Windows Sylenth1 x86 | Link the x86 plugin and use the bundled host; install the x86 runtime if prompted |

See [synth support](synths.md#support) for other architectures and format limits. Keep the default preview template, automatic concurrency, and 4 GiB memory budget to start.

![Preview generation settings, shown in Chinese](../images/renderers.png)

*The screenshot uses a test library and example values. The English interface has the same layout.*

<a id="scan"></a>

## 3. Add and scan a preset folder

Under **Synths and settings → Folders**, add a preset folder. Choose automatic detection for mixed synth collections, then click **Scan presets**. Return to Library and find one supported preset marked **Pending**.

If it is **Unavailable**, read the reason in the details panel. FXB banks and Spire native formats cannot generate directly. Start with an individual `.vital`, Serum 1 `.fxp`, Serum 2 `.SerumPreset`, or Sylenth1 `.fxp`.

<a id="test"></a>

## 4. Test one preset

Select it and click **Regenerate this preset** in the details panel. When its status becomes **Generated**, audition **Mid / High / Low** and compare with the sound obtained by opening the original preset in the synth. If confirmation is requested, click **Confirm this preview matches the preset**.

**Pass criteria:** the expected sound is audible, all three ranges match the target, and no loading failure is reported. If it fails, follow [troubleshooting](troubleshooting.md), then retry this single preset.

<a id="use"></a>

## 5. Filter, generate, and audition

Search or select filters, then click **Generate / Resume**. Only the **current search and filter results** are processed; valid previews are skipped. Clear filters before processing the whole library. **Stop** retains finished previews so you can resume later.

Select a preset with a preview and press **Space** to play from the beginning; press it again to restart. Space still types normally in text fields. See [library organization](library.md) for favorites and metadata, and [generation and playback](rendering.md) for templates and progress.

After use, close the app and back up Preview, Config, categories.json, tags.json, and custom language files beside the app. See [backup and upgrades](storage.md#backup).

<a id="faq"></a>

## Feature Q&A

| Question | Answer and location |
| --- | --- |
| Can I choose preview pitches? | **Yes.** Under **Synths and settings → Playback and storage**, enter Low, Mid, and High notes such as `C2`, `C4`, `C6`, `C#4`, or `Db4`. Save for new previews; use **Regenerate this preset** to apply the template to an existing preview. The player can audition each range separately. See [templates](rendering.md#template). |
| Can I change preview duration? | **Yes.** Quick preview supports 0.1–4 seconds per range. Turn it off to set key-hold and tail durations. Vital native rendering has specific parameter limits; see [templates](rendering.md#template). |
| Can I create custom tags? | **Yes.** Select a preset, open **Edit tags and categories → Add tag**, enter its name and optional aliases, select it, and save. New presets have no tags by default. Rescanning preserves manual selections. See [metadata](library.md#metadata). |
| Can I create categories and favorites? | **Yes.** Add or rename categories and aliases in the same editor; use the details controls for favorites and like / dislike feedback. **Restore automatic** restores automatic metadata. See [library organization](library.md#metadata). |
| Can I combine filters and select multiple tags? | **Yes.** Click multiple options directly, without Ctrl / Shift. Options within one group use OR; separate groups use AND. Generation uses the current search and filter results. See [filters](library.md#filters). |
| Can I stop and resume? Are duplicates rendered again? | **Yes, you can resume.** Stop retains finished previews; Generate / Resume skips valid previews. Identical preset content shares a valid cache. See [progress](rendering.md#progress). |
| Can I play without a DAW, the synth, or an internet connection? | **Existing valid previews play locally.** New generation still needs the synth and its license. Rendering environment preparation requires internet access the first time. See [installation](installation.md) and [environment setup](synths.md#environment). |
| Can I choose WAV / AAC or compress old previews? | **Yes.** Choose the save format under Playback and storage, or compress existing WAV files. Changing the format does not force valid previews to regenerate. See [compression](storage.md#compression). |
| Can I choose English and a separate tag language? | **Yes.** Simplified Chinese and English are built in. Interface and metadata language can be selected independently, and language JSON files can be added. See [language](appearance.md#language). |
| Can I directly generate Spire or FXB bank previews? | **Not yet.** They can be indexed; export individual supported presets from FXB banks first. Eligible entries inside Vital `.vitalbank` files can generate. See [format support](synths.md#support). |
| Can I load a preset into a DAW track with one click? | **Automatic track creation and loading are not available yet.** Copy the file path or locate the preset, then load it in the appropriate synth inside your DAW. See [DAW file paths](library.md#daw). |

For errors, missing synths, or incorrect generated sounds, continue to [troubleshooting](troubleshooting.md).

---

[Home](index.md) · [Quick start](quick-start.md) · [Installation](installation.md) · [Synth setup](synths.md) · [Generation and playback](rendering.md) · [Library](library.md) · [Data and backup](storage.md) · [Language and appearance](appearance.md) · [Troubleshooting](troubleshooting.md)

Applies to SoundPreviewer 1.0.1. Text and screenshots work offline; external download and feedback links require internet access.
