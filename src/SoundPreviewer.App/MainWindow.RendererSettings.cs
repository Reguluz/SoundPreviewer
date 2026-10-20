using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private static AppSettings CreateExecutionSettings(AppSettings saved, Action<RenderReadiness>? unavailable = null)
    {
        var runtime = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved, LibraryJson.Options), LibraryJson.Options)!;
        foreach (var state in RenderReadinessService.Evaluate(runtime))
        {
            var plugin = runtime.Plugins.First(item => item.Synth == state.Synth);
            if (plugin.Backend == RenderBackend.Unconfigured || state.CanAttemptRender) continue;
            unavailable?.Invoke(state);
            // Only this execution copy is disabled. Stored choices, offline paths and valid
            // existing previews remain available for setup and normal queue cache reuse.
            plugin.Backend = RenderBackend.Unconfigured;
        }
        return runtime;
    }
}
