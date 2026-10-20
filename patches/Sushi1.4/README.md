# SoundPreviewer Sushi compatibility patch

Base: upstream Sushi `c6ddde6b6b6c06229118432c8c7f7a7c61a61408` (1.4.0 source). The patch is retained for local research, not bundled into the application as a cleared public backend.

`soundpreviewer-state-files.patch` contains:

- File-based initial state input: a single-program `CcnK` / `FPCh` FXP chunk or a raw binary plugin state.
- Optional state dump after applying the initial state, for compatibility diagnostics.
- The initial-state schema's actual `processor` property and the new file fields.
- Timed MIDI `control_change` events (controller and value 0–127 on channel 1).
- A VST2 flag check that does not modify the plugin's flags.
- The optional LV2 build guard and Windows oscpack system-library links.
- The complete newly added `src/engine/preview_state_probe.h`; it must not be omitted because it was untracked in the prototype checkout.
- A Windows process manifest requesting UTF-8, a startup code-page check, and Unicode VST2 library paths using `u8path` and `LoadLibraryW`. The new `apps/soundpreviewer-utf8.manifest` is included in the patch.

Sushi itself applies the pinned upstream `third-party/vst3sdk.windows.patch` to the VST 3 `pluginterfaces` submodule. That SDK compatibility patch is already in the pinned upstream source and is not duplicated here.

Use `scripts/Build-Sushi.ps1` for a checked, idempotent application of the patch. The script never resets an existing checkout or downloads the VST 2 SDK. It requires matching source and refuses unexpected top-level modifications. A state dump can differ from the input chunk because a plugin may normalize or augment its state; a successful round trip alone is not proof of exact preset equivalence.

The experimental host requires Windows 10 1903 or later so its UTF-8 process manifest is supported. It fails before parsing paths when the effective ANSI code page is not UTF-8. This does not change system-wide encoding settings or SoundPreviewer's existing Windows 10 1809 minimum.
