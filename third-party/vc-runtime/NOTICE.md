# Microsoft C++ application-local runtime

The Windows package includes the unmodified, Microsoft-signed x64 release files
`msvcp140.dll`, `vcruntime140.dll` and `vcruntime140_1.dll` from the Visual Studio
C++ Redistributable directory. They are Microsoft software, not open-source
components of SoundPreviewer. Their embedded copyright and signature are retained.

Sources and applicable terms:

- https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
- https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/
- https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution

These files are deployed locally for the optional DawDreamer renderer. Setup
copies them only into the private Python runtime when needed and never replaces
Windows, another Python installation, or another application's DLLs. The bundled
manifest records their versions and SHA-256 hashes. Future application packages
are responsible for servicing this local copy.
