using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Vst2Host;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--self-test"])
        {
            try
            {
                if (Environment.Is64BitProcess) throw new PlatformNotSupportedException("Expected an x86 process.");
                HostSelfTest.Run();
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        string? requestPath = null, resultPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--request" && i + 1 < args.Length) requestPath = args[++i];
            else if (args[i] == "--result" && i + 1 < args.Length) resultPath = args[++i];
        }
        if (requestPath is null || resultPath is null) { Console.Error.WriteLine("Usage: SoundPreviewer.Vst2Host.exe --request <json> --result <json>"); return 2; }
        RenderResult result;
        try
        {
            if (Environment.Is64BitProcess) throw new PlatformNotSupportedException("VST2 宿主必须以 x86 进程运行。");
            if (new FileInfo(requestPath).Length > 1024 * 1024) throw new InvalidDataException("请求超过 1 MiB 限制。");
            var request = JsonSerializer.Deserialize<RenderRequest>(File.ReadAllText(requestPath), LibraryJson.Options) ?? throw new InvalidDataException("请求为空。");
            request.Template.Validate();
            FxpPreset? preset;
            if (request.IsInitializationBaseline)
            {
                if (request.Preset.Synth != SynthKind.Sylenth1 || request.Plugin.Synth != SynthKind.Sylenth1 ||
                    request.Preset.SourcePath.Length > 0 || request.Preset.ContentHash.Length > 0 ||
                    request.Preset.InternalPath.Length > 0 || request.Preset.BankSlot is not null)
                    throw new InvalidDataException("默认启动基准必须使用空预制身份及匹配的 Sylenth1 配置。");
                preset = null;
            }
            else preset = FxpPreset.Read(request);
            FxpPreset.ValidatePlugin(request.Plugin.PluginPath);
            result = VstRenderer.Render(request, preset);
        }
        catch (Exception error)
        {
            result = new RenderResult { Message = error.Message, NonRetryable = error is PresetStateCompatibilityException };
            Console.Error.WriteLine(error);
        }
        string target = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string staged = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(result, LibraryJson.Options));
        File.Move(staged, target, overwrite: true);
        return result.Success ? 0 : 1;
    }
}
