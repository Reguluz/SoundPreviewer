# Optional x86 VST2 Host

This separate process supports one standalone Sylenth1 FXP per request. It does
not support FXB banks, VST3, x64 plugins, audio recording, or batch requests.
It requires the x86 .NET 8 Desktop Runtime and Microsoft VC++ x86 runtime.

Build from the repository root using the official NuGet feed:

```powershell
& src/SoundPreviewer.Vst2Host/build.ps1
```

The script creates workspace-local package/cache directories. The output is
`artifacts/vst2host-x86`; deploy that entire directory, not just the executable.
For an already primed offline cache, add `-Offline`. The main application's
offline restore configuration and existing project outputs are not changed.

Run repeatable TEST ONLY checks without loading any plugin:

```powershell
& src/SoundPreviewer.Vst2Host/verify-self-test.ps1
```

The checks exercise the real VST.NET unmanaged audio buffers, standalone FXP
parsing, invalid source hashes/IDs/chunks, bank rejection, sample rounding,
MIDI note-off block boundaries, and float32 WAV byte budgets. They are not
evidence of a real instrument render. The script removes generic DOTNET_ROOT
only from its child process so a parent's x64 SDK path cannot mask x86 .NET.

The rendering protocol is `--request <RenderRequest.json> --result <RenderResult.json>`.
Each real request generates the configured low/middle/high notes with fresh
plugin instances, using the requested sample rate, MIDI velocity and BPM.
Outputs always require listening confirmation. Source SHA256, PE architecture,
FXP envelope and actual plugin ID are checked before program loading.

VST.NET remains separately replaceable DLLs under LGPL-2.1-only; see the
included third-party notices and licenses. No commercial instrument or preset
is included. Do not run against real plugins merely to execute the self-test.
