using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestPortablePathsAsync()
    {
        string originalDirectory = ApplicationPaths.DataDirectory;
        string directory = Fixture("application-paths");
        string application = Path.Combine(directory, "release-v1"), userData = Path.Combine(directory, "user-data");
        Equal(userData, ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "New installations keep user data outside replaceable application files.");
        string legacy = Path.Combine(application, "local-data");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "library.db"), "TEST ONLY location fixture");
        Equal(legacy, ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "An existing portable library remains selected until explicitly changed.");
        ApplicationPaths.RememberDataDirectory(legacy, userData);
        Equal(legacy, ApplicationPaths.ResolveDefaultDataDirectory(Path.Combine(directory, "release-v2"), userData), "A remembered portable library survives an installation-directory change.");
        Equal(0, Directory.GetFiles(userData, "*.tmp").Length, "Remembered locations leave no partial metadata files.");
        ApplicationPaths.ConfigureDataDirectory(null, application, userData);
        string otherLibrary = Path.Combine(directory, "other-library");
        Directory.CreateDirectory(otherLibrary);
        File.WriteAllText(Path.Combine(otherLibrary, "library.db"), "TEST ONLY second location fixture");
        ApplicationPaths.RememberDataDirectory(otherLibrary, userData);
        Equal(otherLibrary, ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "Another process can remember a different library for future launches.");
        Equal(legacy, ApplicationPaths.DataDirectory, "An already configured process keeps its original library when the shared locator changes.");
        Equal(Path.Combine(legacy, "Config", "preset-categories.json"), SoundPreviewer.Core.Services.PresetCategoryService.DefaultRulesPath, "Editable category rules stay with the open library after another process changes the locator.");
        ApplicationPaths.RememberDataDirectory(legacy, userData);
        File.Delete(Path.Combine(legacy, "library.db"));
        Throws<DirectoryNotFoundException>(() => ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "An unavailable remembered library must not silently become an empty library.");
        Throws<DirectoryNotFoundException>(() => ApplicationPaths.RememberDataDirectory(legacy, userData), "Only an existing library can replace the remembered location.");
        File.WriteAllText(Path.Combine(userData, "library-location.json"), "{\"FormatVersion\":99,\"DataDirectory\":\"relative\"}");
        Throws<InvalidDataException>(() => ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "Unknown location formats cannot silently fall back to a new library.");
        ApplicationPaths.ConfigureDataDirectory("portable-library");
        Equal(Path.Combine(AppContext.BaseDirectory, "portable-library"), ApplicationPaths.DataDirectory, "Relative overrides never depend on shell working directory.");
        True(ApplicationPaths.IsWithin(ApplicationPaths.DataDirectory, AppContext.BaseDirectory), "An explicit relative library is still based on the application directory.");
        True(!ApplicationPaths.IsWithin(Path.Combine(AppContext.BaseDirectory, "..", "outside"), ApplicationPaths.DataDirectory), "Sibling traversal is not within the output root.");
        True(!ApplicationPaths.IsWithin(ApplicationPaths.DataDirectory + "-sibling", ApplicationPaths.DataDirectory), "A prefix alone is not containment.");
        string first = Path.Combine(_fixtureRoot, "UPPER.vital"), second = Path.Combine(_fixtureRoot, "upper.vital");
        bool same = Fingerprints.PresetId(SynthKind.Vital, first) == Fingerprints.PresetId(SynthKind.Vital, second);
        Equal(OperatingSystem.IsWindows(), same, "Unix case-sensitive paths keep independent identities while Windows IDs stay compatible.");
        True(ApplicationPaths.SamePath(first, first), "A missing path still compares equal to itself.");
        Equal(OperatingSystem.IsWindows(), ApplicationPaths.SamePath(first, second), "Missing distinct Unix paths are never folded together.");
        True(!ApplicationPaths.SamePath("", ""), "Unset plugin paths do not accidentally match.");
        True(Path.GetFileName(ApplicationPaths.WorkerPath) is "SoundPreviewer.Worker" or "SoundPreviewer.Worker.exe" or "SoundPreviewer.Worker.dll", "Worker discovery supports native apphosts and DLL fallback.");
        ApplicationPaths.ConfigureDataDirectory(originalDirectory);
        return Task.CompletedTask;
    }
}
