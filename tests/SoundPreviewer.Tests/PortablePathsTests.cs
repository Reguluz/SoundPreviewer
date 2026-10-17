using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestPortablePathsAsync()
    {
        ApplicationPaths.ConfigureDataDirectory(null);
        Equal(Path.Combine(AppContext.BaseDirectory, "local-data"), ApplicationPaths.DataDirectory, "Default data stays beside the executable.");
        ApplicationPaths.ConfigureDataDirectory("portable-library");
        Equal(Path.Combine(AppContext.BaseDirectory, "portable-library"), ApplicationPaths.DataDirectory, "Relative overrides never depend on shell working directory.");
        True(ApplicationPaths.IsWithin(ApplicationPaths.DataDirectory, AppContext.BaseDirectory), "Generated data is inside application directory.");
        True(!ApplicationPaths.IsWithin(Path.Combine(AppContext.BaseDirectory, "..", "outside"), ApplicationPaths.DataDirectory), "Sibling traversal is not within the output root.");
        True(!ApplicationPaths.IsWithin(ApplicationPaths.DataDirectory + "-sibling", ApplicationPaths.DataDirectory), "A prefix alone is not containment.");
        string first = Path.Combine(_fixtureRoot, "UPPER.vital"), second = Path.Combine(_fixtureRoot, "upper.vital");
        bool same = Fingerprints.PresetId(SynthKind.Vital, first) == Fingerprints.PresetId(SynthKind.Vital, second);
        Equal(OperatingSystem.IsWindows(), same, "Unix case-sensitive paths keep independent identities while Windows IDs stay compatible.");
        True(ApplicationPaths.SamePath(first, first), "A missing path still compares equal to itself.");
        Equal(OperatingSystem.IsWindows(), ApplicationPaths.SamePath(first, second), "Missing distinct Unix paths are never folded together.");
        True(!ApplicationPaths.SamePath("", ""), "Unset plugin paths do not accidentally match.");
        True(Path.GetFileName(ApplicationPaths.WorkerPath) is "SoundPreviewer.Worker" or "SoundPreviewer.Worker.exe" or "SoundPreviewer.Worker.dll", "Worker discovery supports native apphosts and DLL fallback.");
        ApplicationPaths.ConfigureDataDirectory(null);
        return Task.CompletedTask;
    }
}
