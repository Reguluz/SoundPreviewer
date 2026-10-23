using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public sealed record InitBaselineState(SynthKind Synth, string Status, string Detail, bool CanCompare,
    bool CanConfirm, string WavPath)
{
    public IReadOnlyList<NotePreview> Notes { get; init; } = [];
    public bool CanRenderWithVerifiedPresetLoad { get; init; }
}

internal sealed record PreparedInitBaseline(InitBaselineState State, InitAudioSignature? Signature);

public static class InitBaselineService
{
    public const string BaselineKind = "FreshInstanceNoPresetLoaded";
    public const string RejectionPrefix = "初始化匹配：";
    public const string SuspectedLoadFailurePrefix = "经过验证疑似加载失败：";
    private const string AudioComparisonMode = "AudioComparison";
    private const string VerifiedPresetLoadMode = "VerifiedPresetLoad";

    public static string DescribeRejection(SynthKind synth, string detail) => synth is SynthKind.Serum1 or SynthKind.Serum2
        ? SuspectedLoadFailurePrefix + "生成结果与未加载预制时的启动声音相同或极近似，本次预览未保存。" +
            "当前 Serum 版本可能低于制作该音色的版本；请在 Serum 中打开该音色查看提示，如要求较新版本，请更新后重试。 " + detail
        : RejectionPrefix + detail;

    public static bool IsRejection(string message) => message.StartsWith(RejectionPrefix, StringComparison.Ordinal) ||
        message.StartsWith(SuspectedLoadFailurePrefix, StringComparison.Ordinal);

    public static string ExplainStoredRejection(SynthKind synth, string message) => synth is SynthKind.Serum1 or SynthKind.Serum2
        ? message.Replace(RejectionPrefix, DescribeRejection(synth, ""), StringComparison.Ordinal) : message;

    public static bool Supports(PluginConfiguration plugin) =>
        plugin.Synth is SynthKind.Serum1 or SynthKind.Serum2 or SynthKind.Sylenth1 && plugin.Backend == RenderBackend.DawDreamer ||
        plugin.Synth == SynthKind.Serum1 && plugin.Backend == RenderBackend.Sushi &&
        PluginSelectionService.CurrentPlatform == PluginPlatform.Windows && PluginSelectionService.CurrentArchitecture == PluginArchitecture.X64 &&
        PluginSelectionService.IsSushiExecutable(plugin.ExecutablePath) ||
        plugin.Synth == SynthKind.Sylenth1 && plugin.Backend == RenderBackend.ExternalWorker &&
        string.Equals(Path.GetFileName(plugin.ExecutablePath), "SoundPreviewer.Vst2Host.exe", StringComparison.OrdinalIgnoreCase);

    public static async Task<InitBaselineState> InspectAsync(AppSettings settings, PluginConfiguration plugin,
        CancellationToken cancellationToken = default) =>
        (await Task.Run(() => Read(settings, plugin, cancellationToken), cancellationToken).ConfigureAwait(false)).State;

    public static async Task<InitBaselineState> GenerateAsync(AppSettings settings, PluginConfiguration plugin,
        string workerPath, CancellationToken cancellationToken = default, Action<int>? processStarted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Supports(plugin)) return Unsupported(plugin);
        string? metadataPath = null, temporaryMetadata = null;
        var createdWavs = new List<string>();
        bool published = false;
        try
        {
            var identity = await Task.Run(() => Identity(settings, plugin, cancellationToken), cancellationToken).ConfigureAwait(false);
            var capturePlugin = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(plugin, LibraryJson.Options), LibraryJson.Options)!;
            capturePlugin.ExecutableHash = identity.ExecutableHash;
            capturePlugin.PluginHash = identity.PluginHash;
            string directory = Path.Combine(Path.GetFullPath(settings.PreviewDirectory), "InitializationBaselines");
            Directory.CreateDirectory(directory);
            string stem = plugin.Synth + "-" + identity.Key[..16] + "-" + Guid.NewGuid().ToString("N");
            metadataPath = Path.Combine(directory, stem + ".json");
            var references = new List<BaselineReference>();
            var signatures = new List<InitAudioSignature>();
            for (int index = 0; index < 3; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string wav = Path.Combine(directory, stem + $"-{index + 1}.wav");
                createdWavs.Add(wav);
                var result = await new WorkerProcessClient().RenderAsync(new RenderRequest
                {
                    IsInitializationBaseline = true,
                    Preset = new PresetRecord { Id = "startup-baseline-" + plugin.Synth, Name = "默认启动基准", Synth = plugin.Synth },
                    Plugin = capturePlugin, Template = settings.Template, OutputPath = wav, CacheKey = identity.Key
                }, workerPath, cancellationToken, null, processStarted).ConfigureAwait(false);
                if (!result.Success) throw new InvalidDataException($"默认启动基准第 {index + 1}/3 次生成失败：" + result.Message);
                cancellationToken.ThrowIfCancellationRequested();
                var audio = WaveFile.Read(wav, cancellationToken);
                RequireAudible(result);
                signatures.Add(InitAudioComparer.CreateSignature(audio, result.Notes, cancellationToken));
                references.Add(new BaselineReference { WavPath = wav, WavSha256 = Fingerprints.HashFile(wav, cancellationToken), Result = result });
            }
            string mode = AudioComparisonMode, stabilityDetail = "";
            try { _ = InitAudioComparer.CreateCalibratedSignature(signatures, cancellationToken); }
            catch (InvalidDataException error) when (PresetLoadVerification.IsSupported(plugin) &&
                references.All(reference => PresetLoadVerification.IsKnownAdapter(reference.Result!.AdapterVersion)) && InitAudioComparer.IsStabilityFailure(error))
            {
                // Random startup oscillators cannot provide a reliable audio reference.
                // This mode never claims audio comparison; every target must instead prove
                // exact VST2 state readback before the queue can commit its preview.
                mode = VerifiedPresetLoadMode;
                stabilityDetail = error.Message;
            }
            if (references.Select(reference => reference.Result!.AdapterVersion).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new InvalidDataException("三次默认启动校准的适配器版本不一致。");
            // A binary may have changed while the isolated renderer was running.
            if ((await Task.Run(() => Identity(settings, plugin, cancellationToken), cancellationToken).ConfigureAwait(false)).Key != identity.Key)
                throw new InvalidDataException("音源或模板在基准生成期间发生变化，请重新生成基准。");
            var metadata = new BaselineMetadata
            {
                Kind = BaselineKind, PolicyVersion = InitAudioComparer.PolicyVersion, Synth = plugin.Synth,
                Mode = mode, StabilityDetail = stabilityDetail,
                Identity = identity.Key, WavPath = references[0].WavPath, WavSha256 = references[0].WavSha256,
                TemplateJson = JsonSerializer.Serialize(Fingerprints.TemplateIdentity(settings.Template), LibraryJson.Options),
                CreatedUtc = DateTime.UtcNow, Result = references[0].Result, References = references
            };
            temporaryMetadata = metadataPath + ".tmp";
            var storedMetadata = JsonSerializer.Deserialize<BaselineMetadata>(JsonSerializer.Serialize(metadata, LibraryJson.Options), LibraryJson.Options)!;
            RewritePaths(storedMetadata, path => PreviewPathStorage.Store(path, settings.PreviewDirectory));
            await File.WriteAllTextAsync(temporaryMetadata, JsonSerializer.Serialize(storedMetadata, LibraryJson.Options), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryMetadata, metadataPath);
            // Publishing the locator is the last non-cancellable step; retain any previous baseline on failure.
            plugin.InitBaselinePath = metadataPath;
            published = true;
            return mode == VerifiedPresetLoadMode ? VerifiedPresetLoadReady(plugin.Synth, metadata) : Ready(plugin.Synth, metadata);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsDiagnostic(error))
        { return new(plugin.Synth, "生成失败", error.Message + " 未启用默认音色保护。", false, false, ""); }
        finally
        {
            if (!published)
            {
                foreach (string wav in createdWavs) DeleteOwned(wav);
                DeleteOwned(metadataPath);
            }
            DeleteOwned(temporaryMetadata);
        }
    }

    internal static async Task<PreparedInitBaseline> PrepareAsync(AppSettings settings, PluginConfiguration plugin,
        string workerPath, CancellationToken cancellationToken, Action<int>? processStarted = null)
    {
        var prepared = await Task.Run(() => Read(settings, plugin, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (prepared.State.CanCompare || prepared.State.CanRenderWithVerifiedPresetLoad || !Supports(plugin)) return prepared;
        var generated = await GenerateAsync(settings, plugin, workerPath, cancellationToken, processStarted).ConfigureAwait(false);
        return generated.CanCompare || generated.CanRenderWithVerifiedPresetLoad
            ? await Task.Run(() => Read(settings, plugin, cancellationToken), cancellationToken).ConfigureAwait(false)
            : new(generated, null);
    }

    private static PreparedInitBaseline Read(AppSettings settings, PluginConfiguration plugin, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Supports(plugin)) return new(Unsupported(plugin), null);
        if (string.IsNullOrWhiteSpace(plugin.InitBaselinePath))
            return new(new(plugin.Synth, "未生成", "尚未生成无预制加载的默认启动基准；生成预览前将自动进行三次独立校准。", false, false, ""), null);
        try
        {
            var identity = Identity(settings, plugin, token);
            if (!File.Exists(plugin.InitBaselinePath) || new FileInfo(plugin.InitBaselinePath).Length > 1024 * 1024)
                throw new InvalidDataException("默认启动基准记录不存在或超过大小限制。");
            var metadata = JsonSerializer.Deserialize<BaselineMetadata>(File.ReadAllText(plugin.InitBaselinePath), LibraryJson.Options)
                ?? throw new InvalidDataException("默认启动基准记录无效。");
            RewritePaths(metadata, path => PreviewPathStorage.Resolve(path, settings.PreviewDirectory));
            if (metadata.Kind != BaselineKind || metadata.PolicyVersion != InitAudioComparer.PolicyVersion ||
                metadata.Synth != plugin.Synth || metadata.Identity != identity.Key ||
                metadata.TemplateJson != JsonSerializer.Serialize(Fingerprints.TemplateIdentity(settings.Template), LibraryJson.Options))
                return new(new(plugin.Synth, "已过期", "音源文件、渲染程序、模板或比对策略已变化，须重建默认启动基准。", false, false, ""), null);
            if (metadata.References is null || metadata.References.Count != 3 || metadata.References.Any(reference => reference is null) ||
                metadata.References.Select(reference => Path.GetFullPath(reference.WavPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
                throw new InvalidDataException("默认启动校准须包含三份独立参考音频。");
            if (metadata.Result is null || !SamePath(metadata.Result.OutputPath, metadata.WavPath) ||
                !SamePath(metadata.WavPath, metadata.References[0].WavPath) || metadata.WavSha256 != metadata.References[0].WavSha256)
                throw new InvalidDataException("默认启动基准音频路径与结果记录不符。");
            var signatures = metadata.References.Select(reference => ReadReference(reference, settings.Template, token)).ToArray();
            if (metadata.References.Select(reference => reference.Result!.AdapterVersion).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new InvalidDataException("默认启动校准的适配器版本不一致。");
            if (metadata.Mode == VerifiedPresetLoadMode)
            {
                if (!PresetLoadVerification.IsSupported(plugin) ||
                    metadata.References.Any(reference => !PresetLoadVerification.IsKnownAdapter(reference.Result!.AdapterVersion)))
                    throw new InvalidDataException("当前宿主不支持逐音区核验目标预制状态，不能使用替代防错检查。");
                bool unstable = false;
                try { _ = InitAudioComparer.CreateCalibratedSignature(signatures, token); }
                catch (InvalidDataException error) when (InitAudioComparer.IsStabilityFailure(error)) { unstable = true; }
                if (!unstable) throw new InvalidDataException("替代防错记录没有可核对的启动声音变化依据，请重新检查。");
                metadata.Result = metadata.References[0].Result;
                return new(VerifiedPresetLoadReady(plugin.Synth, metadata), null);
            }
            if (metadata.Mode != AudioComparisonMode) throw new InvalidDataException("未知的默认启动检查方式，请重新检查。");
            var signature = InitAudioComparer.CreateCalibratedSignature(signatures, token);
            metadata.Result = metadata.References[0].Result;
            return new(Ready(plugin.Synth, metadata), signature);
        }
        catch (Exception error) when (IsDiagnostic(error))
        { return new(new(plugin.Synth, "基准无效", error.Message + " 须重新生成，不能以静音或失效基准判定音色。", false, false, ""), null); }
    }

    private static InitBaselineState Ready(SynthKind synth, BaselineMetadata metadata) =>
        new(synth, "可比对", "已完成三次独立默认启动校准，绑定当前音源与模板；至少匹配两份完整三音区参考才拒绝，保护受固定变化边界限制。", true, false, metadata.WavPath)
        { Notes = metadata.Result!.Notes };

    private static InitBaselineState VerifiedPresetLoadReady(SynthKind synth, BaselineMetadata metadata) =>
        new(synth, "逐音色核验", "默认启动声音存在随机变化，无法可靠进行声音比对。生成时必须在三个音区逐一回读并核对目标预制状态；缺少核验结果将拒绝保存，音色仍须试听确认。 " +
            metadata.StabilityDetail, false, false, metadata.WavPath)
        { Notes = metadata.Result!.Notes, CanRenderWithVerifiedPresetLoad = true };

    private static InitBaselineState Unsupported(PluginConfiguration plugin) => new(plugin.Synth, "不支持此检查",
        plugin.Backend == RenderBackend.Unconfigured ? "渲染器未配置，尚不能生成默认启动基准。" :
        "当前后端不支持此声音检查，但不阻止生成；生成结果须人工试听，未启用默认音色保护。", false, false, "");

    private static (string Key, string ExecutableHash, string PluginHash) Identity(AppSettings settings,
        PluginConfiguration plugin, CancellationToken token)
    {
        settings.Template.Validate();
        if (!File.Exists(plugin.ExecutablePath)) throw new FileNotFoundException("基准渲染程序不存在。", plugin.ExecutablePath);
        if (!File.Exists(plugin.PluginPath) && !Directory.Exists(plugin.PluginPath)) throw new FileNotFoundException("基准音源 VST2 文件不存在。", plugin.PluginPath);
        if (string.IsNullOrWhiteSpace(plugin.Version)) throw new InvalidDataException("基准音源版本未配置。");
        string executableHash = plugin.Backend == RenderBackend.DawDreamer && DawDreamerRuntimeService.IsManagedExecutablePath(plugin.ExecutablePath)
            ? Fingerprints.RendererExecutableHash(plugin, token) : Fingerprints.HashFile(plugin.ExecutablePath, token);
        string pluginHash = Fingerprints.PluginBinaryHash(plugin, settings.DiscoveredPlugins, token);
        if (pluginHash.Length == 0) throw new InvalidDataException("无法核对基准音源的实际二进制指纹。");
        string script = Path.Combine(AppContext.BaseDirectory, "Renderers", "dawdreamer_worker.py");
        string scriptHash = plugin.Backend == RenderBackend.DawDreamer && File.Exists(script) ? Fingerprints.HashFile(script, token) : "";
        string stateAdapter = Path.Combine(AppContext.BaseDirectory, "Renderers", "serum2_state.py");
        string stateAdapterHash = plugin.Synth == SynthKind.Serum2 && plugin.Backend == RenderBackend.DawDreamer && File.Exists(stateAdapter)
            ? Fingerprints.HashFile(stateAdapter, token) : "";
        var sidecars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (plugin.Backend == RenderBackend.ExternalWorker || plugin.Backend == RenderBackend.Sushi)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(plugin.ExecutablePath))!;
            var names = plugin.Backend == RenderBackend.Sushi ? new[] { "sndfile.dll" } :
                new[] { "SoundPreviewer.Vst2Host.dll", "Jacobi.Vst.Core.dll", "Jacobi.Vst.Host.Interop.dll",
                    "Ijwhost.dll", "SoundPreviewer.Vst2Host.runtimeconfig.json", "SoundPreviewer.Vst2Host.deps.json" };
            foreach (string name in names)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(directory, name);
                sidecars.Add(name, File.Exists(path) ? Fingerprints.HashFile(path, token) : "");
            }
        }
        string key = Fingerprints.HashText(JsonSerializer.Serialize(new
        {
            plugin.Synth, plugin.Backend, Executable = ApplicationPaths.PathIdentity(plugin.ExecutablePath),
            Plugin = ApplicationPaths.PathIdentity(plugin.PluginPath), plugin.Version, executableHash, pluginHash,
            Template = Fingerprints.TemplateIdentity(settings.Template), Fingerprints.AdapterVersion, Fingerprints.QuickPreviewPolicy,
            InitAudioComparer.PolicyVersion, Kind = BaselineKind, scriptHash, stateAdapterHash, sidecars
        }));
        return (key, executableHash, pluginHash);
    }

    private static void RequireAudible(RenderResult result)
    {
        if (!result.Success || result.Metrics is null || result.Metrics.HasNonFinite || result.Metrics.IsSilent || result.Notes.Count != 3 ||
            result.Notes.Any(note => note.Metrics is null || note.Metrics.HasNonFinite || note.Metrics.IsSilent || note.Metrics.Rms < 0.00001))
            throw new InvalidDataException("默认启动基准存在静音、极低响度或无效音区，不能用于自动拒绝音色。");
    }

    private static InitAudioSignature ReadReference(BaselineReference reference, PreviewTemplate template, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(reference.WavPath) || reference.WavSha256 != Fingerprints.HashFile(reference.WavPath, token))
            throw new InvalidDataException("默认启动参考音频缺失或校验不符。");
        if (reference.Result is null || !SamePath(reference.Result.OutputPath, reference.WavPath))
            throw new InvalidDataException("默认启动参考音频路径与结果记录不符。");
        var audio = WaveFile.Read(reference.WavPath, token);
        reference.Result.Metrics = WaveFile.Analyze(audio, token);
        if (audio.SampleRate != template.SampleRate) throw new InvalidDataException("默认启动参考采样率与模板不符。");
        WorkerProcessClient.ValidateNotes(reference.Result, template);
        foreach (var note in reference.Result.Notes)
        {
            token.ThrowIfCancellationRequested();
            int first = checked((int)Math.Round(note.StartSeconds * audio.SampleRate));
            int frames = checked((int)Math.Round(note.DurationSeconds * audio.SampleRate));
            note.Metrics = WaveFile.Analyze(new WaveData { SampleRate = audio.SampleRate, Channels = audio.Channels,
                Samples = audio.Samples.AsSpan(first * audio.Channels, frames * audio.Channels).ToArray() }, token);
        }
        RequireAudible(reference.Result);
        return InitAudioComparer.CreateSignature(audio, reference.Result.Notes, token);
    }

    private static bool SamePath(string first, string second) => ApplicationPaths.SamePath(first, second);

    private static void RewritePaths(BaselineMetadata metadata, Func<string, string> rewrite)
    {
        metadata.WavPath = rewrite(metadata.WavPath);
        if (metadata.Result is not null) metadata.Result.OutputPath = rewrite(metadata.Result.OutputPath);
        if (metadata.References is null) return;
        foreach (var reference in metadata.References)
        {
            if (reference is null) continue;
            reference.WavPath = rewrite(reference.WavPath);
            if (reference.Result is not null) reference.Result.OutputPath = rewrite(reference.Result.OutputPath);
        }
    }

    private static bool IsDiagnostic(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or JsonException or InvalidOperationException or OverflowException or NotSupportedException;

    private static void DeleteOwned(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class BaselineMetadata
    {
        public BaselineMetadata() { }
        public string Kind { get; set; } = "";
        public string PolicyVersion { get; set; } = "";
        public string Mode { get; set; } = AudioComparisonMode;
        public string StabilityDetail { get; set; } = "";
        public SynthKind Synth { get; set; }
        public string Identity { get; set; } = "";
        public string WavPath { get; set; } = "";
        public string WavSha256 { get; set; } = "";
        public string TemplateJson { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public RenderResult? Result { get; set; }
        public List<BaselineReference> References { get; set; } = [];
    }

    private sealed class BaselineReference
    {
        public BaselineReference() { }
        public string WavPath { get; set; } = "";
        public string WavSha256 { get; set; } = "";
        public RenderResult? Result { get; set; }
    }
}
