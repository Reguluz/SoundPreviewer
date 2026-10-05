using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Worker;

string? requestPath = null, resultPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--request" && i + 1 < args.Length) requestPath = args[++i];
    else if (args[i] == "--result" && i + 1 < args.Length) resultPath = args[++i];
    else { Console.Error.WriteLine("Usage: SoundPreviewer.Worker --request <file.json> --result <file.json>"); return 2; }
}
if (requestPath is null || resultPath is null)
{
    Console.Error.WriteLine("Usage: SoundPreviewer.Worker --request <file.json> --result <file.json>");
    return 2;
}

var result = new RenderResult();
try
{
    if (new FileInfo(requestPath).Length > 1024 * 1024) throw new InvalidDataException("请求文件超过 1 MB 限制。");
    var request = JsonSerializer.Deserialize<RenderRequest>(await File.ReadAllTextAsync(requestPath), LibraryJson.Options)
        ?? throw new InvalidDataException("请求 JSON 无效。");
    request.Template.Validate();
    if (request.Preset.Synth != request.Plugin.Synth) throw new InvalidDataException("音源配置与预制不一致。");
    result = await RenderDispatcher.RenderAsync(request, CancellationToken.None);
}
catch (Exception error)
{
    result.Message = error.Message;
}

try
{
    string destination = Path.GetFullPath(resultPath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result, LibraryJson.Options));
    File.Move(temporary, destination, overwrite: true);
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 3;
}
return result.Success ? 0 : 1;
