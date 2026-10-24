# Language and appearance

[简体中文（默认）](../appearance.md) · **English**

On this page:

- [Interface and metadata language](#language)
- [Add an interface language](#translations)
- [Category and tag JSON](#catalogs)
- [Themes and colors](#colors)

<a id="language"></a>

## Interface and metadata language

Under **Synths and settings → Playback and storage**, choose interface language and category / tag language. Metadata can follow the interface or use a separate language. Changes apply immediately and are saved. Preset names, paths, and custom content retain their original values; switching language does not regenerate previews or change favorite / filter identities.

<a id="translations"></a>

## Add an interface language

Built-in resources are **`language/zh-CN.json`** and **`language/en-US.json`**. Copy one to `language/language-id.json`, set the first `languageName` property to its display name, and translate values in `strings`. Preserve keys and placeholders such as `{1}`. Added files appear in the selector after restarting. On Windows the folder is beside the EXE; on Mac it is inside `.app/Contents/MacOS/language`.

<a id="catalogs"></a>

## Category and tag JSON

Editable **`categories.json`** and **`tags.json`** are beside the program, or beside the `.app` on Mac. Close the app before editing `Name`, `Aliases`, and `Languages` inside `Items`. For example, `"Languages":{"en-US":{"Name":"Arp","Aliases":[]}}` supplies English text. Without a matching translation, the default name and aliases are used. Preserve `CatalogId` and each item's `Id`; renaming, aliases, and translations retain matching through these IDs.

Opening a legacy library adds missing IDs after saving a database backup to `Preview/Backups/before-metadata-ids.db`. Catalog snapshots in the database can restore missing root files. Import remaps IDs by catalog identity to avoid conflicts between libraries. Invalid existing JSON is reported and preserved. Restart after manual edits. On upgrade or relocation, retain **Preview, Config, categories.json, tags.json**, and added language files.

<a id="colors"></a>

## Themes and colors

Click **Colors** at the top-right. Choose Light, Dark, or System mode and a Silver teal, Moss green, Mist purple, or Ocean blue preset. Use HEX inputs and color controls to change accent, background, panels, and text. Changes preview immediately. Apply saves them; canceling or closing restores the original appearance. Restoring defaults previews Silver teal in Light mode and still requires Apply to save. Existing choices are retained.

Appearance is stored with your library in Preview. Preserve it when updating or moving; changing colors does not alter preset records, tags, or audio.

![Interface and metadata language selectors; Chinese interface](../images/storage.png)

![Colors dialog; Apply saves and Cancel restores](../images/appearance.png)

*Screenshots use example settings and the Chinese interface. The English interface has the same layout.*

For everyday metadata editing, see [library organization](library.md#metadata). Before moving, follow the [backup checklist](storage.md#backup).

---

[Home](index.md) · [Quick start](quick-start.md) · [Installation](installation.md) · [Synth setup](synths.md) · [Generation and playback](rendering.md) · [Library](library.md) · [Data and backup](storage.md) · [Language and appearance](appearance.md) · [Troubleshooting](troubleshooting.md)

Applies to SoundPreviewer 1.0.1. Text and screenshots work offline; external download and feedback links require internet access.
