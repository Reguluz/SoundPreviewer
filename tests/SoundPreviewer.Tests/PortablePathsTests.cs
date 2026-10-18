using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestPortablePathsAsync()
    {
        string originalDirectory = ApplicationPaths.DataDirectory;
        string directory = Fixture("application-paths");
        string application = Path.Combine(directory, "release-v1"), userData = Path.Combine(directory, "user-data");
        Equal(Path.Combine(application, "Preview"), ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "New installations keep database, previews and user metadata in Preview beside the application.");
        string legacy = Path.Combine(application, "local-data");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "library.db"), "TEST ONLY location fixture");
        Equal(legacy, ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "An existing portable library remains selected until explicitly changed.");
        ApplicationPaths.RememberDataDirectory(legacy, application);
        Equal(legacy, ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "A program-local locator remembers the chosen existing library.");
        Equal(0, Directory.GetFiles(application, "*.tmp").Length, "Remembered locations leave no partial metadata files.");
        ApplicationPaths.ConfigureDataDirectory(null, application, userData);
        string otherLibrary = Path.Combine(directory, "other-library");
        Directory.CreateDirectory(otherLibrary);
        File.WriteAllText(Path.Combine(otherLibrary, "library.db"), "TEST ONLY second location fixture");
        ApplicationPaths.RememberDataDirectory(otherLibrary, application);
        Equal(otherLibrary, ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "Another process can remember a different library for future launches.");
        Equal(legacy, ApplicationPaths.DataDirectory, "An already configured process keeps its original library when the shared locator changes.");
        Equal(Path.Combine(legacy, "Config", "preset-categories.json"), SoundPreviewer.Core.Services.PresetCategoryService.DefaultRulesPath, "Editable category rules stay with the open library after another process changes the locator.");
        ApplicationPaths.RememberDataDirectory(legacy, application);
        File.Delete(Path.Combine(legacy, "library.db"));
        Throws<DirectoryNotFoundException>(() => ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "An unavailable remembered library must not silently become an empty library.");
        Throws<DirectoryNotFoundException>(() => ApplicationPaths.RememberDataDirectory(legacy, application), "Only an existing library can replace the remembered location.");
        File.WriteAllText(Path.Combine(application, "library-location.json"), "{\"FormatVersion\":99,\"DataDirectory\":\"relative\"}");
        Throws<InvalidDataException>(() => ApplicationPaths.ResolveDefaultDataDirectory(application, userData), "Unknown location formats cannot silently fall back to a new library.");
        string portable = Path.Combine(directory, "portable"), movedPortable = Path.Combine(directory, "portable-moved");
        string portableData = Path.Combine(portable, "Preview");
        Directory.CreateDirectory(portableData);
        File.WriteAllText(Path.Combine(portableData, "library.db"), "TEST ONLY portable fixture");
        ApplicationPaths.RememberDataDirectory(portableData, portable);
        True(ApplicationPaths.IsWithin(portable, _fixtureRoot) && ApplicationPaths.IsWithin(movedPortable, _fixtureRoot), "Only temporary fixture directories may be moved.");
        Directory.Move(portable, movedPortable);
        Equal(Path.Combine(movedPortable, "Preview"), ApplicationPaths.ResolveDefaultDataDirectory(movedPortable, userData), "A relative program-local locator follows a moved portable folder.");
        string blockedApplication = Path.Combine(directory, "blocked-application"), fallbackUser = Path.Combine(directory, "fallback-user");
        Directory.CreateDirectory(Path.Combine(blockedApplication, "library-location.json"));
        Directory.CreateDirectory(Path.Combine(blockedApplication, "Preview"));
        File.WriteAllText(Path.Combine(blockedApplication, "Preview", "library.db"), "TEST ONLY old default library");
        Equal(Path.Combine(fallbackUser, "library-location.json"), ApplicationPaths.RememberDataDirectory(otherLibrary, blockedApplication, fallbackUser), "A non-writable program locator can fall back to a small user locator.");
        Equal(otherLibrary, ApplicationPaths.ResolveDefaultDataDirectory(blockedApplication, fallbackUser), "An explicit fallback selection takes precedence over an older default Preview library.");
        string recoveryApplication = Path.Combine(directory, "recovery-application"), recoveryUser = Path.Combine(directory, "recovery-user");
        Directory.CreateDirectory(recoveryApplication);
        string staleLocator = Path.Combine(recoveryApplication, "library-location.json");
        File.WriteAllText(staleLocator, System.Text.Json.JsonSerializer.Serialize(new { FormatVersion = 1, DataDirectory = Path.Combine(directory, "unavailable-old-library") }));
        File.SetLastWriteTimeUtc(staleLocator, DateTime.UtcNow.AddMinutes(-1));
        ApplicationPaths.RememberDataDirectory(otherLibrary, recoveryUser);
        Equal(otherLibrary, ApplicationPaths.ResolveDefaultDataDirectory(recoveryApplication, recoveryUser), "A newer explicit fallback recovers from a retained read-only program locator whose library disappeared.");
        File.WriteAllText(staleLocator, "{");
        File.SetLastWriteTimeUtc(staleLocator, DateTime.UtcNow.AddMinutes(-1));
        Equal(otherLibrary, ApplicationPaths.ResolveDefaultDataDirectory(recoveryApplication, recoveryUser), "A newer explicit fallback also recovers from an unreadable old program locator.");
        string legacyUser = Path.Combine(directory, "legacy-user");
        Directory.CreateDirectory(legacyUser);
        File.WriteAllText(Path.Combine(legacyUser, "library-location.json"), System.Text.Json.JsonSerializer.Serialize(new { FormatVersion = 1, DataDirectory = otherLibrary }));
        Equal(otherLibrary, ApplicationPaths.ResolveDefaultDataDirectory(Path.Combine(directory, "new-installation"), legacyUser), "Old user-directory locators remain readable for compatibility.");
        Equal(Path.Combine(directory, "Applications"), ApplicationPaths.ResolvePortableApplicationDirectory(Path.Combine(directory, "Applications", "SoundPreviewer.app", "Contents", "MacOS"), true), "A Mac app stores Preview beside the bundle, not inside Contents.");
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
