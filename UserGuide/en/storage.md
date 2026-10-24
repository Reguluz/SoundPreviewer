# Data, backup, and upgrades

[简体中文（默认）](../storage.md) · **English**

On this page:

- [Data locations](#locations)
- [Compression and old previews](#compression)
- [Missing folders and recovery](#recovery)
- [Backup, cleanup, and legacy import](#import)
- [Upgrading and moving](#backup)

<a id="locations"></a>

## Data locations

The preset index, generated audio, settings, favorites, tags, and categories are kept in **Preview beside the application**, for both installed and portable editions. On Windows it is beside `SoundPreviewer.exe`; on Mac it is beside the `.app`, outside the bundle. An unwritable program folder produces an explicit error; move the app to a separate writable location.

Preview and startup-reference file links are stored relative to Preview, for example `preview.m4a` or `InitializationBaselines/reference.wav`. Close the app before moving the program, Preview, and Config together. Audio is then read from the new location, and new audio is also written there. Upgrades compatibly convert old absolute references while preserving audio and personal records.

<a id="compression"></a>

## Compression and old previews

New previews default to **AAC-LC 128 kbps stereo (`.m4a`)**, retaining the template's **44100 / 48000 Hz** sample rate. Under **Synths and settings → Playback and storage**, choose WAV for lossless new files and save settings.

Existing WAV previews remain readable and reusable. Changing the format does not force regeneration. **Compress existing WAV** groups shared file references, converts audio, verifies sample rate and note-range lengths, and updates every reference. Only after success does it remove an old WAV confirmed to belong to this library. Favorites, tags, categories, confirmation status, and source presets are retained. After failure or stopping, successful conversions remain; other originals stay usable. External, shared, or uncertain-ownership files are not deleted.

Playback, Space replay, waveforms, and note-range seeking support both AAC and WAV. Compression uses Windows / macOS system codecs, with no Python, FFmpeg, or separate audio tool required. AAC frame-padding information stays in the M4A file without a sidecar. Loading checks still use original lossless audio, and startup references remain WAV.

Current versions read older WAV files. If you roll back to a version supporting only WAV, restore your pre-upgrade Preview backup.

<a id="recovery"></a>

## Missing folders and recovery

| Folders at startup | Behavior |
| --- | --- |
| Neither Preview nor Config exists | Creates an empty library and defaults when no legacy source is available; add folders, scan, and generate again |
| Only Preview is missing | Retains source configuration in Config; rescan and regenerate previews |
| Only Config is missing | Continues using Preview records and audio; rebuilds directory settings from the library, using compatible old category rules when available or defaults otherwise |
| Both exist | Continues using existing records, configuration, and valid caches without replacing personal files with defaults |

If an automatically located old library no longer exists, the app explains this and creates Preview beside the current program. A missing manually selected import source still produces an error. Missing-folder recovery does not overwrite damaged existing configuration. Presets can be rescanned and previews regenerated, but favorites, tags, feedback, and manual categories require a backup. Close the app, back up the current folder, and restore the old Preview manually when these records are needed. Preserve Config or old rules copies for personal category rules.

Early versions may have used an old user library and external audio folders. Migration first copies and verifies the database, personal records, rules, and referenced audio before using them beside the app. Original databases and audio remain as rollback backups. An existing Preview keeps its records; only referenced external audio / startup references are brought inside it, without replacing it with another old library. New audio is written to the current Preview.

<a id="import"></a>

## Backup, cleanup, and legacy import

| Task | Action and result |
| --- | --- |
| Check the actual data location | Under Playback and storage, inspect database / audio locations or open the Preview folder |
| Free preview space | Clear previews; preset records, favorites, feedback, tags, and categories remain for later regeneration |
| Import a separate legacy library | Choose **Import legacy library**, select a folder containing `library.db`, and confirm. The selected library is copied and verified before the current complete Preview is backed up and replaced; records are not merged |
| Choose an output location | Data is fixed in Preview beside the current app; arbitrary external audio output folders are no longer offered |
| Back up | Close the app and copy all of Preview and Config; preserve original presets and external resources |

Cleanup only removes previews and references whose ownership can be confirmed. Shared, external, and uncertain files are retained with a message, so clearing previews does not necessarily empty the folder. Check migrated records and playback before disposing of any old directory yourself.

Manual import first copies the selected database and audio into staging and checks readability. It then atomically renames the complete current Preview to `Preview-before-import-*` beside the app before switching. On failure, it attempts to restore the original name if Preview is absent, and reports the backup location. Preserve that backup.

Older backups may contain absolute Preview paths. Close the app, preserve the current Preview, and restore the desired backup under the name Preview without overwriting existing data. Missing historical audio keeps source / cache information, while active references belong under the current Preview so audio can later be recovered or regenerated.

<a id="backup"></a>

## Upgrading and moving

Close the app first. Prefer upgrading in the same folder: run the new Windows installer, replace program files for Portable, or replace the Mac `.app`. **Preserve Preview, Config, categories.json, tags.json, and custom language files** to retain personal records, folders, and valid caches. This preserves data; it is not a prerequisite for first launch. Packages do not contain personal directory configuration, and upgrades / uninstall preserve existing files. After importing another library, the current `Config/directories.json` remains the directory configuration. Keep legacy root JSON for compatible migration and rollback. Use pre-upgrade backups when returning to an older app.

Portable means no installer is required. Move the app together with its data and custom files after closing and backing up. The prepared private environment moves with the folder; absolute paths to source presets, plugins, and manually selected external Python environments need checking. There is no in-app automatic update; obtain later versions from GitHub.

For a clean folder, retain the old installation as a backup, fully extract a new package separately, then copy Preview, Config, categories.json, tags.json, custom language files, and any unmigrated root JSON. Verify records, playback, and plugin paths before handling the old folder.

[Installation](installation.md) · [Environment relocation](synths.md#environment) · [Metadata and language files](appearance.md#catalogs)

---

[Home](index.md) · [Quick start](quick-start.md) · [Installation](installation.md) · [Synth setup](synths.md) · [Generation and playback](rendering.md) · [Library](library.md) · [Data and backup](storage.md) · [Language and appearance](appearance.md) · [Troubleshooting](troubleshooting.md)

Applies to SoundPreviewer 1.0.1. Text and screenshots work offline; external download and feedback links require internet access.
