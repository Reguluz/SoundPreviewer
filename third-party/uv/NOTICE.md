# uv private bootstrap tool

SoundPreviewer packages only the matching `uv` executable from the official
[Astral uv 0.13.4 release](https://github.com/astral-sh/uv/releases/tag/0.13.4).
The executable is unmodified. `uvx` and `uvw` are not included.

uv is available under the Apache 2.0 or MIT license. Both upstream license texts
are retained beside this notice. Upstream source: https://github.com/astral-sh/uv.

The package does not include a Python installation, DawDreamer, commercial audio
plugins, presets, or user data. Preparing the optional renderer downloads a
private CPython 3.12.15 distribution using uv's pinned download catalog and pinned
binary Python packages from PyPI. Their license notices remain in that private
installation. Existing libraries and preview audio do not require this download.

The bootstrap does not install uv globally, alter PATH or register Python.
Runtime files, downloads and caches belong to the application's Preview folder.
