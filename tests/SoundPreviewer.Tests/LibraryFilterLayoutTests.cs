using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestLibraryFilterLayoutAsync()
    {
        string[] defaults = ["status", "category", "tag", "synth"];
        foreach (string json in new[] { "{}", "{\"LibraryFilterOrder\":null}", "{\"LibraryFilterOrder\":[]}", "{\"LibraryFilterOrder\":[\"unknown\"]}" })
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, LibraryJson.Options)!;
            True(settings.LibraryFilterOrder.SequenceEqual(defaults), "Old or invalid layouts use the four default groups.");
        }
        var custom = JsonSerializer.Deserialize<AppSettings>("{\"LibraryFilterOrder\":[\"tag\",\"synth\",\"unknown\",\"tag\",\"source\"],\"FutureSetting\":42}", LibraryJson.Options)!;
        string[] expected = ["tag", "synth", "source"];
        True(custom.LibraryFilterOrder.SequenceEqual(expected), "Custom order and hidden groups survive normalization without duplicates.");
        var directory = Path.Combine(_fixtureRoot, "filter-layout");
        Directory.CreateDirectory(directory);
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.Initialize();
        store.SaveSettings(custom);
        var loaded = store.LoadSettings();
        True(loaded.LibraryFilterOrder.SequenceEqual(expected), "The visible order persists across library reloads.");
        Equal(42, loaded.AdditionalProperties!["FutureSetting"].GetInt32(), "Saving layout preserves unknown settings.");
        return Task.CompletedTask;
    }
}
