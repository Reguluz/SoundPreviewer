using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private static AppSettings CreateExecutionSettings(AppSettings saved, out IReadOnlyList<RenderReadiness> readiness)
    {
        var runtime = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved, LibraryJson.Options), LibraryJson.Options)!;
        // Readiness belongs to this execution, not to the user's saved backend choice.
        // The queue can still reuse valid previews before considering these checks.
        readiness = RenderReadinessService.Evaluate(runtime);
        return runtime;
    }
}
