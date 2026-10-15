# Third-Party Components

This optional executable dynamically links unmodified VST.NET2-Host 2.1.0 by
Marc Jacobi / Jacobi Software, Copyright 2008-2022, licensed LGPL-2.1-only.

- Package: https://www.nuget.org/packages/VST.NET2-Host/2.1.0
- Source: https://github.com/obiwanjacobi/vst.net
- Reference source snapshot: b4928c5ee9c2a1c619c519ef0006be9e3101fc48
- Official HostSample: Source/Samples/Jacobi.Vst.Samples.Host
- License: LICENSE.VST.NET.txt, distributed beside this executable.

The Jacobi assemblies remain separate, replaceable DLLs. They are not modified,
merged, statically linked, trimmed, or embedded. This repository includes the
host source and build procedure so the executable can be rebuilt and relinked.
No Steinberg SDK source, commercial plugin binary, preset, or license data is
distributed with this host. VST is a trademark of Steinberg Media Technologies.

## Microsoft Runtime Components

The unmodified x86 Ijwhost.dll supplied by the VST.NET2-Host 2.1.0 package is
.NET runtime 6.0.8, source commit 55fb7ef977e7d120dc12f0960edcff0739d7ee0e,
licensed MIT by the .NET Foundation and Contributors. Its source is available
at https://github.com/dotnet/runtime. See LICENSE.Microsoft.txt.

The executable is framework-dependent and requires the installed x86 .NET 8
Desktop Runtime. Framework assemblies, including System.Configuration.ConfigurationManager,
are resolved from that installed framework, not copied as independent packages.
No System.Configuration.ConfigurationManager NuGet package is redistributed.

VST.NET2-Host declares Microsoft.Extensions.Configuration.Json 6.0.0 and its
transitive dependencies in its restore graph. This executable excludes the
package's automatic build and runtime assets and explicitly deploys only the
two Jacobi assemblies and Ijwhost.dll. Configuration packages are not deployed
or bundled; the native VST2 path and the real Sylenth1 smoke test do not use
the managed-plugin configuration loader. Any future use of that loader must
explicitly resolve and document its additional dependencies.
